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
- 托管逻辑结束后：Editor 下强制 `mono_thread_detach`，保证跨 PIE 不残留。
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
- Editor 下 `ShouldDetachAfterManagedJob` 默认启用

并在 `FMonoDomain::Initialize/Deinitialize` 中自动启用与收敛。

### 6.2 C++：TaskGraph worker 进入托管区的统一护栏
- `Plugins/UnrealCSharp/Source/UnrealCSharp/Private/Domain/InternalCall/FTaskGraph.cpp`

改动点：
- worker lambda 统一使用 `FManagedJobScope` 包装。
- 若闸门关闭或 Domain 未就绪，任务直接返回。
- Editor 下每次托管执行后 detach，避免跨 PIE 残留。

## 7. 验证步骤（可复现）

1. 启动 Editor，进入任意可运行关卡。
2. 调用 `TaskGraphBatch.ExecuteBatch(...)` 或 `TaskGraphProbe.Enqueue(...)`，确认 worker 执行托管逻辑。
3. Stop PIE，然后再次 Play：
   - 预期：不再卡死在 `LoadFromStream(Game.dll)`。
4. 如需压力验证，可执行 `TaskGraphPerfComparison.Run(...)` 观察多次 PIE 仍可反复进入。

## 8. 风险与回滚

### 风险
- Editor 下每任务 detach 存在额外开销（但可接受）。
- 若托管任务内部阻塞/死循环，Stop PIE 会等待 in-flight 归零而长时间停顿。

### 回滚
- 回滚 `FMonoDomain` 与 `FTaskGraph` 中的 fence/attach/detach 改动即可恢复旧行为。

## 9. 结论

借鉴 Unity 的“全局屏障 + 线程身份收敛”经验，结合 UnrealCSharp 的现有结构，本方案在不引入 Native Kernel 的前提下，提供了可落地、可调试、可回滚的 TaskGraph worker 托管执行能力，核心目标是**稳定的 PIE 调试与可控的生命周期管理**。
