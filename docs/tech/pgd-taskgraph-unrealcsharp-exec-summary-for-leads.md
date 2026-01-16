# PGD x UE TaskGraph x UnrealCSharp：TaskGraph 并行能力构建过程记录与问题复盘（面向负责人）

面向读者：项目负责人/技术负责人/后来接手的人。本文的目标不是“给结论”，而是把我们从 0 到现状一路做过什么、怎么验证、怎么踩坑、怎么定位、现在卡在哪、接下来有哪些路记录清楚。

**TL;DR（把现状压到 30 秒）**

- 最终目标与 `docs/tech/pgd-paralleljob-to-ue-taskgraph-mapping.md` 一致：把 PGD 的并行 Job 语义映射到 UE TaskGraph，并对开发者暴露 UE 中可用的并行接口。
- 进展：已经跑通“在 UE TaskGraph 的 native worker 线程里执行 C# 托管代码”的 PoC，并扩展出 batch 接口与 benchmark 对照入口。
- 阻塞：Editor 在二次 PIE（Play -> Stop -> Play）时稳定卡死。卡死点稳定在加载 `Game.dll` 时的 `LoadFromStream` 调用路径；触发条件强相关于“TaskGraph worker 调用了 `EnsureThreadAttached()`（即 worker attach 进入运行时）”。
- 决策点：继续走“worker 直接跑托管”的路线，就必须补上更重的生命周期收敛与可观测体系；否则建议收敛为“TaskGraph worker 不进入托管运行时”的方案（native kernel/或托管并行留在 C#）。

---

## 1) 目标与边界（先统一口径）

我们要解决的问题不是“能不能多线程”，而是：把 PGD 的并行 Job 能力在 UE5 中工程化落地。

- PGD 语义参考与落地建议：`docs/tech/pgd-paralleljob-to-ue-taskgraph-mapping.md`
- 本仓库里 `TaskGraphPerfComparison.Run(...)` 只是 benchmark/实验入口，用来快速迭代与观测，不是最终 API 形态。

本文默认边界：主要在 `Plugins/UnrealCSharp/` 与 `Script/` 范围内试验；不讨论最终产品化的完整 API 设计细节（那部分会在 PGD 映射文档里继续演进）。

---

## 2) 里程碑与产物索引（按时间顺序）

下面这张表是“做过什么”的目录，方便负责人快速定位证据与代码。

| 时间 | 我们做了什么 | 产物/证据 |
| --- | --- | --- |
| 2026-01-13 10:09 | PoC：证明 TaskGraph worker 可以执行 C#（含线程 ID 打点） | `Plugins/UnrealCSharp/Source/UnrealCSharp/Private/Domain/InternalCall/FTaskGraph.cpp`、`Plugins/UnrealCSharp/Script/UE/Library/TaskGraphProbe.cs`、`task_record/code_change_task_20260113_100943.md` |
| 2026-01-13 19:01 | 建立 benchmark 入口（MainMenu BeginPlay 调用） | `Plugins/UnrealCSharp/Script/UE/Library/TaskGraphPerfComparison.cs`、`Script/Game/Game/StackOBot/UI/MainMenu/MainMenu_C.cs`、`task_record/code_change_task_20260113_190111.md` |
| 2026-01-13 19:05 | baseline/testline 双线对照（拆成两条 internal call） | `Plugins/UnrealCSharp/Script/UE/Library/TaskGraphBatchLines.cs`、`Plugins/UnrealCSharp/Script/UE/Library/FTaskGraphImplementation.cs`、`task_record/code_change_task_20260113_190518.md` |
| 2026-01-13 19:27 | testline：缓存托管入口查找（减少每批次 class/method 查找） | `Plugins/UnrealCSharp/Source/UnrealCSharp/Private/Domain/InternalCall/FTaskGraph.cpp`、`task_record/code_change_task_20260113_192714.md` |
| 2026-01-13 19:41 | baseline：回填相同缓存（把提升固化成新基线） | `Plugins/UnrealCSharp/Source/UnrealCSharp/Private/Domain/InternalCall/FTaskGraph.cpp`、`task_record/code_change_task_20260113_194138.md` |
| 2026-01-15 16:08 | 二次 PIE 卡死：证据链与术语解释沉淀 | `docs/tech/unrealcsharp-taskgraph-worker-mono-attach-pie-freeze.md`、`task_record/archive/2026-W03/code_change_task_20260115_160803.md` |
| 2026-01-15 19:52 | 新增：统计任务落在哪些 worker 线程（分布输出） | `Plugins/UnrealCSharp/Script/UE/Library/TaskGraphWorkerDistribution.cs`、`task_record/archive/2026-W03/code_change_task_20260115_195157.md` |

---

## 3) 第一阶段：先证明“能跑通”（PoC）

### 3.1 我们先验证了什么？

最早的目标很朴素：TaskGraph 的 worker OS 线程里能不能执行托管代码？如果做不到，后续讨论都没意义。

我们用 `TaskGraphProbe` 做了一个最小链路验证：

- C# 调 internal call（只传一个 token）
- C++ 把任务丢给 TaskGraph 后台线程
- worker 线程里 attach 进运行时（否则运行时不允许在“未知线程”执行托管代码）
- 调回 C# 静态方法，打印线程信息

对应代码：

- C#：`Plugins/UnrealCSharp/Script/UE/Library/TaskGraphProbe.cs`
- C++：`Plugins/UnrealCSharp/Source/UnrealCSharp/Private/Domain/InternalCall/FTaskGraph.cpp`（`EnqueueProbeImplementation`）

### 3.2 PoC 的执行链路（ASCII 流程图）

```
GameThread (C#)
  |
  | TaskGraphProbe.Enqueue(token)
  v
InternalCall (C++)
  |
  | CreateAndDispatchWhenReady(... AnyBackgroundThread ...)
  v
TaskGraph worker (OS thread, thread-pool)
  |
  | EnsureThreadAttached()
  |   -> (runtime) thread_attach(Domain)
  |
  | Runtime_Invoke( TaskGraphProbe.OnWorker(...) )
  v
Managed (C# static method)
  |
  | Console.WriteLine: token + managedTid + nativeTid
  v
Output Log
```

这个阶段的结论很明确：从“技术可行性”上，确实能让 UE TaskGraph worker 执行托管代码。

---

## 4) 第二阶段：把“能跑通”升级为“能对比、能迭代”

PoC 只能说明“能执行”。但 PGD 的并行能力最终要面向高频、可对比、可回归的性能场景。所以我们做了两件事：

1) batch 接口：一次 dispatch N 个任务，任务按 index 回调到 C#。
2) benchmark 入口：在固定场景（MainMenu）里跑 baseline/testline，并把总耗时输出到 Log，便于对比。

### 4.1 batch 接口是什么形态？

核心思路：C# 把一个“批次状态对象（BatchState）”用 `GCHandle` 固定住，把句柄传给 C++；C++ 在 worker 上回调 `ExecuteTask(handle, index)`；C# 再用句柄找回状态对象并执行 `executeIndex(index)`。

```
C#                   C++ (TaskGraph)                    C#
BatchState           dispatch N tasks                   ExecuteTask(handle, i)
  |                        |                                 |
  | GCHandle(state)        |                                 | FromIntPtr(handle)
  | stateHandle ---------->|                                 | state.ExecuteIndex(i)
  |                        | worker(i) -> Runtime_Invoke --->|
```

对应代码：

- C#：`Plugins/UnrealCSharp/Script/UE/Library/TaskGraphBatch.cs`
- C++：`Plugins/UnrealCSharp/Source/UnrealCSharp/Private/Domain/InternalCall/FTaskGraph.cpp`（`ExecuteBatch*`）

### 4.2 为什么要 baseline/testline 双线？

我们需要一个“不会被误判”的对比方式：

- baseline 代表当前稳定路径
- testline 代表我们正在迭代的优化路径

这样才能做到：先只改 testline，确认收益/风险，再回填 baseline。

### 4.3 我们做过哪些“明确可见的”优化？

最早的性能问题之一是：每次批处理都会查找托管入口（class/method lookup）。这在高频调用下会被放大。

因此我们做了缓存：

- testline 先做 `ExecuteTask` 的 `MonoMethod*` 缓存（带失效 key，避免脚本域/程序集重载后使用旧指针）。
- 确认有收益后再回填到 baseline。

task_record 里记录了可复现实验日志（示例）：`task_record/code_change_task_20260113_194138.md`

---

## 5) 第三阶段：问题暴露 - Editor 二次 PIE 卡死

### 5.1 现象是什么？

最典型可复现路径：

- 第一次 PIE：正常
- Stop PIE
- 第二次点击 Play：Editor 卡死（无崩溃、无异常弹窗）

补充现象：Stop 后重新编译 C# 时也可能卡死（同属“PIE unload/reload 边界”的一类问题）。

### 5.2 卡死点钉在哪里？

证据链已经钉死到：

- `Plugins/UnrealCSharp/Source/UnrealCSharpCore/Private/Domain/FMonoDomain.cpp`
- `FMonoDomain::LoadAssembly(const TArray<FString>& InAssemblies)` 循环里
  - 第一次处理 `UE.dll`：不卡
  - 第二次处理 `Game.dll`：卡在 `LoadFromStream` 的调用路径（`Runtime_Invoke(...)` 不返回）

这意味着：问题不是“点击 Play 的入口就卡死”，而是脚本加载阶段卡死，且只对 `Game.dll` 复现。

---

## 6) 定位过程复盘（这部分最重要：怎么一步步得出的）

我们刻意用“像人类一样”的排障方式：每一步都能回答一个问题，能排除一类可能。

### Step A：先把卡死点钉死（不猜）

结论：第二次 Play 卡在 `LoadAssembly -> LoadFromStream(Game.dll) -> Runtime_Invoke(...)`。

### Step B：确认 Stop 的卸载链路确实执行（避免误判为“没清理”）

我们跟踪 Stop 路径，确认会进入：

- `FMonoDomain::Deinitialize`（并进一步走到域/环境 Deinitialize 链路）

结论：Stop 不是“啥也没做”。问题更像是“做了卸载，但某些状态没有完全收敛”。

### Step C：用“极简对照实验”收敛触发条件

这是目前最强证据：

- 在 `FTaskGraph.cpp` 的 worker lambda 中，只保留 `FMonoDomain::EnsureThreadAttached()`（也就是 worker 上的 attach）
- 其它托管调用全部注释掉
- 依旧复现二次 PIE 卡死
- 如果连 `EnsureThreadAttached()` 也去掉，则不再复现

结论：不是“任务里做了什么 C# 计算导致卡死”，而是“让 UE 的线程池 worker 进入运行时（attach）”本身就足够触发问题。

### Step D：为什么 Break All 看不到明显的运行时栈？

我们在卡死现场 Break All 看到上百线程，但除 GT 外，难以直接在其它线程里看到 `mono_*` 关键帧。

常见原因：

- 第三方运行时/优化构建导致符号与栈回溯不可用
- 运行时内部等待表现为 OS 层 `WaitForSingleObject/NtWaitForSingleObject`，但无法展开到运行时内部帧

结论：此时继续“翻线程栈找字符串”收益极低，更可靠的做法是继续用可复现对照实验收敛条件。

### Step E：为什么“拿到线程 ID”不等于“能让该线程执行清理”？

讨论里出现过一个直觉方案：Stop PIE 时“枚举所有 attach 过的 worker 线程”，像 GT 一样逐个清理。

关键结论：线程 ID 只能识别线程，不能让线程自动执行你指定的函数。

- `mono_thread_detach()` 这类线程级清理必须在“同一条 OS 线程”上执行，GT 无法代劳。
- UE TaskGraph 只保证任务运行在“某个后台线程”上，不提供把任务稳定派发回“某个具体 worker OS 线程”的公开能力。

```
你能做到的：记录 (ThreadId -> 曾 attach)
你做不到的：凭 ThreadId 让 TaskGraph 任务回到“同一条 worker 线程”执行 detach
```

工程含义：如果我们真要做“逐线程 attach/detach 的严格管理”，通常要走“自建可控线程池”或“引入协作式清理机制”，而不是期望 TaskGraph 线程池按线程 ID 可控。

---

## 7) 基于当前困境，我们考虑过哪些路（不是拍脑袋）

下面按“能解释清楚 + 能落地”的方式列三条路。它们不是互斥的：可以先做 B/C 做基线，再回头探索 A。

### 路线 A：TaskGraph worker 直接跑托管（当前探索路线）

```
TaskGraph worker -> EnsureThreadAttached() -> Runtime_Invoke(managed)
```

优点：形式上“把 C# 任务交给 UE TaskGraph”。

代价：在 Editor/PIE 反复 unload/reload 的边界下，要保证稳定，需要额外建设：

- 生命周期收敛（Stop 前禁止新任务进入、等待 in-flight 任务退出）
- 运行时内部状态可观测（能回答“哪个线程还在跑托管、还持有哪些引用/栈帧”）
- 线程级 attach/detach 的可控模型（TaskGraph 线程池本身不提供这一点）

一句话：这条路不是“修个 bug”就完事，而是要补一整套治理体系。

### 路线 B：TaskGraph worker 不进入托管，只跑 native kernel（推荐的工程化方向）

```
C# 描述任务/准备数据 -> TaskGraph worker 并行跑 C++ kernel -> 结果写回可读 buffer -> C# 读取/应用
```

优点：绕开 attach 生命周期冲突，PIE 稳定性更好；TaskGraph 并行能力能真正发挥。

代价：需要把热路径计算落到 C++（或可生成的 native kernel）上。

### 路线 C：托管并行留在 C# ThreadPool，UE 只提供 barrier/回主线程入口（过渡/对照基线）

```
C# Task/Parallel 并行计算
需要触达 UE 对象时 -> 回到 GameThread
```

优点：实现成本低，绕开 TaskGraph worker attach。

代价：并行调度不受 UE TaskGraph 统一管理；和 UE 自身并行负载的协同较弱。

---

## 8) 现在我们在哪，以及建议的下一步

现状总结：

- 能力建设上，已经完成“PoC -> batch -> benchmark -> 局部优化”的闭环。
- 稳定性上，二次 PIE 卡死是硬阻塞，且触发条件已收敛到“worker attach 进入运行时”。

建议的下一步（给负责人用的决策点）：

- 先决定：我们是否要把“路线 A（worker 直接跑托管）”作为长期主线？
  - 如果是：要投入建设“可控线程模型 + 生命周期收敛 + 可观测性”，否则后续还会被类似边界反复打断。
  - 如果不是：尽快把“路线 B（native kernel）”拉成主线，让 PGD 的并行能力先以稳定形态落地，A 作为可选增强再探索。

补充（短期实操）：我们已经补了一个统计工具，用于观察任务在 worker 上的分配分布：

- `Plugins/UnrealCSharp/Script/UE/Library/TaskGraphWorkerDistribution.cs`

