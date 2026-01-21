# UE TaskGraph 执行 C# 逻辑的探索总结（排除 Native Kernel）

## 适用范围与前提

- 仅总结“托管 C# 逻辑在 UE TaskGraph worker 上执行”的探索过程。
- 不包含 Native Kernel 方案与相关结论。
- 以本仓库当前实现为基线，示例代码引用现有文件并做最小化裁剪。

## 1. 最初的最小链路：在 TaskGraph worker 拉起 C# 逻辑

**结论**：最初的实现是“C# 调用 internal call -> C++ 侧投递 TaskGraph 任务 -> worker 执行 `Runtime_Invoke`”。这条链路能跑通，但对 PIE stop/start 的生命周期并不安全。

### 1.1 关键路径

```
GameThread (C#)
  |
  | TaskGraphBatch.ExecuteBatch(...)
  v
InternalCall (C++)
  |
  | CreateAndDispatchWhenReady (AnyBackgroundThreadNormalTask)
  v
TaskGraph worker
  |
  | Runtime_Invoke(ExecuteTask)
  v
C# ExecuteTask(...)
```

### 1.2 示例代码（最小链路）

**C++ 侧（投递 TaskGraph 任务并调用托管方法）**  
路径：`Plugins/UnrealCSharp/Source/UnrealCSharp/Private/Domain/InternalCall/FTaskGraph.cpp`

```cpp
FFunctionGraphTask::CreateAndDispatchWhenReady([StateHandle, Index, FoundMethod]()
{
    void* StateHandleParam = StateHandle;
    int32 IndexParam = Index;
    void* Params[2]{ &StateHandleParam, &IndexParam };

    MonoObject* Exception = nullptr;
    (void)FMonoDomain::Runtime_Invoke(FoundMethod, nullptr, Params, &Exception);

    if (Exception != nullptr)
    {
        FMonoDomain::Unhandled_Exception(Exception);
    }
}, TStatId(), nullptr, ENamedThreads::AnyBackgroundThreadNormalTask);
```

**C# 侧（发起 Batch，并在 C# 中执行任务）**  
路径：`Plugins/UnrealCSharp/Script/UE/Library/TaskGraphBatch.cs`

```csharp
public static void ExecuteBatch(Action<int> executeIndex, int taskCount)
{
    var state = new BatchState { ExecuteIndex = executeIndex };
    var handle = GCHandle.Alloc(state);

    try
    {
        FTaskGraphImplementation.FTaskGraph_ExecuteBatchImplementation(
            (nint)GCHandle.ToIntPtr(handle),
            taskCount,
            wait: true);
    }
    finally
    {
        handle.Free();
    }
}

public static void ExecuteTask(nint stateHandle, int index)
{
    var handle = GCHandle.FromIntPtr((IntPtr)stateHandle);
    var state = (BatchState)handle.Target!;
    state.ExecuteIndex(index);
}
```

### 1.3 初期问题埋点

- TaskGraph worker 是常驻线程池，跨 PIE 复用。
- Worker 一旦 `mono_thread_attach`，托管线程状态可能跨 PIE 残留。
- Stop/Reload 缺少全局同步点，导致新一轮 `LoadFromStream` 等待旧线程状态收敛，出现卡死。

## 2. PIE 卡死后的安全机制：借鉴 Unity Job System

**结论**：参考 Unity Job System 的工程经验，补齐“全局屏障 + 托管线程身份可收敛 + 纯计算约束”，解决了绝大多数 PIE 卡死问题，但调试模式仍需特殊处理。

### 2.1 Unity 经验抽象为工程规则

1) **全局屏障**：Stop/Reload 前禁止新任务进入，等待 in-flight 归零。  
2) **线程身份可收敛**：worker 可常驻，但托管身份不能跨重载残留。  
3) **Job 只做纯计算**：不触碰 UObject/资源加载，不阻塞 GameThread。

### 2.2 安全机制流程（ASCII）

**Stop/Reload 过程**

```
GameThread (Stop PIE)
  |
  | DisableManagedJobExecution()
  | WaitForManagedJobDrain()
  v
Unload/Reload Managed Domain
```

**Worker 托管执行过程**

```
TaskGraph worker
  |
  | TryEnterManagedJobExecution()
  | EnsureThreadAttached()
  v
Runtime_Invoke(Managed Method)
  |
  | (Editor) EnsureThreadDetached()
  v
LeaveManagedJobExecution()
```

### 2.3 示例代码（托管 Job 护栏）

**C++ 侧托管护栏封装**  
路径：`Plugins/UnrealCSharp/Source/UnrealCSharp/Private/Domain/InternalCall/FTaskGraph.cpp`

```cpp
struct FManagedJobScope
{
    FManagedJobScope()
        : bEntered(FMonoDomain::TryEnterManagedJobExecution())
        , bDetachOnExit(FMonoDomain::ShouldDetachAfterManagedJob() && !IsInGameThread())
    {
        if (bEntered)
        {
            FMonoDomain::EnsureThreadAttached();
        }
    }

    ~FManagedJobScope()
    {
        if (!bEntered) return;
        if (bDetachOnExit) { FMonoDomain::EnsureThreadDetached(); }
        FMonoDomain::LeaveManagedJobExecution();
    }
};
```

### 2.4 调试模式的特殊处理

- Debugger-agent 的 TLS 断言表明：**调试模式下不能频繁 detach**。
- 因此在 Debug 模式关闭 per-job detach，只在 Stop/Reload 的 fence 处保证收敛。

## 3. 安全机制仍不够稳定：两条更完整的路线

**背景**：即便引入 fence + attach/detach，调试器 TLS、Domain Reload 与 UE worker 常驻仍可能在极端情况下触发不稳定。为此提出两条更稳妥路线。

### 3.1 路线 A：托管 Worker 池 + TaskGraph 只做“调度与依赖”

**核心思路**：UE TaskGraph 负责依赖图与完成回调，纯计算在 C# 线程池执行。这样保留 UE 的任务图语义，但避免 UE worker 与 Mono TLS 冲突。

```
GameThread
  |
  | ScheduleManagedJob -> GraphEvent
  v
UE TaskGraph (依赖/回调)
  |
  | Enqueue to C# Job Queue
  v
Managed Worker Pool (C#)
  |
  | Execute -> Complete(handle)
  v
GraphEvent completes
```

**优点**

- Debug 友好，TLS 冲突风险低。
- Stop/Reload 可直接 StopAndDrain，生命周期可控。
- TaskGraph 依赖语义仍可复用。

**代价**

- 多一次跨语言调度开销。
- UE 原生的 work-stealing 只在“依赖图”层生效，执行层在 C# 池内。

### 3.2 路线 B：改引擎，让 UE Worker 直接跑托管逻辑

**核心思路**：引擎侧补齐 worker 生命周期钩子与托管线程上下文，让 UE worker 直接成为“托管线程”，从而完全复用 UE 任务系统。

#### 3.2.1 引擎侧改动要点

- **Worker 生命周期钩子**（`FScheduler::CreateWorker` / `FScheduler::WorkerLoop`）
  - OnWorkerStart：`mono_thread_attach` + TLS 初始化。
  - OnWorkerStop：线程退出时 `mono_thread_detach`。
- **Managed Task 标记**
  - 仅对“托管任务”走 managed attach 路径，避免所有任务付出成本。
- **PIE Stop/Reload 同步点**
  - Stop 时禁入新任务 + 等待 in-flight 归零 + 切回 root domain。

#### 3.2.2 Domain 版本与 TLS 管理（ASCII）

```
UE Worker Thread
  |
  | TLS: { MonoThread*, DomainGeneration, bAttached }
  |
  | OnTaskExecute (managed)
  |   if DomainGeneration stale -> switch domain
  |   Runtime_Invoke(...)
  v
Managed Domain
```

#### 3.2.3 调试模式处理

- Debug：worker attach 一次，直到线程退出才 detach。
- Non-Debug：可选择 attach-once 或 task-level attach，但需统一策略。

#### 3.2.4 风险与代价

- 引擎源码改造与维护成本高。
- Mono/Debugger 版本变化可能重新引入 TLS 断言。
- UE 内部线程模型变更时，Hook 点可能失效。

## 4. 本仓库相关文件

- 任务投递与托管执行：`Plugins/UnrealCSharp/Source/UnrealCSharp/Private/Domain/InternalCall/FTaskGraph.cpp`
- 托管 fence 与 attach/detach：`Plugins/UnrealCSharp/Source/UnrealCSharpCore/Public/Domain/FMonoDomain.h`
- 托管 fence 实现：`Plugins/UnrealCSharp/Source/UnrealCSharpCore/Private/Domain/FMonoDomain.cpp`
- C# 侧批量执行：`Plugins/UnrealCSharp/Script/UE/Library/TaskGraphBatch.cs`
- C# 侧探针：`Plugins/UnrealCSharp/Script/UE/Library/TaskGraphProbe.cs`

## 5. 小结

当前托管执行链路已经通过 “fence + attach/detach” 解决了大部分 PIE 卡死问题，但如果目标是“像 Unity Job System 一样稳定”，需要在路线 A（托管池 + TaskGraph 依赖）与路线 B（引擎改造）之间做取舍。路线 A 更稳且成本低，路线 B 语义最完整但维护成本最高。
