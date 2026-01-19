# UnrealCSharp × UE5：TaskGraph worker 执行 C# 时，二次 PIE 卡死（LoadFromStream/Game.dll）的定位思路与方案审视

**TL;DR**：当前卡死的关键触发点是“把 UE TaskGraph worker 线程 `mono_thread_attach(Domain)` 进 Mono 域”。第一次 PIE 里做过 attach 后，Stop/Play 的反复卸载/重载边界会变得不稳定，第二次 Play 常卡在 `FMonoDomain::LoadAssembly -> LoadFromStream(Game.dll)` 的 `Runtime_Invoke(...)` 上。继续推进 PGD×TaskGraph 并行化，需要先明确“哪些线程可以进入 Mono、在什么生命周期点必须停机/等待”。

## 1. 背景：我们在解决什么问题

现象（可复现）：

- 第一次 PIE 正常运行；
- Stop PIE 后再次点击 Play，Editor 卡死；
- 卡死点稳定在 `Plugins/UnrealCSharp/Source/UnrealCSharpCore/Private/Domain/FMonoDomain.cpp` 的 `LoadFromStream(Game.dll)` 相关路径（`Runtime_Invoke(AlcLoadFromStreamMethod, ...)`）。

进一步的关键实验（非常重要）：

- 在 `Plugins/UnrealCSharp/Source/UnrealCSharp/Private/Domain/InternalCall/FTaskGraph.cpp` 的 TaskGraph worker lambda 中，只保留 `FMonoDomain::EnsureThreadAttached()`，其它托管调用全部注释掉，仍能触发“二次 PIE 卡死”。
- 如果连 `EnsureThreadAttached()` 也注释掉，则二次 PIE 不再卡死。

因此，当前最值得优先解释/验证的问题不是“任务里做了什么托管计算”，而是：

> 为什么“worker 线程 attach 到 Mono 域”会让 Stop/Play 的 unload/reload 边界变得不稳定，从而第二次 load `Game.dll` 卡死？

## 2. 关键术语（不默认你了解）

### 2.1 OS 线程（操作系统线程）

操作系统（Windows）创建并调度的真实线程：有自己的线程 ID、栈、寄存器，能被 `WaitForSingleObject` 等系统调用阻塞/唤醒。UE 的各种线程（GameThread、TaskGraph worker 等）最终都是 OS 线程。

### 2.2 UE TaskGraph worker（任务图工作线程）

UE 在进程内创建的一组线程池工作线程，用来执行 `FFunctionGraphTask::CreateAndDispatchWhenReady(..., ENamedThreads::AnyBackgroundThread...)` 派发的任务。

关键特点：

- 它们是“线程池”，会复用；
- 通常跨越多次 PIE（Play→Stop→Play）仍然存在，不会因为 Stop PIE 自动销毁；
- 所以 worker 的“线程生命周期”与 MonoDomain/程序集的“卸载/重载生命周期”天然不对齐。

### 2.3 PIE（Play In Editor）

编辑器内运行游戏世界的一种模式。Stop PIE 通常会触发世界/子系统清理，并且很多插件会选择在 PIE stop/start 反复初始化/反初始化自身运行时。

### 2.4 Mono Domain 与 `mono_thread_attach`

Mono 是托管运行时。它内部也要管理“哪些线程会执行托管代码”，因为 GC/异常/加载器等需要每个线程的运行时状态。

当你在某个线程上调用：

- `mono_thread_attach(Domain)`

含义是：

- 把“当前 OS 线程”登记进 Mono 的线程系统；
- 为这个线程建立/初始化运行时线程状态（通常存于 TLS，供 GC/运行时使用）；
- 之后该线程就可以执行托管代码（例如通过 `mono_runtime_invoke`）。

这类“线程登记/线程状态”的语义，和“某个 C++ 对象析构”是两条不同的生命周期线。

### 2.5 `AssemblyLoadContext.LoadFromStream` 与 `Game.dll`

在 Editor 下，UnrealCSharp 通过 `AssemblyLoadContext.LoadFromStream(...)` 将 `Game.dll`（你们 C# 构建产物）加载进运行时。

对应代码位置：

- `Plugins/UnrealCSharp/Source/UnrealCSharpCore/Private/Domain/FMonoDomain.cpp`
  - `Class_Get_Method_From_Name(..., "LoadFromStream", 1)`
  - `const auto Result = Runtime_Invoke(AlcLoadFromStreamMethod, AssemblyLoadContextObject, Params);`

二次 PIE 卡死稳定停在这条 invoke 链路上，说明运行时内部可能在等待“加载器/域/GC/线程状态”相关的某个同步条件。

## 3. 当前实现：TaskGraph 是怎么“和 Mono 扯到一起”的

当前实现的核心是：**让 UE TaskGraph worker 线程执行托管 C# 任务**。

对应代码（真实路径）：

- TaskGraph 派发点：`Plugins/UnrealCSharp/Source/UnrealCSharp/Private/Domain/InternalCall/FTaskGraph.cpp`
  - 使用 `FFunctionGraphTask::CreateAndDispatchWhenReady(... AnyBackgroundThread...)` 派发任务到 worker
  - worker lambda 内会调用 `FMonoDomain::EnsureThreadAttached()`，随后调用托管 `ExecuteTask(...)`（通过 `Runtime_Invoke` 或 unmanaged thunk）

而 `EnsureThreadAttached()` 的实现（真实路径）：

- `Plugins/UnrealCSharp/Source/UnrealCSharpCore/Private/Domain/FMonoDomain.cpp`
  - `mono_thread_current()`
  - `mono_thread_attach(Domain)`

## 4. 一张流程图：从“第一次 PIE 正常”到“第二次 PIE 卡死”的最短链路

```
（第一次 Play）
GameThread
  |
  | C#：TaskGraphPerfComparison.Run(...)
  v
UnrealCSharp internal call（C# -> C++）
  |
  v
FTaskGraph.cpp 组装 tasks 并派发
  |
  | CreateAndDispatchWhenReady(... AnyBackgroundThread ...)
  v
TaskGraph worker OS 线程池（多个线程，线程会复用/跨 PIE 存活）
  |
  | worker lambda:
  |   EnsureThreadAttached()
  |     -> mono_thread_attach(Domain)  <-- 关键触发点（已通过实验验证）
  |   (然后可能执行托管 ExecuteTask / thunk)
  v
（第一次 Play 结束）

（Stop PIE）
GameThread / Editor 侧触发停机
  |
  v
UnrealCSharp Deinitialize 链路（你已验证会进入）
  |
  v
DeinitializeAssemblyLoadContext / UnloadAssembly / 清理句柄与 images
  |
  v
（Stop 完成，线程池 worker 仍存在）

（第二次 Play）
GameThread
  |
  v
Initialize / LoadAssembly
  |
  v
LoadFromStream(Game.dll) -> Runtime_Invoke(...)
  |
  v
卡死：invoke 内部等待某个运行时条件（loader/GC/线程状态/锁）
```

这个图强调两条独立的生命周期：

- MonoDomain/ALC/assemblies：Stop PIE 会反复卸载/重载；
- TaskGraph worker OS 线程：Stop PIE 不会销毁，会跨 PIE 复用。

二次卡死通常就出现在这两条生命周期“未建立严格同步协议”的交界处。

## 5. 结论（当前证据支持到哪里）

基于“只保留 `EnsureThreadAttached()` 就能复现二次卡死”的实验，当前最强结论是：

- 卡死的充分条件高度相关于：**TaskGraph worker OS 线程被 attach 进 Mono domain**；
- 卡死的直接表现是：第二次 Play 加载 `Game.dll` 的 `LoadFromStream` invoke 不返回。

这不足以在无符号/无托管栈的条件下直接断言 Mono 内部的具体锁名，但足以支撑下一步设计审视：

> 如果 PGD×TaskGraph 的并行化需要稳定的 Editor 工作流，就必须明确“哪些线程允许 attach、何时必须停机、Stop/编译/重载边界如何保证 worker 不再触碰 Mono”。

## 5.1 “线程状态不收敛”到底指什么（结合 Mono 的 stop-the-world 证据）

这里说的“线程状态不收敛”不是一个玄学词，它指的是：在某个运行时同步点（例如 GC stop-the-world、线程挂起/恢复、某些加载/卸载阶段）到来时，运行时需要“所有已登记为托管线程的 OS 线程”都进入可安全处理的状态；但其中至少有一条线程没有及时进入，导致其他线程在运行时内部等待。

在本仓库自带的 Mono 头文件中可以看到明确证据：Mono 的 GC 事件包含 stop-the-world，并且在锁已持有的阶段会持有 GC/suspend locks：

- `Plugins/UnrealCSharp/Source/ThirdParty/Mono/src/mono/metadata/details/profiler-types.h`
  - `MONO_GC_EVENT_PRE_STOP_WORLD_LOCKED`：注释说明“GC 和 suspend locks 已获取”
  - `MONO_GC_EVENT_POST_START_WORLD_UNLOCKED`：注释说明“GC 和 suspend locks 已释放”

结合你们观察到的“第二次 Play 卡在 `Runtime_Invoke(LoadFromStream(Game.dll))`”，可以理解为：

- `LoadFromStream` 的 invoke 走进运行时内部后，遇到了一个需要全局一致性/同步的阶段；
- 如果有任何已 attach 的线程处于不合适的状态（例如仍在托管运行时关键区/未到安全点），运行时可能会等待；
- 等待发生在运行时内部，外部看到的只是主线程在 `Runtime_Invoke(...)` 处不返回。

下面用 ASCII 图把这个“等待”过程描述清楚（不依赖 Mono 内部符号）：

```
第一次 PIE：worker 线程进入 Mono

  TaskGraph worker (OS thread #W)
      |
      | EnsureThreadAttached()
      |   -> mono_thread_attach(Domain)  [线程登记为托管线程]
      v
  (此后该 OS 线程在 Mono runtime 内有线程状态/TLS 记录)

Stop PIE：插件侧卸载/反初始化（看起来完成）

  GameThread
      |
      | Deinitialize / UnloadAssembly / ALC.Unload (Editor)
      v
  (Domain/ALC/句柄清理完成，但 OS thread #W 仍存在且曾 attach)

第二次 PIE：加载 Game.dll，进入运行时同步点

  GameThread
      |
      | LoadAssembly -> LoadFromStream(Game.dll)
      |   -> Runtime_Invoke(...)
      v
  Mono runtime 内部遇到同步点（例如 stop-the-world/挂起/关键区互斥）
      |
      | 需要：所有“已登记托管线程”(包括 #W) 达到安全状态
      |
      +--> 如果 #W 状态已收敛：继续加载 -> 返回
      |
      +--> 如果 #W 状态未收敛：运行时等待 (#W 达到安全点)
              |
              v
        外部表现：GameThread 卡在 Runtime_Invoke(LoadFromStream)
```

## 6. 审视 TaskGraph 并行化方案：三条路线与取舍

### 路线 A：UE worker 直接跑托管（当前路线）

- 形式：TaskGraph worker 执行 `EnsureThreadAttached()` + `Runtime_Invoke`/thunk 跑 C#。
- 优点：看起来像“真正把 C# 任务放到 UE TaskGraph 上”。
- 代价：在 PIE stop/start、自动编译重载等边界，线程生命周期与域生命周期冲突，稳定性成本很高；必须额外构建“停机协议 + 可观测性”。

### 路线 B：worker 不进 Mono，TaskGraph 只跑 native kernel（推荐的工程化方向）

- 形式：C# 只负责描述任务、准备数据；TaskGraph worker 执行 C++/native 计算；结果写回可读的 native buffer，再由 C# 读取/应用。
- 优点：不需要 `mono_thread_attach`；PIE 工作流稳定性更好；TaskGraph 的并行能力能真正发挥。
- 代价：计算核心需要 native 实现或可生成/可 AOT 的形式；这反而更接近“PGD 并行计算内核”的长期落地方向。

### 路线 C：托管并行留在 C# ThreadPool，UE 只提供同步/回主线程入口

- 形式：C# 用 `Task/Parallel` 做并行；需要触碰 UE 对象时通过 UnrealCSharp 的同步上下文回到 GameThread。
- 优点：不触碰 UE worker 与 attach 生命周期；实现成本低。
- 代价：并行调度不受 UE TaskGraph 统一管理；与 UE 自身并行负载的协同更弱。

## 7. 下一步怎么“像人类一样”继续定位（建议的日志验证清单）

在不做大改动的前提下，建议先用极少量日志把“状态机边界”钉死（日志的目标是回答问题，而不是刷屏）：

1) Stop PIE 是否真的执行了 unload（以及执行到哪一步结束）  
2) worker 线程是否在 Stop 边界前曾经 attach（发生过几次、在哪些线程）  
3) 第二次 Play 卡死前，是否再次尝试 load `Game.dll`，以及卡在 `LoadFromStream` 之前/之后

建议日志点位（不需要默认读者会懂）：

- PIE 边界：`Plugins/UnrealCSharp/Source/UnrealCSharpCore/Private/Listener/FEngineListener.cpp`
  - `OnPreBeginPIE` / `OnCancelPIE`
- 域生命周期：`Plugins/UnrealCSharp/Source/UnrealCSharpCore/Private/Domain/FMonoDomain.cpp`
  - `Initialize` / `Deinitialize`
  - `DeinitializeAssemblyLoadContext`（开始/结束）
  - `LoadAssembly` 加载 `Game.dll` 的那次（开始/结束）
- attach 触发点：`FMonoDomain::EnsureThreadAttached`
  - 打印当前线程 ID 与“是否发生过 attach”

有了这些日志，即使没有 Mono 内部符号，也能把问题从“黑盒卡死”变成“可复现的状态机缺口”。

## 8. 一句话的“黄金规则”

> 在 Editor/PIE 会反复 unload/reload 的环境里，不要让“长期存活的线程池 worker”不受控地进入托管 runtime；要么把托管执行收敛到可控线程域，要么把并行计算移到不依赖 Mono 的 native 层。
