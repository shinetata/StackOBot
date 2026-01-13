# UnrealCSharp × UE TaskGraph：用 TaskGraph 并行执行“纯 C# 计算”的可行性与设计要点（讨论沉淀）

**TL;DR**：当前仓库的 UnrealCSharp 没有对 C# 暴露“把 C# 委托投递到 UE TaskGraph worker 执行”的现成接口；但从机制上可以支持——关键是让 TaskGraph worker 线程在执行托管代码前对 Mono 做 thread attach，并提供一个可等待的完成信号（推荐包装成 C# `Task`）来管理生命周期、异常与并发边界。

这篇文档把我们围绕“把 C# 侧重任务交给 UE TaskGraph 并行调度”的讨论固定下来，后续方案设计可以直接基于这里的结论与图示展开。

---

## 1) 目标与非目标

### 1.1 目标（当前讨论范围）

- 场景 A：**UE TaskGraph worker 线程执行纯 C# 计算**（不触碰 `UObject`/`ProcessEvent`/任何 UE API）。
- 计算完成后：
  - 可能需要回到 UE GameThread（GT）应用结果；也可能完全不需要回 GT（继续在后台链式处理）。

### 1.2 非目标（暂不讨论）

- 在 C# 侧构建带依赖的 TaskGraph 任务链（GraphEvent Prerequisites）。
- 让 TaskGraph worker 直接操作 UE 对象（这通常需要强制切回 GT，且要非常小心线程安全）。
- 讨论 UE::Tasks / TaskFlow（当前只讨论 TaskGraph）。

---

## 2) 当前仓库现状：有什么、没有什么

### 2.1 仓库里确实在用 TaskGraph，但不对 C# 暴露

插件内部有 TaskGraph/AsyncTask 的使用示例（用于插件自身逻辑，而不是让 C# 投递任务）：

- `Plugins/UnrealCSharp/Source/Compiler/Private/FCSharpCompilerRunnable.cpp`：
  - `FFunctionGraphTask::CreateAndDispatchWhenReady(..., ENamedThreads::GameThread)`
- 多处 internal call 在“不安全时切回 GT”：
  - 例如 `Plugins/UnrealCSharp/Source/UnrealCSharp/Private/Domain/InternalCall/FRegisterArray.cpp`：
    - `AsyncTask(ENamedThreads::GameThread, [...]{ RemoveContainerReference(...); })`

### 2.2 仓库里没有“C# -> TaskGraph worker 执行 managed delegate”的现成 API

在 `Script/` 与 `Plugins/UnrealCSharp/Script/` 中没有找到用于：

- 从 C# 传入委托/闭包
- C++ 侧投递到 TaskGraph worker
- worker 线程进入 Mono 执行该委托

因此，如果要实现“C# 把重活交给 TaskGraph”，需要新增一个桥接层（internal call + 托管包装）。

### 2.3 最小验证 PoC：证明“TaskGraph worker 能执行 C#”

本仓库补齐了一个最小 probe，用来验证“Native 子线程（TaskGraph worker）可以调度并执行 C# 代码”：

- C++ internal call：`Plugins/UnrealCSharp/Source/UnrealCSharp/Private/Domain/InternalCall/FTaskGraph.cpp`
  - `FTaskGraph_EnqueueProbeImplementation(int token)`：投递 `FFunctionGraphTask` 到 `ENamedThreads::AnyBackgroundThreadNormalTask`
  - worker 内：
    - `FMonoDomain::EnsureThreadAttached()`（确保 worker 线程可进入 Mono）
    - `FMonoDomain::Runtime_Invoke(...)` 调用 `Script.Library.TaskGraphProbe.OnWorker(...)`
- C# probe：`Plugins/UnrealCSharp/Script/UE/Library/TaskGraphProbe.cs`
  - `TaskGraphProbe.Enqueue(int token)`：从 C# 侧触发投递
  - `TaskGraphProbe.OnWorker(...)`：在 worker 线程执行，`Console.WriteLine` 输出线程信息

注意：internal call 的 extern 声明需要遵循 UnrealCSharp 绑定命名约定（`F<Class>Implementation`），本 PoC 的 extern 位于：
- `Plugins/UnrealCSharp/Script/UE/Library/FTaskGraphImplementation.cs`

验证方式（手动）：

1) 在任意会在运行时执行的 C# 入口（例如某个 `BeginPlay`/初始化逻辑）调用：
   - `Script.Library.TaskGraphProbe.Enqueue(123);`
2) 在 UnrealEditor 的 Output Log 中搜索：
   - `[TaskGraphProbe] token=123 ... GT=... Worker=...`
3) 确认 `GT` 与 `Worker` 的 native thread id 不相同，即证明“TaskGraph worker 线程执行了托管代码”。

#### 2.3.1 场景扩展：主线程创建连续内存，worker 遍历并修改

不考虑主线程 wait/竞争等因素时，你可以用 `int[]` 做一个“主线程创建 → worker 线程修改”的最小验证：

1) 在主线程（例如 `BeginPlay`）创建连续内存并赋值：

```csharp
Script.Library.TaskGraphProbe.MainThreadCreateSharedInt32(10000);
```

2) 投递到 TaskGraph worker：

```csharp
Script.Library.TaskGraphProbe.Enqueue(123);
```

3) 观察 Output Log：除了线程信息外，还会有一条 “SharedInt32 processed” 日志，包含长度、修改前后 checksum 与首尾元素值：

- 预期：`first=0`，`last=19998`（因为原来是 9999，处理后乘 2）

---

### 2.4 已补齐的最小“批次任务执行”桥接：TaskGraph 后端跑通 `tasks[]`

为了让后续能把“C# 侧切好的 chunk/section 批次”交给 UE TaskGraph 并行调度，本仓库新增了一个最小桥接接口：

- C++：`Plugins/UnrealCSharp/Source/UnrealCSharp/Private/Domain/InternalCall/FTaskGraph.cpp`
  - `FTaskGraph_ExecuteBatchImplementation(nint stateHandle, int taskCount, bool wait)`：一次 dispatch `taskCount` 个 TaskGraph 任务
  - 每个 task body 内：`EnsureThreadAttached()` -> `Runtime_Invoke ExecuteTask(handle, index)`
- C#：`Plugins/UnrealCSharp/Script/UE/Library/TaskGraphBatch.cs`
  - `TaskGraphBatch.ExecuteBatch(Action<int> executeIndex, int taskCount)`：阻塞等待全部任务完成（当前实现固定 `wait=true`）
  - `TaskGraphBatch.ExecuteTasks(ITaskGraphTask[] tasks)`：语义上对齐“执行批次 tasks[]”

批次执行的最小结构（ASCII）：

```
+------------------------------+
| C#（SDK/业务层）              |
| - 构造 executeIndex / tasks[] |
| - 调用 TaskGraphBatch.Execute |
+---------------+--------------+
                |
                v
+------------------------------+
| C++ internal call (FTaskGraph)|
| - dispatch N 个 TaskGraph task |
+---------------+--------------+
                |
                v
+------------------------------+
| TaskGraph worker（native）    |
| - EnsureThreadAttached()      |
| - Runtime_Invoke ExecuteTask  |
+---------------+--------------+
                |
                v
+------------------------------+
| C# ExecuteTask(handle, index) |
| - 回调 executeIndex(index)     |
| - 或 tasks[index].Execute()    |
+------------------------------+
```

## 3) 核心问题：TaskGraph worker 怎么执行 C#？

### 3.1 关键事实：Mono 不是“只属于 GT 的一份环境”

在本仓库里，Mono 域（Domain）由 UnrealCSharp 初始化并保存在静态变量上：

- `FMonoDomain::Domain`：`Plugins/UnrealCSharp/Source/UnrealCSharpCore/Public/Domain/FMonoDomain.h`

“Mono 在 GT”更多是指：插件把进入托管世界的**主入口**放在 GT（Tick/事件回调等），而不是 Mono 只能在 GT 跑。

### 3.2 真正的门槛：worker 线程必须对 Mono 做 thread attach

UE TaskGraph worker 是 native 线程。要在这个线程里调用 `mono_runtime_invoke(_array)` 执行托管代码，必须先把该线程 attach 到 Mono：

- `mono_thread_attach(FMonoDomain::Domain)` 或 `mono_jit_thread_attach(FMonoDomain::Domain)`
- 线程结束时 `mono_thread_detach(...)`

这一步决定了“可不可靠/会不会随机崩”，而不是 TaskGraph API 本身。

> 备注：当前仓库代码里没有看到现成的 `EnsureThreadAttached()` 封装；要做该方案，需要补齐这一层（并且要考虑 worker 线程通常是长生命周期线程，建议每线程 attach 一次，而不是每个任务 attach/detach）。

---

## 4) 建议的最小架构（A：纯 C# 计算）

### 4.1 组件分层图（ASCII）

```
+--------------------------------------------------------------------------------------+
| C# 调用侧（游戏逻辑/系统）                                                             |
| - 调用 SDK：UETaskGraph.ParallelFor(...)（SDK 内部封装 chunk 切分与投递）                |
| - 或调用底层：UETaskGraph.RunWorker(...) 返回 System.Threading.Tasks.Task                |
+---------------------------------------+----------------------------------------------+
                                        |
                                        | internal call（把一个 WorkItem 投递到 UE）
                                        v
+--------------------------------------------------------------------------------------+
| UnrealCSharp C++ 桥接层（新增）                                                       |
| - 接收 WorkItem 的句柄/GCHandle                                                       |
| - 通过 TGraphTask / FFunctionGraphTask 投递到 TaskGraph worker                         |
| - worker 执行前：EnsureMonoThreadAttached()                                            |
| - worker 执行：mono_runtime_invoke 托管入口（TaskGraphDispatcher.Execute(handle)）     |
+---------------------------------------+----------------------------------------------+
                                        |
                                        v
+--------------------------------------------------------------------------------------+
| UE TaskGraph Worker Threads（并行）                                                    |
| - 执行“纯 C# 计算”（不触碰 UObject/UE API）                                            |
| - 完成后：tcs.SetResult / tcs.SetException（托管侧）                                   |
+---------------------------------------+----------------------------------------------+
                                        |
                                        |（可选）回 GT：依赖 UnrealCSharp SynchronizationContext
                                        v
+--------------------------------------------------------------------------------------+
| UE GameThread（可选）                                                                  |
| - 在 GT 上 await 时捕获到 SynchronizationContext                                       |
| - continuation 通过 Post 入队，最终在 Tick 里执行                                      |
+--------------------------------------------------------------------------------------+
```

### 4.2 为什么推荐“包装成 C# Task”

即使你“不需要回 GT”，也强烈建议返回 `Task/Task<T>`（或至少一个可等待句柄），原因不是“主线程”，而是：

- 你需要一个明确的**完成点**（内存生命周期、资源复用、阶段化流水线）。
- 你需要可靠的**异常传播**（否则 worker 内异常容易丢失或变成未捕获崩溃）。
- 你需要可选的**取消/退出安全**（关卡切换/插件停用/域卸载时的行为必须可控）。
- 你需要组合能力（`WhenAll/WhenAny`、批处理、限流、依赖关系）。

> “回到 GT”只是 `Task` 的一个额外好处：在 GT 上 `await` 会捕获 UnrealCSharp 的 `SynchronizationContext`，后续 continuation 自然回到 GT 的 Tick 队列执行。

参考：UnrealCSharp 的同步上下文实现位于：
- `Plugins/UnrealCSharp/Script/UE/CoreUObject/SynchronizationContext.cs`

---

## 5) 典型用法：连续内存（10000 struct）并行遍历

### 5.1 关键原则：按 chunk 切分，而不是 1 元素 1 任务

TaskGraph 更适合“多任务并行”，但每个任务应相对短小。对 10000 个元素的遍历，通常做：

- 把 `[0..9999]` 分成 N 段（chunk）
- 每段一个 TaskGraph 任务
- 每个任务内部是一个 tight loop（for）

**切分位置（本项目暂定）**：由 **插件/C# SDK 层封装 chunk 切分**，业务侧只提供“处理一个 chunk”的回调（`(start, end) => ...`）。

chunkSize 常见从 `128/256/512/1024` 试起，最终用性能测试调参；SDK 也可以提供一个默认策略（见 7.1）。

### 5.2 伪代码：并行处理 +（可选）回 GT 应用结果

> 注意：这里的 `UETaskGraph.*` 为拟议 API 名称，用于表达“投递到 TaskGraph worker 并返回 Task”；并非当前仓库已有实现。

```csharp
using System;
using System.Threading.Tasks;

struct Item { public float X, Y; }

static Item Process(Item v) => v; // 纯计算：不要触碰 UE API

public static async Task ProcessItemsInParallel(Item[] data)
{
    int chunkSize = 256; // 可选：也可以让 SDK 自动决定

    await UETaskGraph.ParallelFor(
        length: data.Length,
        chunkSize: chunkSize,
        body: (start, end) =>
        {
            for (int i = start; i < end; i++)
            {
                data[i] = Process(data[i]);
            }
        });

    // 可选：回到 GT 后再触碰 UE（ApplyResultToUE 等）
}
```

等价的底层实现思路是：SDK 根据 `length/chunkSize` 生成 N 个 work item，每个 work item 调 `RunWorker(...)`，最后 `await Task.WhenAll(...)`。

### 5.3 流程图：调度、执行、完成（ASCII）

```
+------------------------------------------+
| UE GameThread（发起调度的线程）            |
+----------------------+-------------------+
                       |
                       | 1) 调用 SDK：UETaskGraph.ParallelFor(...)
                       v
+------------------------------------------+
| C# SDK：切分 length -> N 个 chunk          |
| - 每个 chunk 对应一个 work item（start,end） |
| - 投递到 UETaskGraph.RunWorker(...)        |
+----------------------+-------------------+
                       |
                       | 2) internal call -> C++ 投递 TaskGraph
                       v
+------------------------------------------+
| C++：投递到 TaskGraph worker              |
| - TGraphTask/FFunctionGraphTask          |
| - EnsureMonoThreadAttached()             |
| - Invoke(ManagedDispatcher.Execute)      |
+----------------------+-------------------+
                       |
                       v
+------------------------------------------+
| TaskGraph Workers（并行执行）             |
| - 每个任务处理一个 chunk 的 for 循环       |
| - 完成后：tcs.SetResult/SetException      |
+----------------------+-------------------+
                       |
                       | 3) WhenAll 完成
                       v
+------------------------------------------+
| 调用方后续（continuation）               |
| - 若在 GT await：通过 SyncContext 回 GT   |
| - 若在后台 await：继续在后台上下文         |
+------------------------------------------+
```

---

## 6) GT 会不会阻塞？不阻塞时怎么避免竞争？

### 6.1 GT 是否阻塞，取决于“怎么等”

- 会阻塞：在 GT 上做同步等待 `Task.Wait()` / `Task.WaitAll(...)`（或 C++ 侧 WaitUntilTaskCompletes）。
- 不阻塞（推荐）：在 GT 上用 `await Task.WhenAll(...)`，方法挂起，GT 继续跑帧循环；完成后 continuation 回到 GT 的 Tick 队列执行。

### 6.2 不阻塞时避免竞争：用规约，不要靠侥幸

无论回不回 GT，只要多个线程同时读写同一块可变内存，就会有竞争风险。建议用以下之一：

- **阶段化（Ownership / Phase Barrier）**：任务运行期间 GT/其他线程不读写这块数据；`WhenAll` 完成后再进入下一阶段。
- **双缓冲（front/back）**：worker 写 backBuffer；GT 只读 frontBuffer；完成后在安全点交换。
- **分区写 + 汇总**：每个任务只写自己的输出分片，避免共享写，最后统一 merge。

> 结论：不建议“让 GT 和 worker 同时碰同一数组，然后用 lock 保护”，这通常会吞掉并行收益并引入长尾卡顿。

---

## 7) 拟议接口形态（便于后续方案设计）

### 7.1 最小 C# API（建议）

对业务侧更友好的“切分封装”入口（暂定主入口）：

- `Task ParallelFor(int length, int chunkSize, Action<int,int> body)`
- `Task ParallelFor(int length, Action<int,int> body)`（SDK 自动决定 chunkSize）
-（可选）支持取消：`Task ParallelFor(int length, int chunkSize, Action<int,int> body, CancellationToken token)`

底层投递能力（供 SDK/高级用法调用）：

- `Task RunWorker(Action work)`
- `Task<T> RunWorker<T>(Func<T> work)`

SDK 默认 chunk 策略（拟议）：

```
taskTarget = Environment.ProcessorCount * 4        // 目标任务数（偏短任务）
chunkSize  = ceil(length / taskTarget)
chunkSize  = clamp(chunkSize, min: 128, max: 2048) // 用性能测试调整上下限
```

### 7.2 最小 C++ 桥接（建议职责）

- internal call：`EnqueueWorkItem(handle)`  
  - handle：托管侧 WorkItem 的 GCHandle/IntPtr（用于跨线程保活与取回）
- worker 任务体：
  - `EnsureMonoThreadAttached()`（每线程一次）
  - 调用托管入口 `TaskGraphDispatcher.Execute(handle)`

### 7.3 风险清单（后续实现必须逐项落地）

- Mono thread attach/detach 策略（按线程一次 vs 按任务）。
- 域卸载/退出时的任务处理（拒绝新任务/取消/等待清理）。
- 异常传播与日志策略（SetException + 统一上报）。
- “禁止在 worker 触碰 UE API”的约束与防呆（文档约束 + 运行期检查/断言可选）。

---

## 8) Golden Rule

**把 TaskGraph 用作“纯计算并行器”，把 UE API 调用限定在 GT。**  
当你需要“计算完成后改 UE”，用 `await` 捕获的 `SynchronizationContext` 或显式 `AsyncTask(GameThread, ...)` 回到 GT 再做。
