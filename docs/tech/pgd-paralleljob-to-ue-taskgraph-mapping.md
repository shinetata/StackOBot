# PGD_Core ParallelJob × UE TaskGraph：并行模型对标与迁移落地建议（面向 UnrealCSharp）

**TL;DR**：PGD 的 `ParallelForEach/ScheduleParallel` 本质是“把 `IParallelTask[]` 批次交给 runner 执行”，并用 `JobHandle` 做完成信号与异常聚合；在 UE5 中落地时，**PGD 作为独立 C# 框架不改动**，只以 `PGD.dll` 形式被 UnrealCSharp 引入。UE 并行后端通过 **UnrealCSharp 内的 Extension** 提供：继续由 C# 层做 chunk/section 切分，只把“执行这一批任务”的调度替换为 UE TaskGraph，并在 worker 线程进入 C# 前做 Mono thread attach（本仓库已通过 PoC 验证可行）。

补充约束（很关键）：PGD_Core 的 `ParallelJob` 模块里，`ParallelManager/ParallelRunner/IParallelTask/JobHandle` 等类型大多是 **internal**。因此 UnrealCSharp 的 Extension 层无法直接复用这些实现；本文把它们当作“语义参考/性能策略参考”，而 UE 侧需要**重建一套对外可用的调度管理层**（以 public API + 生成代码信息为输入），最终仍然落到“执行一批任务”的 TaskGraph 后端。

## 本机路径（新对话上下文约定）

- PGD 工程根目录：`C:\WorkSpace\GitHub\PGDCS`
- 重点目录（高性能 ECS 框架）：`C:\WorkSpace\GitHub\PGDCS\PGD_Core`
- ParallelJob 参考实现：`C:\WorkSpace\GitHub\PGDCS\PGD_Core\src\Extensions\ParallelJob`
- Query 生成代码（切分策略来源）：`C:\WorkSpace\GitHub\PGDCS\PGD_Core\src\Query\Generated`
- ParallelJob 单元测试（真实用法入口）：`C:\WorkSpace\GitHub\PGDCS\Test\PGDCore\Core\ParallelJob\ParallelJobComparisonTests.cs`

---

## 1) PGD ParallelJob：你真正要迁移的是什么

PGD 的并行化模块不是“某个神奇的线程池”，而是一套可复用的语义：

- **任务单元（语义）**：每个 worker 执行一个“独立区间/分片”的计算（通常对应一个 chunk 或 chunk 的一个 section）
- **批次（语义）**：一次调度提交的一组任务（概念上等价于 `tasks[]`）
- **执行器（语义）**：把批次任务并行跑完，并提供“阻塞/非阻塞 + 完成信号 + 异常聚合”
- **两种完成语义**：
  - 阻塞：`ParallelManager.ExecuteTasks(tasks)` -> 调用点阻塞直到批次完成
  - 非阻塞：`ParallelManager.ExecuteTasksAsync(tasks)` -> 立即返回 `JobHandle`，稍后 `Complete()`

### 1.1 单元测试里的真实使用方式（先理解这个，再谈 UE 落地）

入口：`C:\WorkSpace\GitHub\PGDCS\Test\PGDCore\Core\ParallelJob\ParallelJobComparisonTests.cs`

这个单测文件给出“PGD 在实际业务里会怎么用并行”的关键形态：

- **系统 Tick 内调用模式（每帧执行）**：
  - `PGDSystem<T1,T2>.OnUpdate()` 里调用 `GetQuery().ParallelForEach(...)`（示例：`SystemA..SystemE`）
  - `PGDSystem<T1,T2>.OnUpdate()` 里调用 `GetQuery().ScheduleParallel(...)` 拿到 `JobHandle`（示例：`SystemF..SystemJ`）
- **典型负载（热路径）**：
  - 单测里 `PairEntityCount = 10000`，多个 Query/多个 System 每帧都遍历并写组件（`ref` 写入）。
  - `MeasureParallelQueries(...)` / `MeasureScheduleQueries(...)` 会连续迭代多次（32/100/500/1000），模拟高频帧循环。
- **ScheduleParallel 的关键语义**：
  - `ScheduleParallel` 只负责“启动并行”，不会自动等待。
  - 业务侧必须在正确阶段 `Complete()` 建立 barrier（单测里用 `ScheduleParallelHandleStore.CompleteAll()` 统一收敛）。

对 UE TaskGraph 后端的直接含义：
- “非阻塞式”不是可选优化，而是 PGD 的核心用法之一。
- 每帧高频调用下，后端热路径不能引入固定大开销（反射/查找/分配/锁竞争）。

核心调用链（PGD 文档与代码一致；注意这些类型在 PGD 内部多为 internal，这里只用于理解语义）：

```
IQuery<T...>.ParallelForEach / ScheduleParallel
  -> ParallelManager.ExecuteTasks / ExecuteTasksAsync
    -> ParallelRunner.ExecuteRunner / ExecuteRunnerAsync
      -> tasks[index].ExecuteTask()
```

### 1.2 PGD 的关键实现点（用于对标 UE TaskGraph）

- `ParallelRunner` 线程常驻：首次使用创建固定数量后台线程，后续复用。
- 任务分配是静态的：线程 index -> `tasks[index]`，没有 work stealing。
- 异常聚合：worker 线程收集 `taskExceptions`，阻塞模式在调用点抛出；异步模式在 `JobHandle.Complete()` 抛出。
- chunk/section 切分策略在 C#（生成代码）侧完成：
  - `ParallelForEach(lambda)`：按 chunk 分配，每次凑满 `taskCount` 就执行一批。
  - `ParallelForEach(lambda, taskCount)`：对每个 chunk 再做 section 切分（受 `MIN_PARALLEL_CHUNK_SIZE` 影响）。

相关文件（PGD_Core）：

- `src/Extensions/ParallelJob/ParallelManager.cs`
- `src/Extensions/ParallelJob/ParallelRunner.cs`
- `src/Extensions/ParallelJob/JobHandle.cs`
- `src/Query/Generated/IQuery.g1.cs` / `src/Query/Generated/IQuery.g2.cs`

### 1.3 反推的“高性能编程规范”（以 PGD 的热路径为准）

从 `IQuery.g*.cs` + `ParallelTask.g*.cs` + 单测用法，可以把 PGD 的性能约束归纳为：

- **组件必须是 struct，且以 `ref` 访问写入**：业务逻辑形态是 `lambda(ref T1, ref T2, IEntity)`，避免拷贝与装箱。
- **热路径禁止分配**：每帧遍历上千/上万实体，任何闭包捕获、`Task/TCS`、字符串拼接都应视为不可接受。
- **数据必须连续、可切片**：chunk 提供 `Span<T>`/连续视图，遍历应是 tight loop（`for`）。
- **并行粒度必须可控**：
  - 数据量小应退化单线程（例如 `MIN_PARALLEL_CHUNK_SIZE=10000` 这样的阈值策略）。
  - 并行时按 chunk/section 切分，避免“1 元素 1 任务”的调度开销。
- **ScheduleParallel 必须由业务显式建立 barrier**：`CompleteAll()` 是常见形态，这要求后端提供“极低成本的 handle + Complete”。

---

## 2) UE TaskGraph：你能用它替换 PGD 的哪一段

TaskGraph 的定位是：**把许多“短任务”调度到 UE 的 worker 线程池执行**，并提供完成事件/依赖（GraphEvent）。

对 PGD 来说，TaskGraph 最适合替换的是：

```
ParallelManager.ExecuteTasks/ExecuteTasksAsync
  -> (原) ParallelRunner.* (自己管理线程)
  -> (新) TaskGraph：调度 N 个任务到 UE worker 池
```

也就是说（并且要满足“PGD 不依赖引擎层/插件层”的边界）：

- **chunk/section 的切分**：继续放在 C#（PGD 生成代码/SDK）侧做（保持语义与性能策略可控）。
- **执行这一批 tasks（语义上的 tasks[]）**：改为用 TaskGraph 调度。

落地形态建议：

- **PGD.dll**：保持纯 C#、不引 UE/UnrealCSharp 依赖。
- **UE 扩展层（UnrealCSharp Extension）**：在 UE 项目侧重建 public 的并行调度入口（切分 + 批次管理 + 异常/完成信号），把“执行批次 tasks[]”映射到 TaskGraph 调度。

---

## 3) UnrealCSharp 约束：TaskGraph worker 进入 C# 的必要条件

TaskGraph worker 是 native 线程，要在 worker 上执行托管逻辑必须满足：

1) worker 线程在进入 C# 前要 **attach 到 Mono**（否则不可可靠执行托管代码）
2) worker 线程会复用：需要避免重复 attach 触发 Mono cooperative GC 的线程状态断言

本仓库已经验证并修复了这两点：

- `FMonoDomain::EnsureThreadAttached()`：`Plugins/UnrealCSharp/Source/UnrealCSharpCore/Private/Domain/FMonoDomain.cpp`
  - 逻辑：若 `mono_thread_current()!=nullptr` 则不重复 attach，否则 `mono_thread_attach(Domain)`
- PoC：TaskGraph worker 回调执行 C#：
  - C++：`Plugins/UnrealCSharp/Source/UnrealCSharp/Private/Domain/InternalCall/FTaskGraph.cpp`
  - C#：`Plugins/UnrealCSharp/Script/UE/Library/TaskGraphProbe.cs`

---

## 4) 能力映射：把 PGD 的语义“按原样”落到 UE TaskGraph

### 4.1 概念映射表

| PGD 概念 | UE TaskGraph 对应 | 迁移策略 |
| --- | --- | --- |
| `IParallelTask.ExecuteTask()` | 一个 TaskGraph task 的 body | 每个 task 内只做纯计算，不触碰 UE API |
| `tasks[]` 批次 | `GraphEventRef[]` / 一组 dispatch | 一次批次 dispatch N 个 TaskGraph 任务 |
| 阻塞等待（ParallelForEach） | `WaitUntilTasksComplete` / 等待全部事件 | 仅在“明确需要阻塞”时使用 |
| 非阻塞（ScheduleParallel + JobHandle） | 返回一个 handle（C# `Task` 或 JobHandle） | 用 `TaskCompletionSource` 或自定义 `JobHandle`，完成时 signal |
| 异常聚合 | 聚合 exceptions | worker 捕获异常并汇总，等待/Complete 时抛出 |
| PGD runner 线程数（taskCount） | TaskGraph 并行度（提交的任务数） | taskCount 仍由 PGD 侧策略决定（例如 `min(chunkCount, MAX_THREAD_COUNT)`） |

### 4.2 执行流程（ASCII）

```
+------------------------------+
| C#（PGD 侧公开能力/生成信息）  |
| - 计算 chunkCount/taskCount   |
| - 产出批次描述（start/len 等） |
+---------------+--------------+
                |
                v
+------------------------------+          +----------------------------------+
| UE 扩展层并行管理（新建）       |  ----->  | UE TaskGraph（worker 线程池）        |
| - 批次管理/异常聚合/完成信号    |          | - dispatch N 个任务                  |
+---------------+--------------+          +------------------+----------------+
                |                                            |
                |                                            v
                |                           +----------------------------------+
                |                           | 每个 TaskGraph task（worker）       |
                |                           | - EnsureThreadAttached()           |
                |                           | - ExecuteTask(handle, i)           |
                |                           | - 捕获异常写入 exceptions[i]        |
                |                           +------------------+----------------+
                |                                            |
                v                                            v
+------------------------------+          +----------------------------------+
| 阻塞模式：调用点等待完成        |          | 非阻塞：完成时 signal handle         |
| - 聚合异常并抛出               |          | - Complete/await 时聚合异常并抛出    |
+------------------------------+          +----------------------------------+
```

---

## 5) 以 PGD 的性能标准审视当前 TaskGraph 能力（StackOBot / UnrealCSharp）

当前仓库已经跑通了 “C# -> TaskGraph worker -> 执行 C#” 的最小能力，但它是 **PoC 级别**：可以验证可行性，不满足 PGD 这类“每帧高频、大规模遍历”的性能要求。

### 5.1 已实现能力（能跑通，但不是性能形态）

- “批次执行”桥接：
  - C++：`Plugins/UnrealCSharp/Source/UnrealCSharp/Private/Domain/InternalCall/FTaskGraph.cpp`
    - `FTaskGraph.ExecuteBatch(handle, taskCount, wait)`：for 循环 dispatch `taskCount` 个 `FFunctionGraphTask`
    - task body：`EnsureThreadAttached()` 后调用托管入口（当前走 `Runtime_Invoke`）
  - C#：`Plugins/UnrealCSharp/Script/UE/Library/TaskGraphBatch.cs`
    - 用 `GCHandle` 保活 batch 状态：`ExecuteIndex(index)` 执行第 `index` 个分片

### 5.2 当前形态的主要性能问题（对照 PGD 热路径要求）

1) **每批次的 class/method 查找（等价于“反射式查找”）**
   - C++ 侧每次 `ExecuteBatch` 都会调用 `FMonoDomain::Class_From_Name` / `Class_Get_Method_From_Name` 去定位 `TaskGraphBatch.ExecuteTask`。
   - 在 PGD 场景里，这是每帧多次调用的热路径，固定成本不可接受（应该缓存并处理 Domain reload 失效）。
2) **每个 task 的 `Runtime_Invoke`（反射式 invoke 路径）**
   - 即使缓存了 `MonoMethod*`，`Runtime_Invoke` 本身仍有明显桥接开销。
   - 对“细粒度 chunk/section”会直接吞掉并行收益。
3) **托管侧分配点必须被压到最低**
   - `GCHandle.Alloc/Free` 是必要开销，但频率需要可控（例如批次 state 池化/按系统复用 state）。
   - 若允许业务传入捕获 lambda/委托，闭包对象会在热路径分配（必须禁止或约束为 `static`）。

### 5.3 极致性能优先的取舍建议（可以阉割功能）

目标是把热路径压到：**TaskGraph dispatch + EnsureThreadAttached(常数) + 直接调用托管入口 + Span tight loop**，其余一律搬到冷路径或砍掉。

- 砍掉/限制：
  - 禁止捕获闭包：只允许 `static` 回调或生成代码内联的回调（否则必分配）。
  - 不提供 `Task/await` 语义：对齐 PGD 的 `JobHandle` 风格（返回轻量 handle，业务显式 `Complete()`）。
  - 不支持 worker 触碰 UE API：强制“纯计算”；如需回 GT，只允许在 completion 阶段做极轻的 signal。
- 必做优化：
  - **缓存 `MonoClass*`/`MonoMethod*`（并处理 Domain reload 失效）**：消灭每批次查找。
  - 进一步：缓存 `UnmanagedThunk`（`Method_Get_Unmanaged_Thunk`）替代 `Runtime_Invoke`，避免反射式 invoke。
  - 批次 state/GCHandle 池化：降低 `Alloc/Free` 频率，避免 GC 压力。

“缓存 + thunk” 的伪代码示意（表达意图）：

```
// C++：冷路径缓存（Domain 变更时失效重建）
global cachedDomainGen
global cachedMethod
global cachedThunk

function GetOrBuildCache():
  if DomainGen != cachedDomainGen:
    cachedMethod = null
    cachedThunk  = null
    cachedDomainGen = DomainGen

  if cachedThunk != null: return true
  cls = Class_From_Name("Script.Library","TaskGraphBatch")
  m   = Class_Get_Method_From_Name(cls,"ExecuteTask",2)
  cachedMethod = m
  cachedThunk  = Method_Get_Unmanaged_Thunk(m)   // 目标：热路径直接 call thunk
  return cachedThunk != null

function ExecuteBatch(handle, taskCount):
  if !GetOrBuildCache(): return
  for i in [0..taskCount):
    dispatch TaskGraph worker:
      EnsureThreadAttached()
      call cachedThunk(handle, i)               // 目标：替代 Runtime_Invoke
```

---

## 6) 非阻塞式批次执行（TaskGraph 后端方案草案，后续以本节为准）

当前仓库里 `TaskGraphBatch.ExecuteBatch(...)` 是阻塞式（`wait=true`）：调用线程会同步等待批次全部完成，然后立刻释放 `GCHandle`。

非阻塞（`wait=false`）不能直接“把 wait 改成 false”，否则 C# 在 internal call 返回后会立刻 `handle.Free()`，但 TaskGraph worker 仍会继续回调 `ExecuteTask(handle, index)`，导致 handle 失效（轻则异常，重则未定义行为）。

### 6.1 目标语义（建议）

- `ExecuteBatchAsync(...)`：dispatch 立即返回一个可等待对象（建议 `Task`/`ValueTask` 或自定义 handle）。
- `GCHandle` 生命周期延续到“所有 worker 任务完成后”，由 **completion 回调**释放。
- 异常不穿透到 native（避免 `Unhandled_Exception`），而是在托管侧捕获并聚合后设置到返回的 `Task`。

### 6.2 最小可落地实现：TaskGraph prerequisites + Completion Task

核心点：仍然 dispatch `N` 个 worker task，但不在 C++ 里 `WaitUntilTasksComplete`；改为再 dispatch 一个 “Completion Task”，它依赖于前面 `N` 个 task 的 GraphEvent（Prerequisites）。Completion Task 触发一次托管回调 `Complete(handle)`：

```
+------------------------------+
| C# ExecuteBatchAsync(...)     |
| - state{ExecuteIndex,TCS,Ex}  |
| - GCHandle.Alloc(state)       |
| - internal call ExecuteBatch  |
| - return state.Task           |
+---------------+--------------+
                |
                v
+------------------------------+
| C++ ExecuteBatch(wait=false)  |
| - dispatch N worker tasks      |
| - events[] = GraphEventRef     |
| - dispatch completion task     |
|   prereqs = events[]           |
+---------------+--------------+
                |
                v
+------------------------------+
| TaskGraph Worker (task i)     |
| - EnsureThreadAttached()      |
| - Runtime_Invoke ExecuteTask  |
|   -> C# try/catch 记录异常     |
+---------------+--------------+
                |
                v
+------------------------------+
| TaskGraph Worker (completion) |
| - EnsureThreadAttached()      |
| - Runtime_Invoke Complete     |
|   -> tcs.SetResult/SetException|
|   -> GCHandle.Free(handle)     |
+------------------------------+
```

### 6.3 托管侧伪代码（强调异常聚合 + handle 释放时机）

```
public static Task ExecuteBatchAsync(Action<int> executeIndex, int taskCount)
{
    var state = new BatchState
    {
        ExecuteIndex = executeIndex,
        Tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously),
        Exceptions = new ConcurrentQueue<Exception>()
    };

    var handle = GCHandle.Alloc(state);
    state.Handle = handle;

    // native：dispatch N 个 task + completion task（不等待）
    FTaskGraph.ExecuteBatch(GCHandle.ToIntPtr(handle), taskCount, wait:false);

    return state.Tcs.Task;
}

// 被 worker 调用 N 次
public static void ExecuteTask(nint stateHandle, int index)
{
    var state = (BatchState)GCHandle.FromIntPtr((IntPtr)stateHandle).Target!;

    try { state.ExecuteIndex(index); }
    catch (Exception ex) { state.Exceptions.Enqueue(ex); }
}

// 被 completion task 调用 1 次
public static void Complete(nint stateHandle)
{
    var handle = GCHandle.FromIntPtr((IntPtr)stateHandle);
    var state = (BatchState)handle.Target!;

    if (state.Exceptions.TryDequeue(out var ex)) state.Tcs.TrySetException(ex);
    else state.Tcs.TrySetResult(null);

    handle.Free(); // 关键：由 completion 释放
}
```

### 6.4 “回到 GT”怎么做（可选）

非阻塞式通常还需要一个约束：**任何 UE API 调用必须回到 GT**。

两种最小策略：

1) completion task 仍然在 worker 上触发 `Complete`，而 `await` 的 continuation 在 GT 上通过 `SynchronizationContext` 回到 GT（要求 await 点在 GT）。
2) completion task 直接安排到 GT：把 completion task 的线程指定为 `ENamedThreads::GameThread`，在 GT 执行 `Complete`（注意：这会把“完成回调”放到 GT，不能做重活，只做 signal/切换）。

---

## 7) Span 需求下的切分位置：仍在 C# 做，但“传索引，不传 Span”

PGD 的 Query 实现里本来就大量使用 `Span<T>`/`ref`（例如 `components1[n]` 是连续内存访问）。

在 UE TaskGraph + UnrealCSharp 场景下（性能第一优先级），建议保持以下规则：

- **切分仍在 C# 层做**：生成 `(start,end)`，每个 worker 任务只处理自己区间。
- **不要把 `Span<T>` 作为跨任务/跨边界的参数持久化**：
  - `Span<T>` 是 `ref struct`，不能被捕获到普通委托/Task 里长期持有，也不能跨 `await`。
  - 但你可以在 worker 任务的同步执行体内临时创建 `span = data.AsSpan(start, len)` 进行遍历。

### 7.1 `AsSpan(start, len)` 的开销（性能要点）

- `data.AsSpan(start, len)` **不会分配**、不会拷贝数据，本质上只是构造一个很小的 `Span<T>` 值（可理解为“指针 + 长度”的视图）。
- 在 JIT 优化良好时通常会被内联，开销相对 tight loop 的遍历成本可忽略。
- 真正影响吞吐的通常是：任务粒度（TaskGraph 调度开销）、循环体边界检查是否被消掉、缓存局部性与写写冲突规约。

### 7.2 写回语义：Span 修改会同步到源数据

- `Span<T>` 是对源数组/源内存的视图：对 `span[i]` 的写入就是**原地写回**源数据。
- 只有在你显式做了拷贝（例如 `ToArray()`）时才会“不同步”。

典型模式（伪代码）：

```csharp
// worker 上同步执行：可以临时用 Span 做切片遍历
var span = data.AsSpan(start, end - start);
for (int i = 0; i < span.Length; i++)
{
    span[i] = Process(span[i]);
}
```

---

## 8) 最小落地顺序（建议）

1) **保持 PGD 的切分策略不变**（chunkCount/taskCount/sectionSize）。
2) 先在 StackOBot 里把 TaskGraph 后端跑通“执行批次 tasks[]”：
   - 以“提交 N 个 TaskGraph 任务 -> 每个任务回调到 C# 执行 ExecuteTask(handle, i)”为核心。
   - 参考本仓库最小桥接：`Plugins/UnrealCSharp/Script/UE/Library/TaskGraphBatch.cs` + `Plugins/UnrealCSharp/Source/UnrealCSharp/Private/Domain/InternalCall/FTaskGraph.cpp`
3) 再补齐与 PGD 语义对齐的两条路径：
   - 阻塞：对应 `ParallelForEach`
   - 非阻塞：对应 `ScheduleParallel + JobHandle`
4) 最后再做优化点（对 PGD 的参考价值最高）：
   - “并发调度避免覆盖”的思路（PGD 的 AsyncRunnerPool）
   - 统一异常聚合与性能统计

> 关键原则：**PGD 负责“怎么切分任务”，UE TaskGraph 负责“怎么调度这些任务”**。两者各司其职，才能保证迁移过程可控、可回退、可对齐行为。
