# PGD 并行对比：TaskGraph native kernel vs C# 高性能 Task（同一任务）

**TL;DR**：如果目标是“极高性能 + UE 内一致调度 + 可控 barrier”，优先用 **TaskGraph + native kernel**；C# 的 `Parallel.For/Task.Run` 适合作为快速验证/对照基线，但在 UE 内会与引擎线程池竞争，且不掌握 ECS 语义边界。

---

## 1) 问题背景（你现在遇到的真实痛点）

你要对 **同一段连续内存的遍历处理** 做对比：  
同样的数据、同样的循环体、同样的任务划分策略，只换“并行后端”：

- 路线 A：**UE TaskGraph + native kernel**
- 路线 B：**C# 自带的高性能 Task（`Parallel.For` / `Task.Run`）**

**前提**：这类任务不能触碰 UE UObject；只做纯计算/原地写回。

---

## 2) 统一的工作负载（确保公平）

这里用一个最小但真实的负载作为对比基线：

```
int[] 数据被替换为 NativeBuffer<int>
for i in [0..N):
  data[i] += 1
  sum += data[i]
```

- 数据来源：`NativeBuffer<int>`（native-backed）
- 处理方式：原地写回
- 结果：返回 sum（用于验证正确性）

---

## 3) 方案 A：TaskGraph + native kernel（worker 不进 Mono）

### A.1 思路

- C# 只负责：分配 `NativeBuffer<int>`，传 `Ptr/Length` 给 internal call
- C++ 负责：TaskGraph dispatch，多任务并行执行 native kernel
- worker 上不调用任何托管代码（不 attach Mono）

### A.2 C# 调用示例（基于本仓库实现）

```csharp
// Script.Library.NativeBufferTaskGraphDemo.RunInt32Parallel()
using var buf = new NativeBuffer<int>(length: 100_000);
var span = buf.AsSpan();
for (var i = 0; i < span.Length; i++) span[i] = i;

var taskCount = Math.Min(Environment.ProcessorCount, 16);
var sum = FNativeBufferTaskGraphImplementation
    .FNativeBufferTaskGraph_AddOneAndSumInt32ParallelImplementation(buf.Ptr, buf.Length, taskCount);

Console.WriteLine($"sum={sum} first={span[0]} last={span[^1]}");
```

### A.3 C++ 处理示例（片段）

```cpp
// FNativeBufferTaskGraph.cpp
int32* Data = static_cast<int32*>(const_cast<void*>(InData));
const int32 ChunkSize = (InLength + SafeTaskCount - 1) / SafeTaskCount;

for (int32 TaskIndex = 0; TaskIndex < SafeTaskCount; ++TaskIndex)
{
  const int32 Start = TaskIndex * ChunkSize;
  const int32 End = FMath::Min(Start + ChunkSize, InLength);

  FFunctionGraphTask::CreateAndDispatchWhenReady([Data, Start, End, TaskIndex, &PartialSums]()
  {
    int64 LocalSum = 0;
    for (int32 i = Start; i < End; ++i) { Data[i] += 1; LocalSum += Data[i]; }
    PartialSums[TaskIndex] = LocalSum;
  });
}
```

### A.4 特性与代价

- ✅ UE 内统一调度（和渲染/物理/其他 TaskGraph 任务共享资源）
- ✅ worker 不进 Mono（规避 PIE attach 风险）
- ✅ 语义边界可控（PGD 负责 chunk/section，UE 负责调度）
- ⚠️ 需要 native kernel（需要 C++ 代码/生成能力）

---

## 4) 方案 B：C# 高性能 Task（`Parallel.For` / `Task.Run`）

### B.1 思路

- C# 直接用 ThreadPool 并行
- 仍使用同一个 `NativeBuffer<int>`，但必须用 `unsafe` 指针访问
- 不能使用 `Span<T>` 跨线程捕获

### B.2 `Parallel.For` 示例（推荐作为对照基线）

```csharp
using System.Threading.Tasks;
using System.Threading;

public static unsafe long RunWithParallelFor(NativeBuffer<int> buf)
{
    int len = buf.Length;
    int* data = (int*)buf.Ptr;
    long sum = 0;

    Parallel.For(0, len,
        () => 0L,
        (i, _, local) =>
        {
            data[i] += 1;
            return local + data[i];
        },
        local => Interlocked.Add(ref sum, local));

    return sum;
}
```

### B.3 `Task.Run` + 手动分片示例

```csharp
using System.Threading.Tasks;

public static unsafe long RunWithTasks(NativeBuffer<int> buf, int taskCount)
{
    int len = buf.Length;
    int* data = (int*)buf.Ptr;
    int chunk = (len + taskCount - 1) / taskCount;

    var tasks = new Task<long>[taskCount];
    for (int t = 0; t < taskCount; t++)
    {
        int start = t * chunk;
        int end = Math.Min(start + chunk, len);
        tasks[t] = Task.Run(() =>
        {
            long local = 0;
            for (int i = start; i < end; i++) { data[i] += 1; local += data[i]; }
            return local;
        });
    }

    Task.WaitAll(tasks);
    long sum = 0;
    for (int i = 0; i < tasks.Length; i++) sum += tasks[i].Result;
    return sum;
}
```

### B.4 特性与代价

- ✅ 实现成本低，迭代快
- ✅ 不需要 C++，更适合快速对照/验证
- ⚠️ ThreadPool 与 UE 任务系统竞争（可能造成 oversubscription）
- ⚠️ 缺少 ECS 语义边界控制（barrier/结构变更需要你自己约束）

---

## 5) 两种方案的性能/工程权衡

| 维度 | TaskGraph + native kernel | C# Parallel/Task |
| --- | --- | --- |
| 调度体系 | UE TaskGraph（引擎内统一） | .NET ThreadPool（引擎外） |
| ECS 语义边界 | 易对齐 PGD chunk/section + barrier | 需要自建约束 |
| 对 UE 负载协同 | 更好（同一调度体系） | 可能抢占/干扰 |
| 实现成本 | 需要 C++ kernel | 只写 C# |
| 风险点 | native 生命周期与同步 | ThreadPool 争用、粒度失控 |

---

## 6) 推荐结论（面向极高性能）

- **长期主线**：TaskGraph + native kernel，PGD 控制粗粒度（chunk/section/阈值），UE 负责调度。  
- **短期对照**：C# `Parallel.For` 作为基线/验证工具，但不要让它成为最终并行后端。

## The Golden Rule

**PGD 负责“怎么切（语义边界 + 阈值 + barrier）”，UE 负责“怎么跑（线程调度）”。**

