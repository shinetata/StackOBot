# PGD × UE TaskGraph：Native Kernel 方案设计、测试验证与落地改动（StackOBot 基准）

**TL;DR**：在 StackOBot + UnrealCSharp（Mono）环境里，如果目标是“PGD 这类纯 ECS 数据遍历（不碰 UObject）”获得可重复的并行收益，同时避开 TaskGraph worker 进入 Mono 带来的 PIE 稳定性风险，当前最可靠的路径是：**C# 只做切分与数据准备，TaskGraph worker 只跑 C++ native kernel**，数据通过 `NativeBuffer<T>`（native-backed）零拷贝共享。

本文把“PGD 结合 TaskGraph”相关的方案设计、测试设计（含结果）、问题与结论统一整理到一份文档里，并以仓库现有实现为唯一依据，不做超出代码与日志的推断式承诺。

---

## 0. 范围与约束（先把边界说清楚）

### 0.1 目标 workload（刻意收敛）

- 模拟 PGD `IQuery.ForeachEntity` 这类典型热路径：**遍历连续组件数组，原地写回，顺便做一个可校验的 reduction（sum）**。
- **不**涉及：跨帧生命周期、UE UObject 交互、结构性变更（增删组件/迁移 archetype）、随机访问数据结构。

### 0.2 本文回答的问题

1) “把 C# 托管代码跑在 TaskGraph worker 上”有哪些方式、各自会遇到什么问题？  
2) “TaskGraph Native Kernel”方案到底怎么做（数据层/调度层/绑定层），它的测试方案是否公允，结果如何？  
3) 如果走 Native Kernel 方案，PGD 需要改哪些模块（只概括模块与方向，不在本文展开到可直接改 PGD 源码的粒度）。

---

## 1. 两种“用 TaskGraph 运行 C# 托管代码”的方式，以及它们的问题

这里的“运行托管代码”，指的是：**TaskGraph 的 worker OS 线程进入 Mono 运行 C# 方法**。

### 1.1 方式 A：TaskGraph worker attach Mono，直接 `Runtime_Invoke` 托管方法

仓库里已有可运行 PoC / 基础设施：

- C++：`Plugins/UnrealCSharp/Source/UnrealCSharp/Private/Domain/InternalCall/FTaskGraph.cpp`
  - 关键点：worker task 内调用 `FMonoDomain::EnsureThreadAttached()`，随后 `FMonoDomain::Runtime_Invoke(...)` 执行 C# 方法。
- C#：`Plugins/UnrealCSharp/Script/UE/Library/TaskGraphProbe.cs`
  - 关键点：`TaskGraphProbe.OnWorker(...)` 在 worker 线程执行，打印 native thread id 与 managed thread id。

关键代码链路（C++，节选，强调“worker 进入 Mono”这一步确实发生在 TaskGraph task 内）：

```cpp
// FTaskGraph.cpp (节选)
Events.Add(FFunctionGraphTask::CreateAndDispatchWhenReady([StateHandle, Index, InExecuteTaskMethod]()
{
    FMonoDomain::EnsureThreadAttached();
    (void)FMonoDomain::Runtime_Invoke(InExecuteTaskMethod, nullptr, Params, &Exception);
    // ...
}, TStatId(), nullptr, ENamedThreads::AnyBackgroundThreadNormalTask));
```

执行链路（简化）：

```
+------------------------------+
| C# (GameThread)              |
| - internal call enqueue      |
+---------------+--------------+
                |
                v
+---------------+--------------+
| UE TaskGraph worker          |
| - EnsureThreadAttached()     |
| - Runtime_Invoke(C# method)  |
+------------------------------+
```

已知问题（只列“必须正视”的点）：

- **PIE 稳定性风险**：worker 进入 Mono 后，域卸载/重载、线程 attach/detach 的生命周期边界更复杂；历史上已出现“二次 PIE 卡死/挂起”的定位结论。
- **线程亲和性与生命周期管理困难**：TaskGraph 只保证“某个 worker 执行某个 task”，不保证“同一个 OS 线程贯穿一个托管线程生命周期”。一旦需要严格配对的 detach/cleanup，就会变成隐患来源。
- **性能热路径的跨语言开销**：即使缓存 `MonoMethod*`，`Runtime_Invoke` 仍是桥接路径；如果任务粒度小（chunk/section 很细），跨语言调用会吞掉并行收益。

结论：方式 A 在功能上“能跑”，但**稳定性与性能都很难满足 PGD 热路径诉求**。它更适合作为“可行性证明/功能兜底”，不适合作为极致性能主线。

补充阅读（仓库现有讨论沉淀）：

- `docs/tech/unrealcsharp/unrealcsharp-taskgraph-parallel-csharp.md`
- `docs/tech/unrealcsharp/unrealcsharp-taskgraph-worker-mono-attach-pie-freeze.md`
- `docs/tech/pgd/pgd-paralleljob-to-ue-taskgraph-mapping.md`

### 1.2 方式 B：TaskGraph worker 不进入 Mono；C# 只准备数据与切分，worker 跑 C++ kernel

这就是本文的主线方案（Native Kernel）。执行链路（简化）：

```
C#：准备 NativeBuffer<T> + 生成 slices
  |
  | internal call (Ptr/Length/Desc buffers)
  v
UE TaskGraph worker：只跑 C++ tight loop
  |
  v
C#：读回/校验结果（可选）
```

核心收益点不是“TaskGraph 更神奇”，而是：**让 worker 避开 Mono**，把热循环落到 native（可被编译器更充分优化），同时让并行调度纳入 UE 自己的调度体系。

---

## 2. TaskGraph Native Kernel 方案：设计与实现（以仓库当前代码为准）

### 2.1 数据层：`NativeBuffer<T>`（native-backed，unmanaged-only）

代码位置：`Plugins/UnrealCSharp/Script/UE/Library/NativeBuffer.cs`

设计目标很直接：

- C# 能持有一块**连续 native 内存**（不走 GC）。
- C# 侧用 `Span<T>` 当“视图”读写；native 侧用 `Ptr` 当“事实指针”读写。
- 限制 `T : unmanaged`，保证 native 侧可以安全解释为 `T*`。

关键接口（节选）：

```csharp
public unsafe sealed class NativeBuffer<T> : IDisposable where T : unmanaged
{
    public int Length { get; }
    public int Capacity { get; }
    public uint Version { get; }
    public nint Ptr { get; }

    public NativeBuffer(int length, bool clear = true);
    public Span<T> AsSpan();
    public void Dispose();
}
```

关于初始化（也是最容易被误读的点）：

```csharp
using var buf = new NativeBuffer<int>(length: length);
var span = buf.AsSpan();
for (var i = 0; i < span.Length; i++) span[i] = i;
```

- `span` 这个局部变量**就是用来给 `NativeBuffer` 的内存赋初值**（以及后续可能做调试读写）。
- 这段代码的意义不在 “要不要用 span”，而在于：**让三条对比路径在同一份数据上做同一件事**。
- 重要约束：不要缓存 `Span<T>` 到字段或跨越可能 `Resize/Dispose` 的边界（原因见 `docs/tech/unrealcsharp/unrealcsharp-nativebuffer-internalcall-minimal-loop.md`）。

### 2.2 绑定层：internal call（C# → UE C++）

Native Kernel 的关键是：C# 把 `Ptr/Length`（以及描述信息 buffer）交给 C++，C++ 在 TaskGraph 上并行处理。

仓库当前有两组 kernel：

1) 单数组 `AddOneAndSumInt32`（验证最小闭环与基础收益）  
2) 多 archetype、多 slice 的 `pos += vel * dt`（模拟 PGD `IQuery.ForeachEntity` 的真实形态）

### 2.3 调度层（C++）：TaskGraph 并行分发 + wait

#### 2.3.1 单数组基准：`AddOneAndSumInt32ParallelNoLog`

- C++：`Plugins/UnrealCSharp/Source/UnrealCSharp/Private/Domain/InternalCall/FNativeBufferTaskGraph.cpp`
- C# 调用封装：`Plugins/UnrealCSharp/Script/UE/Library/TaskGraphNativeKernelPerf.cs`

核心逻辑：按 `taskCount` 均分区间，每个区间一个 `FFunctionGraphTask`，最终 `WaitUntilTasksComplete`，然后在调用线程上汇总 `PartialSums`。

```cpp
// FNativeBufferTaskGraph.cpp (核心结构)
const int32 ChunkSize = (InLength + SafeTaskCount - 1) / SafeTaskCount;
for (int32 TaskIndex = 0; TaskIndex < SafeTaskCount; ++TaskIndex)
{
    const int32 Start = TaskIndex * ChunkSize;
    const int32 End = FMath::Min(Start + ChunkSize, InLength);

    Events.Add(FFunctionGraphTask::CreateAndDispatchWhenReady(
        [Data, Start, End, TaskIndex, &PartialSums]()
        {
            int64 LocalSum = 0;
            for (int32 i = Start; i < End; ++i) { Data[i] += 1; LocalSum += Data[i]; }
            PartialSums[TaskIndex] = LocalSum;
        },
        TStatId(), nullptr, ENamedThreads::AnyBackgroundThreadNormalTask));
}
FTaskGraphInterface::Get().WaitUntilTasksComplete(Events, ...);
```

#### 2.3.2 ECS 模拟：多 archetype + 多 slice（更贴近 PGD Query）

- C++：`Plugins/UnrealCSharp/Source/UnrealCSharp/Private/Domain/InternalCall/FNativeBufferTaskGraphEcs.cpp`
- C# 数据准备/切分：`Plugins/UnrealCSharp/Script/UE/Library/EcsArchetypePerf.cs`

数据结构（C# 与 C++ 字段顺序必须一致，顺序布局）：

```csharp
// EcsArchetypePerf.cs
[StructLayout(LayoutKind.Sequential)]
public struct ArchetypeDesc { public nint Position; public nint Velocity; public int Length; }

[StructLayout(LayoutKind.Sequential)]
public struct SliceDesc { public int ArchetypeIndex; public int Start; public int Length; }
```

切分策略（C#）：对每个 archetype 生成 1~N 个 slice；`minParallelChunkSize` 用来避免小数组切太碎（对应 PGD 的 `MIN_PARALLEL_CHUNK_SIZE` 思路）。

执行（C++）：每个 slice 一个 TaskGraph task，task 内只做 tight loop：

```cpp
// FNativeBufferTaskGraphEcs.cpp (核心结构)
for (int32 SliceIndex = 0; SliceIndex < InSliceCount; ++SliceIndex)
{
    Events.Add(FFunctionGraphTask::CreateAndDispatchWhenReady(
        [Archetypes, Slices, SliceIndex, &PartialSums]()
        {
            const FSliceDesc& Slice = Slices[SliceIndex];
            const FArchetypeDesc& Arch = Archetypes[Slice.ArchetypeIndex];
            for (int32 i = Slice.Start; i < End; ++i)
            {
                Arch.Position[i] += Arch.Velocity[i] * InDt;
                LocalSum += Arch.Position[i];
            }
            PartialSums[SliceIndex] = LocalSum;
        },
        TStatId(), nullptr, ENamedThreads::AnyBackgroundThreadNormalTask));
}
FTaskGraphInterface::Get().WaitUntilTasksComplete(Events, ...);
```

---

## 3. 性能测试设计（为什么说它“尽量公允”）

这部分完全基于仓库现有 runner 的实现与日志字段，不额外“脑补”。

### 3.1 三条对比路径（同一 workload）

1) `TG`：TaskGraph + native kernel（C++ tight loop）  
2) `PF`：C# `Parallel.For` + chunk（`MaxDegreeOfParallelism=taskCount`）  
3) `TR`：C# `Task.Run` + chunk + `Task.WaitAll`

对应代码：

- Runner（交错顺序 + 中位数）：  
  - `Plugins/UnrealCSharp/Script/UE/Library/TaskGraphVsCSharpPerfRunner.cs`  
  - `Plugins/UnrealCSharp/Script/UE/Library/TaskGraphVsCSharpEcsPerfRunner.cs`
- C# 对照实现：`Plugins/UnrealCSharp/Script/UE/Library/CSharpParallelPerf.cs`
- TaskGraph kernel：  
  - `Plugins/UnrealCSharp/Source/UnrealCSharp/Private/Domain/InternalCall/FNativeBufferTaskGraph.cpp`  
  - `Plugins/UnrealCSharp/Source/UnrealCSharp/Private/Domain/InternalCall/FNativeBufferTaskGraphEcs.cpp`

对照组的“关键实现细节”（避免误读为 `Span<T>` 边界检查导致慢）：

- `PF`/`TR` 都是通过 `int* data = (int*)buf.Ptr;` 直接指针写入同一块 native 内存（见 `Plugins/UnrealCSharp/Script/UE/Library/CSharpParallelPerf.cs` 与 `Plugins/UnrealCSharp/Script/UE/Library/EcsArchetypePerf.cs`）。

`Parallel.For` + chunk（节选）：

```csharp
// CSharpParallelPerf.cs (节选)
Parallel.For(0, taskCount, options,
    () => 0L,
    (taskIndex, _, local) =>
    {
        for (var i = start; i < end; i++) { data[i] += 1; local += data[i]; }
        return local;
    },
    local => Interlocked.Add(ref sum, local));
```

`Task.Run` + chunk（节选）：

```csharp
// CSharpParallelPerf.cs (节选)
tasks[taskIndex] = Task.Run(() =>
{
    long local = 0;
    for (var i = start; i < end; i++) { data[i] += 1; local += data[i]; }
    return local;
});
Task.WaitAll(tasks);
```

### 3.2 公允性手段（当前代码已经做到了哪些）

- **同一份数据来源**：三条路径都操作 `NativeBuffer<T>` 的同一块 native 内存（非 `T[]`）。
- **同一切分策略**：都按 chunk/slice 范围处理，区间 `[start,end)` 一致。
- **预热 warmup**：减少首轮 JIT/线程池爬坡的影响。
- **交错顺序**：每轮以 `TG->PF->TR / PF->TR->TG / TR->TG->PF` 交错，降低“谁先跑谁吃亏/占便宜”的偏置。
- **中位数**：每组参数跑 `rounds` 轮取 median，压制离群波动。
- **结果等价校验**：每轮 `sumOk=True`（三条路径 sum 完全一致），避免“逻辑不同导致性能结论误导”。

Runner 的实现方式（节选，强调“交错顺序 + median + sumOk”都落在代码里，而不是口头描述）：

```csharp
// TaskGraphVsCSharpPerfRunner.cs / TaskGraphVsCSharpEcsPerfRunner.cs (节选)
var orderIndex = r % 3; // TG->PF->TR / PF->TR->TG / TR->TG->PF
// ...
var sumOk = sumTaskGraph == sumParallelFor && sumTaskGraph == sumTaskRun;
var taskGraphMedian = Median(taskGraphMs);
```

仍然存在的不可控因素：

- .NET/Mono ThreadPool 动态爬坡、UE 同帧其他后台任务、OS 调度抖动等会造成个别 round 的离群值；runner 用 median 已尽量抑制，但无法彻底消除。
- 这里衡量的是“端到端实现”性能：包含调度 + 执行 + reduction，而不是只量 tight loop 的纯算术吞吐。

---

## 4. 测试结果（来自当前日志；以表格整理）

完整方案与结果汇总文档：`docs/tech/taskgraph/taskgraph-performance-verification-summary.md`。下面把关键表格同步到本文，避免在多个文档来回跳转。

### 4.1 方案一：单数组 `AddOneAndSumInt32`（length=100000）

固定参数：`length=100000, taskCount=8, iterations=32, warmup=3, rounds=5`

| 运行 | tgMedian(ms) | pfMedian(ms) | trMedian(ms) | ratioPf | ratioTr |
| --- | --- | --- | --- | --- | --- |
| 1 | 1.044 | 1.803 | 2.016 | 1.727 | 1.931 |
| 2 | 0.948 | 1.966 | 1.857 | 2.075 | 1.960 |
| 3 | 0.781 | 1.786 | 2.272 | 2.288 | 2.910 |
| 4 | 0.805 | 1.770 | 1.953 | 2.197 | 2.425 |
| 5 | 0.609 | 2.257 | 2.410 | 3.704 | 3.955 |

### 4.2 方案二：ECS 多 archetype（固定长度）

archetype 长度：`[120000, 35000, 9000, 2500]`  
固定参数：`taskCount=8, iterations=32, warmup=3, rounds=5, minChunk=10000, dt=1`

| 运行 | tgMedian(ms) | pfMedian(ms) | trMedian(ms) | ratioPf | ratioTr |
| --- | --- | --- | --- | --- | --- |
| 1 | 1.383 | 3.184 | 4.468 | 2.302 | 3.230 |
| 2 | 1.494 | 3.895 | 3.510 | 2.607 | 2.349 |
| 3 | 1.330 | 3.103 | 3.125 | 2.333 | 2.350 |
| 4 | 1.261 | 3.333 | 3.413 | 2.644 | 2.707 |
| 5 | 1.626 | 3.377 | 3.013 | 2.077 | 1.853 |

### 4.3 方案三：ECS 参数扫测（taskCount × minChunk）

扫测参数：

- `taskCount = [4, 8, 16]`
- `minParallelChunkSize = [5000, 10000, 20000]`
- `iterations=32, warmup=3, rounds=5, dt=1`

入口（MainMenu BeginPlay 已接入）：`Script/Game/Game/StackOBot/UI/MainMenu/MainMenu_C.cs`

Sweep Run A（9 组组合）：

| taskCount | minChunk | tgMedian(ms) | pfMedian(ms) | trMedian(ms) | ratioPf | ratioTr |
| --- | --- | --- | --- | --- | --- | --- |
| 4 | 5000 | 0.945 | 4.580 | 4.576 | 4.845 | 4.840 |
| 4 | 10000 | 0.865 | 4.576 | 3.539 | 5.288 | 4.090 |
| 4 | 20000 | 0.690 | 4.681 | 3.546 | 6.789 | 5.142 |
| 8 | 5000 | 0.898 | 2.780 | 3.087 | 3.094 | 3.436 |
| 8 | 10000 | 0.623 | 2.904 | 3.197 | 4.663 | 5.134 |
| 8 | 20000 | 0.586 | 3.587 | 3.662 | 6.123 | 6.252 |
| 16 | 5000 | 0.796 | 2.935 | 3.886 | 3.686 | 4.880 |
| 16 | 10000 | 0.927 | 3.701 | 4.361 | 3.994 | 4.705 |
| 16 | 20000 | 0.564 | 3.773 | 4.675 | 6.687 | 8.286 |

Sweep Run B（9 组组合）：

| taskCount | minChunk | tgMedian(ms) | pfMedian(ms) | trMedian(ms) | ratioPf | ratioTr |
| --- | --- | --- | --- | --- | --- | --- |
| 4 | 5000 | 0.667 | 4.335 | 4.831 | 6.495 | 7.238 |
| 4 | 10000 | 0.623 | 4.710 | 4.488 | 7.566 | 7.209 |
| 4 | 20000 | 0.601 | 4.518 | 3.797 | 7.523 | 6.322 |
| 8 | 5000 | 1.047 | 2.729 | 3.228 | 2.607 | 3.083 |
| 8 | 10000 | 0.902 | 3.443 | 3.637 | 3.817 | 4.032 |
| 8 | 20000 | 0.539 | 3.550 | 3.525 | 6.586 | 6.540 |
| 16 | 5000 | 1.142 | 3.438 | 3.419 | 3.012 | 2.995 |
| 16 | 10000 | 0.819 | 3.744 | 3.527 | 4.572 | 4.307 |
| 16 | 20000 | 0.565 | 3.579 | 3.540 | 6.332 | 6.263 |

---

## 5. 这些结果说明什么？是否具备参考意义？

只基于“当前测试代码 + 当前日志”，可以给出以下结论（不做价值倾向）：

1) 对于“纯数据遍历 + 原地写回 + chunk/slice 并行”这一类 workload：  
   **TaskGraph + native kernel 在本仓库/本环境下稳定更快**，优势区间大致落在 2x~8x（随粒度组合波动）。
2) `sumOk=True` 意味着：三条路径做的是同一件事（至少在该 workload 的可观测结果上等价），因此“不是逻辑差异导致的虚假性能”。
3) 这些结果的参考意义是“面向 PGD 的 `IQuery.ForeachEntity` 类似场景”而不是“所有 ECS workload”：
   - 如果 workload 包含大量结构变更、复杂依赖链、跨帧 handle/barrier、与 UE 对象交互，这份基准不能直接外推。

### 5.1 为什么 TG 这条路会更快（从代码链路能确认的收益点）

下面只列“从当前实现能落到具体链路”的点：

- **worker 不进入 Mono**：Native Kernel 路径的 worker task 不调用 `EnsureThreadAttached/Runtime_Invoke`，避开了托管运行时进入成本与不确定性。
- **更低的调度/封装开销**：
  - `Parallel.For` 需要执行一组委托、TLS 初始化、最终聚合（`Interlocked.Add`）等；
  - `Task.Run` 每轮都会创建 `Task<long>[]` 并调度/等待（这是本基准里最容易“输到离谱”的路径）；
  - TaskGraph native kernel 虽然也要创建 `FFunctionGraphTask`，但 tight loop 在 C++，且 reduction 通过写入 `PartialSums[taskIndex]` 避免原子汇总。
- **避免“双线程池”竞争的典型陷阱**：C# 的并行走 .NET/Mono ThreadPool；UE 也有 TaskGraph worker。两套线程池并存时容易 oversubscription（同核竞争、上下文切换、缓存抖动），Editor 环境更明显。Native Kernel 方案把并行完全纳入 UE TaskGraph 的 worker 体系，至少避免了“再额外拉一套线程池”。

### 5.2 TaskGraph 在这里到底起了什么作用？和“普通 C++ 并行”有什么区别？

在当前实现里，TaskGraph 的作用是明确且可定位的：

- 使用 `FFunctionGraphTask::CreateAndDispatchWhenReady(..., ENamedThreads::AnyBackgroundThreadNormalTask)` 创建 TaskGraph task。
- 使用 `FTaskGraphInterface::Get().WaitUntilTasksComplete(Events, ...)` 等待一批 task 完成。

与常见“普通 C++ 并行”相比（例如自己起线程、用 `FQueuedThreadPool`、或第三方线程池）：

- **调度体系统一**：TaskGraph task 与引擎内其他 task 共享同一套 worker/优先级/统计体系，减少“另起炉灶”的资源争用。
- **可扩展到依赖图**：TaskGraph 支持 prerequisites，可以把“本帧系统 A/B/C 的依赖链”显式表达为图；而当前基准只是“独立任务 + join”，尚未用到图依赖能力，但能力存在。

换句话说：当前 Native Kernel 基准已经“确实在用 TaskGraph”，但属于 TaskGraph 能力的最小子集；未来要对齐 PGD 的 `JobHandle`（跨系统依赖、barrier 收敛），依赖图能力才会成为主角。

---

## 6. 如果走 Native Kernel 方案：先看 Unity ECS 如何做“双存储”，再推导 PGD 改动模块

这一节的写法刻意是“先讲 Unity ECS 的处理方式（事实）→ 再讲 PGD 需要改动什么（推导）”。因为“PGD 会出现两种底层存储结构会不会有问题”这个问题，本质上要先回答：**Unity DOTS Entities 是否就是这样做的，以及它靠什么避免冲突**。

更细的评估文档仍然建议配套阅读：

- `docs/tech/pgd/pgd-route-b-native-kernel-assessment.md`
- `docs/tech/pgd/pgd-align-unity-ecs-managed-unmanaged-storage.md`

### 6.1 Unity ECS（DOTS Entities）对 managed/unmanaged 双存储的处理方式（交叉验证）

#### 6.1.1 存储结构：unmanaged 在 chunk；managed 不在 chunk，而在 World 级 store

Unity Entities 1.3.2 的随包文档明确写到（这是最重要的“公开证据”之一）：

- 本地源码包：`C:\WorkSpace\GitHub\PGDCS\Test\Entities101\Library\PackageCache\com.unity.entities@1.3.2`
- 文档：`...\Documentation~\components-managed.md`

关键结论（原文语义，略去无关段落）：

```
Unlike unmanaged components, Unity doesn't store managed components directly in chunks.
Instead, Unity stores them in one big array for the whole World.
Chunks then store the array indices of the relevant managed components.
```

这句话把 Unity 的“二套存储结构”说得非常直白：

- unmanaged component：真实数据按 archetype chunk 连续存储（SoA + chunk）。
- managed component：真实对象集中存在 World 的“一个大数组/大容器”里；chunk 只存 **int 索引**。

#### 6.1.2 访问方式：chunk 里拿 `int[]`（索引数组）+ store 里拿 `object[]`（真实对象）

这个机制在源码里对应两个关键点：

1) `ManagedComponentStore`（World 级存储）
   - 源码：`...\Unity.Entities\ManagedComponentStore.cs`
   - 核心字段：`internal object[] m_ManagedComponentData`

2) `ManagedComponentAccessor<T>`（chunk 级访问器）
   - 源码：`...\Unity.Entities\Iterators\ArchetypeChunkArray.cs`
   - 核心字段：`NativeArray<int> m_IndexArray` + `ManagedComponentStore m_ManagedComponentStore`

访问器的关键逻辑（节选）：

```csharp
// ArchetypeChunkArray.cs (节选)
public unsafe struct ManagedComponentAccessor<T> where T : class
{
    NativeArray<int> m_IndexArray;
    ManagedComponentStore m_ManagedComponentStore;

    public unsafe T this[int index]
    {
        get
        {
            // we can not cache m_ManagedComponentData directly since it can be reallocated
            return (T)m_ManagedComponentStore.m_ManagedComponentData[m_IndexArray[index]];
        }
    }
}
```

这里有两个对 PGD 很关键的工程含义：

- managed 的“chunk 局部性”只体现在 **索引数组**，不是对象本体；访问真实对象必然多一次 indirection。
- Unity 明确提示：`m_ManagedComponentData` 可能会 reallocate，所以不要缓存底层数组引用（这和我们在 `NativeBuffer<T>` 上强调“不缓存 Span<T>”的风险是同类问题）。

#### 6.1.3 执行层的“硬隔离”：managed 不能进 jobs/Burst

还是在 `components-managed.md`（Entities 1.3.2）里，Unity 直接列出限制：

- managed components **不能在 jobs 中访问**
- managed components **不能在 Burst 代码中使用**
- managed components **需要 GC**

这就是 Unity 解决“二套存储结构冲突”的核心策略之一：

> 在数据层允许并存；在执行层严格隔离。  
> 只有 unmanaged 才能进入高性能并行路径（jobs/Burst）；managed 永远走另一条受限路径。

这也解释了为什么 Unity 的双存储结构并不会天然变成灾难：  
因为它从 API/调度层就把“哪些代码能碰 managed”划了红线。

#### 6.1.4 结构变更与生命周期：对 chunk 只搬 `int`，对象生命周期由 store 管

从 `ManagedComponentStore.cs` 的接口命名可以看出它承担的职责（举例，不展开细节）：

- `CloneManagedComponentsFromDifferentWorld(...)`
- `MoveManagedComponentsFromDifferentWorld(...)`

它们共同暗示了一点：结构变更/搬迁时，chunk 侧处理的仍然是“索引数组”；对象本体的 clone/move/dispose 由 store 统一管理。

---

### 6.2 由 Unity ECS 推导：PGD 若要走 Native Kernel + 双存储，需要改动哪些模块

下面是“模块清单”，强调“改动范围到底有多大”。每条只用一句话说明“要改什么”，避免在本文直接写成 PGD 的实现细节设计稿。

> 参考 PGD 源码入口（便于定位；以本机路径为准）：`C:\WorkSpace\GitHub\PGDCS\PGD_Core`

#### 6.2.1 类型系统与元数据（把“能不能进 native kernel”变成编译期/构建期事实）

- 组件注册/类型元数据（TypeId/TypeIndex/Size/Align/IsUnmanaged/IsManaged）：新增“存储种类（native-backed vs managed-store）”标记，并能被 Query 生成代码稳定引用。
- 组件默认值与构造规则：为 managed component 约定（或强制）无参构造与可序列化/可克隆策略的元数据入口。
- TypeHandle/版本号（用于安全与缓存失效）：为两条存储路径提供统一的“版本/变更检测”接口，避免缓存旧视图（Span/数组引用）导致悬空访问。

#### 6.2.2 存储层（把 `PgdArray<T>` 拆成“unmanaged 真实数据”与“managed 索引数组”）

- `PgdArray<T>` 抽象：拆出统一存储接口（容量、扩容、清零、拷贝、切片视图）并提供两套实现（native-backed / managed-backed）。
- `PgdArrayNative<T>`（仅 `where T : unmanaged`）：底层用 native 连续内存（概念上对应 StackOBot 的 `NativeBuffer<T>`），暴露 `Span<T>` 视图与可传递给 native kernel 的指针。
- `ManagedComponentStore`（PGD 版）：引入 World/World-like 级的对象存储（概念上对应 Unity 的 `ManagedComponentStore.m_ManagedComponentData`），提供分配/释放槽位与 clone/dispose 钩子。
- Archetype 组件表（type → storage）：让 archetype 能同时持有“unmanaged 的数据指针/缓冲区”与“managed 的索引数组”，并能在 Query 构建阶段生成对应的 view。

#### 6.2.3 Chunk/View 与 Query 生成代码（避免在最内层循环做“走哪条路径”的分支）

- `ArchetypeChunk<T...>` / chunk view：为 managed 组件提供“索引数组 + store 访问器”的视图（类似 Unity 的 `ManagedComponentAccessor<T>`），而不是继续假装它能变成 `Span<T>`。
- `IQuery<T...>` 生成器：在生成阶段判定“该 Query 是否全 unmanaged”；只有全 unmanaged 才生成/暴露 native kernel 入口（例如 `ParallelForEachNativeKernel`）。
- `IQuery.ForeachEntity` 执行体：把“选择 native kernel or managed 路径”的分支上移到 Query 层/系统层，保证 foreach 内层仍然是 tight loop。
- `MIN_PARALLEL_CHUNK_SIZE`/section 切分策略：保持切分在 C#（生成代码）侧完成，把 slice 描述（start/len）传给 native kernel 执行。

#### 6.2.4 并行调度与后端选择（PGD 的“并行语义”不变，只替换执行后端）

- 并行后端路由（native kernel vs managed parallel）：把“执行一批 tasks”的后端抽象出来；unmanaged Query 走 UE TaskGraph + native kernel，混合/managed Query 走原 C# runner（或单线程）。
- JobHandle/barrier（结构变更 fence）：增加“native job 运行期 fence”，禁止扩容/迁移/结构变更触碰正在被 worker 写入的底层内存。
- 异常聚合与完成语义：native kernel 路径的异常/错误要能回传到 PGD 的统一完成点（对齐 JobHandle 风格），避免把错误吞在 worker 线程里。

#### 6.2.5 结构性变更（Add/Remove/Move）与拷贝语义（Clone/Copy/Destroy）

- AddComponent/RemoveComponent：对 unmanaged 组件执行内存初始化/搬迁；对 managed 组件分配/回收 store 槽位并更新索引数组。
- MoveEntityTo/Archetype 迁移：对 unmanaged 组件搬移真实数据；对 managed 组件只搬 index，并在需要时触发 clone/move/dispose。
- 扩容（Resize/Grow）：native-backed 需要重分配并拷贝；managed-store 需要处理 store 扩容与索引数组同步（并确保旧视图失效）。
- Copy/Clone（实体复制、Prefab/原型复制）：明确 managed component 的默认语义（浅拷贝共享引用 vs ICloneable 深拷贝），并在框架层提供可控的策略入口。
- Destroy/Dispose：在销毁实体/World 时对 managed component 触发 `IDisposable`（若实现），并保证 store 槽位回收。

#### 6.2.6 验证与防呆（避免“性能跑出来了但语义悄悄错了”）

- 路径约束：native kernel API 必须硬性拒绝任何包含 managed 组件的 Query（直接抛错/返回失败），避免 silent fallback 误导性能结果。
- 视图生命周期：为 native-backed 视图（Span/指针）引入版本号/校验，检测“缓存旧视图”与“job 运行期结构变更”的未定义行为。
- 测试基准与回归：为“全 unmanaged / 混合 / 全 managed”三类 Query 分别建立对照基准，避免只测到最有利的那一类 workload。

这一整套改动，才是“PGD 内部出现两种存储结构但不崩”的前提：  
**不是靠约定，而是靠类型系统、API 分层与执行层硬隔离。**

---

## 7. 如何在 StackOBot 里复现（给到能照做的入口）

- 入口：`Script/Game/Game/StackOBot/UI/MainMenu/MainMenu_C.cs`
  - 当前默认在 `ReceiveBeginPlay()` 调用 `TaskGraphVsCSharpEcsPerfRunner.RunPosVelArchetypeCompareSweep(...)` 并打印 `[EcsPerfSweep]/[EcsPerfRound]/[EcsPerfSummary]`。
- 相关 runner：
  - 单数组基准：`Plugins/UnrealCSharp/Script/UE/Library/TaskGraphVsCSharpPerfRunner.cs`
  - ECS 基准：`Plugins/UnrealCSharp/Script/UE/Library/TaskGraphVsCSharpEcsPerfRunner.cs`

建议日志过滤关键词：

- 单数组：`PerfRound`, `PerfSummary`
- ECS：`EcsPerfSweep`, `EcsPerfRound`, `EcsPerfSummary`

如果希望在 `MainMenu_C.ReceiveBeginPlay()` 中改为跑“单数组基准对比”，可以直接调用：

```csharp
TaskGraphVsCSharpPerfRunner.RunAddOneAndSumInt32ParallelCompare(
    length: 100_000,
    taskCount: 8,
    iterations: 32,
    warmup: 3,
    rounds: 5);
```
