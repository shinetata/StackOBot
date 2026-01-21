# UnrealCSharp × UE TaskGraph：Worker 线程执行 C# 的完整解决方案（PIE 稳定版）

## 1. 目标与约束

### 目标
- 允许 TaskGraph worker 执行托管 C# 逻辑（非 Native Kernel 方案）。
- PIE stop/start 可重复调试，不出现 Editor 卡死。
- 生命周期管理可预期，可验证，可回滚。

### 约束
- 仅讨论并落地托管执行路径，不引入 Native Kernel 方案。
- 以本仓库现状为准，代码改动集中在 `Plugins/UnrealCSharp/`。
- 不修改生成物目录。

## 2. 问题回顾（当前根因）

现象：TaskGraph worker 线程执行 C# 后，第二次 PIE 往往卡死在 `LoadFromStream(Game.dll)` 的 `Runtime_Invoke(...)`。

关键证据与背景文档：
- 现象与定位：`docs/tech/unrealcsharp/unrealcsharp-taskgraph-worker-mono-attach-pie-freeze.md`
- Unity 经验对照：`docs/tech/unity_jobsystem/new_unity_job_report.md`
- 方案雏形：`docs/tech/unrealcsharp/unrealcsharp-taskgraph-worker-scripting-jobs-fence.md`

核心根因：
- TaskGraph worker 是**常驻线程池**，跨 PIE 复用。
- worker 一旦 `mono_thread_attach`，其托管线程状态会跨 PIE 残留。
- Stop/Reload 过程缺失“全局同步点”，导致新一轮 Load/GC/Domain 操作等待旧线程状态收敛，从而卡死。

## 3. Unity 经验抽象为可复用规则

从 Unity JobSystem 的公开机制中可抽象出 3 条工程规则：

1) **全局屏障**：Domain Reload 前必须阻断新任务进入，并等待所有托管任务结束。
2) **常驻线程可复用，但托管身份必须可收敛**：Worker 线程可以常驻，但托管附着状态不能跨重载残留。
3) **Job 只做纯计算**：托管 Job 避免 UObject/资源加载/同步回 GameThread，结果由主线程应用。

## 4. 解决方案总览（3 个支柱）

### 4.1 Managed Jobs Fence（全局屏障）

在 C++ 侧建立统一的“托管任务闸门 + in-flight 计数器”，Stop/Unload 前先关闸并等待归零。

- **bManagedJobsEnabled**：是否允许托管任务进入 worker。
- **ManagedJobsInFlight**：当前正在执行的托管任务数。
- **WaitForManagedJobDrain**：Stop/Unload 时等待 in-flight 归零。

这一步对应 Unity 的 `WaitForAllJobs` + “Close Scheduling Gate”。

### 4.2 Worker 线程 Attach/Detach 策略

- worker 执行托管逻辑前：`EnsureThreadAttached()`。
- 托管逻辑结束后：Editor 下默认 `mono_thread_detach`，保证跨 PIE 不残留。
- **调试模式下禁用 per-job detach**，避免 debugger-agent 线程 TLS 断言崩溃。
- Shipping 可关闭 detach，降低频繁 attach/detach 成本。

### 4.3 托管任务运行约束

为避免互锁与不确定行为，TaskGraph worker 托管任务必须满足：
- 不访问 `UObject`、不触发资源加载、不等待 GameThread。
- 只处理纯托管数据或 native buffer 只读/只写片段。
- 写回结果时采用“worker 计算 -> GameThread Apply”。

## 5. 生命周期流程（ASCII）

### 5.1 Stop/Unload 前的全局屏障

```
GameThread (Stop PIE)
  |
  | DisableManagedJobExecution()
  | WaitForManagedJobDrain()  <-- in-flight == 0
  v
Unload AssemblyLoadContext / Assemblies
```

### 5.2 Worker 执行托管逻辑时序

```
TaskGraph worker
  |
  | TryEnterManagedJobExecution()
  | EnsureThreadAttached()
  v
Invoke C# ExecuteTask(...)
  |
  | (Editor) EnsureThreadDetached()
  v
LeaveManagedJobExecution()
```

## 6. 代码落地点（本次实现）

### 6.1 C++：托管任务闸门
- `Plugins/UnrealCSharp/Source/UnrealCSharpCore/Public/Domain/FMonoDomain.h`
- `Plugins/UnrealCSharp/Source/UnrealCSharpCore/Private/Domain/FMonoDomain.cpp`

新增能力：
- `EnableManagedJobExecution / DisableManagedJobExecution`
- `TryEnterManagedJobExecution / LeaveManagedJobExecution`
- `WaitForManagedJobDrain`
- `EnsureThreadDetached`
- Editor 下 `ShouldDetachAfterManagedJob` 默认启用，调试模式下自动关闭

并在 `FMonoDomain::Initialize/Deinitialize` 中自动启用与收敛。

### 6.2 C++：TaskGraph worker 进入托管区的统一护栏
- `Plugins/UnrealCSharp/Source/UnrealCSharp/Private/Domain/InternalCall/FTaskGraph.cpp`

改动点：
- worker lambda 统一使用 `FManagedJobScope` 包装。
- 若闸门关闭或 Domain 未就绪，任务直接返回。
- Editor 下每次托管执行后 detach，避免跨 PIE 残留（调试模式除外）。

## 7. 验证步骤（可复现）

1. 启动 Editor，进入任意可运行关卡。
2. 调用 `TaskGraphBatch.ExecuteBatch(...)` 或 `TaskGraphProbe.Enqueue(...)`，确认 worker 执行托管逻辑。
3. Stop PIE，然后再次 Play：
   - 预期：不再卡死在 `LoadFromStream(Game.dll)`。
4. 如需压力验证，可执行 `TaskGraphPerfComparison.Run(...)` 观察多次 PIE 仍可反复进入。

## 8. 风险与回滚

### 风险
- Editor 下每任务 detach 存在额外开销（但可接受）。
- 调试模式下关闭 per-job detach 后，稳定性依赖 fence 与 in-flight 归零。
- 若托管任务内部阻塞/死循环，Stop PIE 会等待 in-flight 归零而长时间停顿。

### 回滚
- 回滚 `FMonoDomain` 与 `FTaskGraph` 中的 fence/attach/detach 改动即可恢复旧行为。

## 9. 结论

借鉴 Unity 的“全局屏障 + 线程身份收敛”经验，结合 UnrealCSharp 的现有结构，本方案在不引入 Native Kernel 的前提下，提供了可落地、可调试、可回滚的 TaskGraph worker 托管执行能力，核心目标是**稳定的 PIE 调试与可控的生命周期管理**。

## 10. 路线选择：A/B（优先 A）

> 目标一致：让 PGD 在 C# 侧能稳定调用 UE 的并行调度能力，并尽量复用 UE 的依赖图/工作窃取策略。

### 10.1 路线 A：托管 Worker 池 + TaskGraph 只做“调度与依赖”

**核心思路**：UE TaskGraph 负责“依赖关系/触发时机/完成回调”，纯计算任务在托管线程池执行。这样可以保留 UE 的任务图语义，同时避免 UE worker 与 Mono 调试 TLS 的冲突。

#### 10.1.1 结构示意（ASCII）

```
+-----------------------+      enqueue job      +------------------------+
| UE TaskGraph (C++)    | --------------------> | Managed Job Queue (C#) |
| 依赖/调度/完成回调     |                       +-----------+------------+
+-----------+-----------+                                   |
            |                                              v
            |                                      +---------------------+
            |                                      | Managed Worker Pool |
            |                                      | (C# ThreadPool)     |
            |                                      +----------+----------+
            |                                                 |
            |                                                 v
            |                                      +---------------------+
            |                                      | Job Execute (C#)    |
            |                                      +----------+----------+
            |                                                 |
            |  complete (internal call)                       v
            +<----------------------------------------+------------------+
                                                     | UE GraphEvent     |
                                                     +------------------+
```

#### 10.1.2 关键模块

- **UE 侧调度桥**：`TaskGraphManagedBridge`（C++ 内部调用）
  - 创建 `FManagedJobHandle`，并返回 `FGraphEventRef` 作为依赖节点。
  - 完成回调由 C# 侧 `Complete(handle)` 触发 `FGraphEvent`。
- **C# 托管线程池**：`ManagedWorkerPool`（C#）
  - 纯托管线程，避免 UE worker + mono TLS 冲突。
  - 提供 `Start/Stop/Drain`，由 PIE 生命周期驱动。
- **全局 Fence（已实现）**：`Enable/Disable/WaitForManagedJobDrain`
  - Stop/Reload 前关闸，等待 in-flight 归零。
  - 与 C# 线程池 `StopAndDrain` 配合，保证一致收敛。

#### 10.1.3 调度与依赖流程（ASCII）

```
+---------------------+         +------------------------+         +----------------------+
| GameThread          |         | UE TaskGraph           |         | Managed Worker Pool  |
| ScheduleManagedJob  | ----->  | Create FGraphEventRef  | ----->  | Enqueue Job(handle)  |
+----------+----------+         +-----------+------------+         +----------+-----------+
           |                                |                                 |
           |<-------------------------------+                                 |
           |   GraphEvent can be depended on                                  |
           |                                                                  |
           |                                                     +------------v-----------+
           |                                                     | Execute Job (C#)       |
           |                                                     +------------+-----------+
           |                                                                  |
           |                                  Complete(handle) internal call  |
           +<-----------------------------------------------------------------+
```

#### 10.1.4 方案优点

- **Debug 友好**：托管线程由 C# 创建，Mono 调试 TLS 不与 UE worker 冲突。
- **稳定的生命周期**：Stop/Reload 时可强制 `StopAndDrain`，保证 PIE 不挂。
- **语义复用**：TaskGraph 仍承担依赖图/完成回调，PGD 侧可以继续使用 UE 任务图语义。

#### 10.1.5 约束与注意事项

- UE TaskGraph **不直接执行 C#**，只做调度与依赖节点；计算在托管池完成。
- C# 任务仍需遵守“纯计算、不触碰 UObject、不阻塞 GameThread”的规则。
- 若需更细粒度 Work-Stealing，需要在托管池内实现（UE 的窃取策略不直接生效）。

### 10.2 路线 B：改引擎，UE Worker 直接跑托管逻辑

**核心思路**：让 UE TaskGraph worker 线程具备“托管线程身份”，使其能够长期运行 C# 任务，并且在 PIE Reload 时可安全收敛。该路径完全复用 UE 的任务队列、工作窃取与调度策略，但需要引擎级改造。

#### 10.2.1 目标与边界

- 目标：让 UE worker 直接执行 C# 任务，PGD 侧获得 **完整的 UE 任务系统语义**。
- 边界：不引入 Native Kernel；不改变 UE 任务调度算法；仅补齐“托管线程身份”与“生命周期收敛”。
- 风险来源：Mono debugger-agent TLS、Domain Reload、PIE Stop/Start、线程常驻复用。

#### 10.2.2 结构示意（ASCII）

```
---------------------+         +---------------------------+         +---------------------+
| UE TaskGraph        |  task   | UE Worker Thread          |  exec   | Managed C# Job      |
| Scheduler/Queue     | ----->  | ManagedContext (TLS)      | ----->  | Execute(...)        |
+---------------------+         +-------------+-------------+         +---------------------+
                                              |
                                              | OnWorkerStart: Attach + TLS init
                                              | OnWorkerStop : Detach + Cleanup
                                              v
                                    Mono Runtime / Domain
```

#### 10.2.3 引擎改动点（建议落位）

> 具体文件以 UE 版本为准（UE5.2/5.3/5.4 位置略有差异），以下为“典型改动点”。

1) **Worker 生命周期钩子**
   - `Engine/Source/Runtime/Core/Private/Async/Fundamental/Scheduler.cpp`
     - `FScheduler::CreateWorker`：在线程创建后触发 `OnWorkerStart`。
     - `FScheduler::WorkerLoop`：在循环退出前触发 `OnWorkerStop`。
   - 需要新增可注册的 Hook 接口（例如 `FWorkerLifecycleHooks`），供 UnrealCSharp 注册回调。

2) **Managed Task 标记机制**
   - 扩展任务结构，使“需要托管上下文”的 Task 有显式标记：
     - 方案 A：在 `UE::Tasks` 层新增 `EManagedTaskFlags`（仅 C# 调用路径设置）。
     - 方案 B：在 TaskGraph internal call 处包裹 `FManagedTaskScope`，显式标注 managed-only。
   - 目的：避免所有任务都付出 managed attach 检查成本。

3) **PIE Stop/Reload 同步点**
   - 在 Editor 的 Stop/Reload 流程中增加可插拔同步点：
     - 进入 Stop：禁止新增 managed task。
     - 等待 in-flight managed task 归零。
     - 允许 Domain unload/reload。
   - UnrealCSharp 插件中已实现 fence，可在引擎侧暴露“统一 stop hook”来调用。

#### 10.2.4 Managed Worker 上下文与 Domain 切换策略

需要引擎侧提供“托管线程上下文”，并确保与 Domain reload 兼容。

- **Worker TLS 内容**
  - `MonoThread*` 或等价的托管线程句柄。
  - `DomainGeneration`：记录当前使用的 Domain 版本号。
  - `bAttached`：是否已 attach。

- **OnWorkerStart**
  - `mono_thread_attach(rootDomain)`（或当前主 Domain）。
  - 初始化 TLS（线程名、DomainGeneration）。

- **OnTaskExecute（managed task only）**
  - 若 `DomainGeneration` 过期：切换到新 Domain（`mono_domain_set(newDomain)`）。
  - 确保 TLS 处于“managed”状态后再调用 C#。

- **OnWorkerStop**
  - 仅在线程真正退出时 `mono_thread_detach`。
  - Debug 模式下可保持附着直到进程结束，避免 debugger-agent TLS 断言。

#### 10.2.5 PIE Stop/Reload 流程（ASCII）

```
Stop PIE
  |
  | DisableManagedJobExecution()
  | WaitForManagedJobDrain()
  | ForEachWorker: SwitchToRootDomain()
  v
Unload/Reload Managed Domain
  |
  | DomainGeneration++
  | ResumeManagedJobs()
  v
Play PIE
```

#### 10.2.6 调试模式与 TLS 风险处理

- Debugger-agent 的 TLS 断言说明：**不能在调试模式下频繁 detach**。
- 建议策略：
  - Debug：**Attach once, keep attached**（只在 worker 退出时 detach）。
  - Non-Debug：可选择“attach once”或“task-level attach”，但需统一策略以避免不一致。
- 若 Debug 模式下必须 detach，需要引擎/Mono 侧提供“安全 detach”能力（需验证 Mono embed API）。

#### 10.2.7 验证要点（面向路径 B）

- PIE Stop/Start 多次循环，确保不会卡死或崩溃。
- Debug 模式开启时，worker 运行 C# 任务不触发 TLS 断言。
- 在高并发任务下，任务依赖图可正常收敛（`FGraphEvent` 依赖满足）。

#### 10.2.8 风险与代价

- 需要引擎源码修改与长期维护，升级成本高。
- Mono/Debugger 行为变化可能引入新的 TLS 断言或死锁风险。
- 若 UE 任务系统内部实现变更，Hook 点可能需要重做。

### 10.3 结论与选择建议

- **优先路径 A**：不改引擎，调试稳定，生命周期可控，可快速落地。
- **路径 B 为长期目标**：若必须“UE worker 直接执行托管逻辑 + 完全复用 UE 调度”，需准备引擎侧投入与长期维护成本。
