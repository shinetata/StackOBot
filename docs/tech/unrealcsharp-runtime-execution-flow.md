# UnrealCSharp × UE5：从项目启动到 C# 执行的完整运行时流程（代码级）

目标：把“UE 运行时在哪个线程拉起 Mono、什么时候进入 Mono 执行 C#、什么时候回到 UE”的**完整调用链**讲清楚。  
说明：本仓库使用 UnrealCSharp（Mono/.NET 8）作为嵌入式运行时，下面流程全部以本仓库 `Plugins/UnrealCSharp/` 的真实代码为依据。

---

## TL;DR

- **Mono 不是单独一个线程**：UnrealCSharp 调用 `mono_runtime_invoke(_array)` 时，托管 C# 代码就**在当前调用线程同步执行**（通常是 UE GameThread）。
- **“同线程调度”= 普通函数调用栈切换**：UE → 插件 C++ → Mono → C# → 返回。
- **“跨线程调度”= 显式派发到 GameThread**：
  - UE 侧用 `AsyncTask(ENamedThreads::GameThread, ...)` 把敏感操作切回主线程；
  - C# 侧用 `Script.CoreUObject.SynchronizationContext` 把后台线程的任务 Post/Send 回“初始化该 Context 的线程”（实际就是 GameThread）。

---

## 0) 参与者速览（你要在图里认得它们）

UE 模块与监听器（C++）：
- `FUnrealCSharpCoreModule`：`Plugins/UnrealCSharp/Source/UnrealCSharpCore/Private/UnrealCSharpCore.cpp`
- `FEngineListener`：`Plugins/UnrealCSharp/Source/UnrealCSharpCore/Private/Listener/FEngineListener.cpp`
- `FUnrealCSharpModule`：`Plugins/UnrealCSharp/Source/UnrealCSharp/Private/UnrealCSharp.cpp`
- `FCSharpEnvironment`：`Plugins/UnrealCSharp/Source/UnrealCSharp/Private/Environment/FCSharpEnvironment.cpp`
- `FDomain`（Tickable）：`Plugins/UnrealCSharp/Source/UnrealCSharp/Private/Domain/FDomain.cpp`
- `FMonoDomain`（真正调用 mono_* API）：`Plugins/UnrealCSharp/Source/UnrealCSharpCore/Private/Domain/FMonoDomain.cpp`
- UObject 监听：`FUObjectListener`：`Plugins/UnrealCSharp/Source/UnrealCSharp/Private/Listener/FUObjectListener.cpp`
- UE→C# 委托桥：`UDelegateHandler` / `UMulticastDelegateHandler`：
  - `Plugins/UnrealCSharp/Source/UnrealCSharp/Private/Reflection/Delegate/DelegateHandler.cpp`
  - `Plugins/UnrealCSharp/Source/UnrealCSharp/Private/Reflection/Delegate/MulticastDelegateHandler.cpp`
- UE 调用（C#→UE 的一端）：`FUnrealFunctionDescriptor`：
  - `Plugins/UnrealCSharp/Source/UnrealCSharp/Public/Reflection/Function/FUnrealFunctionDescriptor.inl`

C# 同步上下文（托管侧）：
- `Script.CoreUObject.SynchronizationContext`：`Plugins/UnrealCSharp/Script/UE/CoreUObject/SynchronizationContext.cs`

---

## 1) 整体执行流程图（从启动到“可以执行 C#”）

> 线程说明：绝大多数模块加载/PIE/世界 Tick 都发生在 **UE GameThread**，因此下图默认主流程都在 GameThread。

```
+-------------------------------+         +------------------------------------+
| Editor（PIE）                 |         | Packaged（非编辑器）                |
| FEditorListener.cpp           |         | FEngineListener.cpp                 |
| - PreBeginPIE -> SetActive(T) |         | - PostDefault -> SetActive(T)       |
| - CancelPIE  -> SetActive(F)  |         | - OnPreExit   -> SetActive(F)       |
+---------------+---------------+         +------------------+-----------------+
                |                                        |
                v                                        v
+--------------------------------------------------------------------------------------+
| FEngineListener::SetActive(true)                                                    |
| Plugins/UnrealCSharp/Source/UnrealCSharpCore/Private/Listener/FEngineListener.cpp   |
| - 读取 UUnrealCSharpSetting::IsEnableImmediatelyActive()                            |
| - 满足条件才会调用 FUnrealCSharpCoreModule::SetActive(true)                         |
+-----------------------------------------+--------------------------------------------+
                                          |
                                          v
+--------------------------------------------------------------------------------------+
| FUnrealCSharpCoreModule::SetActive(true)                                             |
| Plugins/UnrealCSharp/Source/UnrealCSharpCore/Private/UnrealCSharpCore.cpp            |
| -> FUnrealCSharpCoreModuleDelegates::OnUnrealCSharpCoreModuleActive.Broadcast()      |
+-----------------------------------------+--------------------------------------------+
                                          |
                                          v
+--------------------------------------------------------------------------------------+
| FUnrealCSharpModule::OnUnrealCSharpCoreModuleActive()                                |
| Plugins/UnrealCSharp/Source/UnrealCSharp/Private/UnrealCSharp.cpp                    |
| -> FUnrealCSharpModuleDelegates::OnUnrealCSharpModuleActive.Broadcast()              |
+-----------------------------------------+--------------------------------------------+
                                          |
                                          v
+--------------------------------------------------------------------------------------+
| FCSharpEnvironment::OnUnrealCSharpModuleActive()                                     |
| Plugins/UnrealCSharp/Source/UnrealCSharp/Private/Environment/FCSharpEnvironment.cpp  |
| -> FCSharpEnvironment::Initialize()  （这里开始真正拉起 Mono）                        |
+-----------------------------------------+--------------------------------------------+
                                          |
                                          v
+--------------------------------------------------------------------------------------+
| new FDomain(params)                                                                   |
| Plugins/UnrealCSharp/Source/UnrealCSharp/Private/Domain/FDomain.cpp                  |
| -> FMonoDomain::Initialize(params)                                                    |
|    Plugins/UnrealCSharp/Source/UnrealCSharpCore/Private/Domain/FMonoDomain.cpp       |
|    - mono_jit_init("UnrealCSharp")                                                    |
|    - RegisterBinding(): mono_add_internal_call(...)                                   |
| -> InitializeSynchronizationContext()                                                 |
|    - C#：Script.CoreUObject.SynchronizationContext.Initialize()                       |
|    - 缓存 Tick thunk：SynchronizationContext.Tick(float)                               |
+-----------------------------------------+--------------------------------------------+
                                          |
                                          v
+--------------------------------------------------------------------------------------+
| FUnrealCSharpModuleDelegates::OnCSharpEnvironmentInitialize.Broadcast()               |
| Plugins/UnrealCSharp/Source/UnrealCSharp/Private/Environment/FCSharpEnvironment.cpp  |
| 订阅者示例：                                                                           |
| - FCSharpBind::OnCSharpEnvironmentInitialize()（绑定 CDO）                             |
|   Plugins/UnrealCSharp/Source/UnrealCSharp/Private/Registry/FCSharpBind.cpp          |
| - FDynamicRegistry::RegisterDynamic()（扫描 attribute 绑定动态类）                    |
|   Plugins/UnrealCSharp/Source/UnrealCSharp/Private/Registry/FDynamicRegistry.cpp     |
+--------------------------------------------------------------------------------------+

结论：到这里为止，Mono Domain 已初始化，internal calls 已注册，C# 同步上下文已建立，可以开始“进入 Mono 执行 C#”。
```

---

## 2) “同线程调度”的核心：UE GameThread 如何进入 Mono 执行 C#，再返回

### 2.1 每帧 Tick：UE → C#（SynchronizationContext.Tick）

UnrealCSharp 把 `FDomain` 设计成 `FTickableGameObject`，所以 UE 每帧都会在 GameThread 调 `FDomain::Tick`：

- `FDomain::Tick(float DeltaTime)`：`Plugins/UnrealCSharp/Source/UnrealCSharp/Private/Domain/FDomain.cpp`
  - 内部调用 `SynchronizationContextTick(DeltaTime, &Exception)`

`SynchronizationContextTick` 是一个 **C# 静态方法的 unmanaged thunk**，在初始化阶段拿到：

- `FDomain::InitializeSynchronizationContext()`：
  - `Runtime_Invoke(InitializeMonoMethod, ...)` → 执行 `Script.CoreUObject.SynchronizationContext.Initialize()`
  - `Method_Get_Unmanaged_Thunk(TickMonoMethod)` → 缓存 `Tick(float)` 的函数指针

C# 侧 `Script.CoreUObject.SynchronizationContext`：
- `Plugins/UnrealCSharp/Script/UE/CoreUObject/SynchronizationContext.cs`
  - `Initialize()` 记录当前托管线程 ID（此刻正处于 GameThread 的调用栈里）
  - `Post/Send(...)` 把任务塞进队列
  - `Tick()` 在该线程执行队列任务

所以每一帧都存在一个固定的“进入 Mono 执行 C#”入口（哪怕队列为空也是一次调用）：

```
+------------------------------+       +------------------------------------------+
| UE GameThread 每帧 Tick      |  ---> | FDomain::Tick(DeltaTime)                 |
|（FTickableGameObject）       |       | UnrealCSharp/Private/Domain/FDomain.cpp  |
+------------------------------+       +----------------------+-------------------+
                                                         |
                                                         v
                                         +------------------------------------------+
                                         | (unmanaged thunk)                        |
                                         | Script.CoreUObject.SynchronizationContext.Tick |
                                         | Plugins/UnrealCSharp/Script/UE/CoreUObject/SynchronizationContext.cs |
                                         +----------------------+-------------------+
                                                         |
                                                         v
                                         +------------------------------------------+
                                         | 执行 Post/Send 队列中的任务（回到主线程） |
                                         +------------------------------------------+
```

### 2.2 UObject 创建/异步加载：UE（可能非 GT）→ 记录 → GT 执行 Bind/Constructor（进入 C#）

UObject 可能在非 GameThread 被创建（例如异步加载阶段）。插件使用双路径：

- `FCSharpEnvironment::NotifyUObjectCreated(...)`：`Plugins/UnrealCSharp/Source/UnrealCSharp/Private/Environment/FCSharpEnvironment.cpp`
  - `if (IsInGameThread()) { Bind<true>(InObject); }`
  - `else { AsyncLoadingObjectArray.Add(InObject); }`

随后在 GameThread 的异步加载 flush 回调中统一处理：

- `FCSharpEnvironment::OnAsyncLoadingFlushUpdate()`：同文件
  - 取出 “已完成加载且可绑定” 的 UObject
  - 对每个对象：
    - `Bind<true>(PendingBindObject)`
    - `FDomain::Object_Constructor(FoundMonoObject)`（进入 Mono 调 `.ctor`）

这一段的“线程与进入 C#”非常典型：

```
+-------------------------------+       +-----------------------------------------------+
| UE 非 GameThread（可能）       |       | UE GameThread                                |
+-------------------------------+       +-----------------------------------------------+
| UObject created               |       | (每帧/异步加载 flush 回调)                     |
| GUObjectArray listener        |       | FCSharpEnvironment::OnAsyncLoadingFlushUpdate |
| FUObjectListener.cpp          |       | FCSharpEnvironment.cpp                        |
+---------------+---------------+       +----------------------+------------------------+
                |                                      |
                v                                      |
 +------------------------------+                      |
 | FCSharpEnvironment::NotifyUObjectCreated            |
 | FCSharpEnvironment.cpp                               |
 | - if IsInGameThread(): Bind<true>(obj)              |
 | - else: AsyncLoadingObjectArray.Add(obj)            |
 +------------------------------+                      |
                |                                      |
                | (仅入队，不进入 Mono)                 |
                +------------------------------+-------+
                                               |
                                               v
                             +-----------------------------------------------+
                             | 取出 PendingBindObjects                        |
                             | - Bind<true>(obj)  建立 UObject<->MonoObject 映射 |
                             +----------------------+------------------------+
                                                    |
                                                    v
                             +-----------------------------------------------+
                             | FDomain::Object_Constructor(FoundMonoObject)  |
                             | -> FMonoDomain::Object_Constructor            |
                             | -> mono_runtime_invoke(.ctor) 进入 C# 构造      |
                             +-----------------------------------------------+
```

### 2.3 UE 委托/事件：UE → C#（mono_runtime_invoke_array）

当 UE 的委托触发时（通常仍在 GameThread），插件用 handler 拦截 `ProcessEvent`，然后调用 C# 绑定的方法：

- `UDelegateHandler::ProcessEvent(...)`：`.../DelegateHandler.cpp`
- `UMulticastDelegateHandler::ProcessEvent(...)`：`.../MulticastDelegateHandler.cpp`

真正进入 C# 的地方在：
- `FCSharpDelegateDescriptor::CallDelegate(...)`：`.../FCSharpDelegateDescriptor.cpp`
  - 组装 `MonoArray` 参数
  - 调用 `Runtime_Invoke_Array(...)`

最终落到：
- `FMonoDomain::Runtime_Invoke_Array(...)`：`.../FMonoDomain.cpp`
  - `mono_runtime_invoke_array(...)`

这条链路也是完全同步的“同线程进入/同线程返回”：

```
+------------------------------+       +-----------------------------------------------+
| UE GameThread                |       | UnrealCSharp（C++ 桥接）                       |
+------------------------------+       +-----------------------------------------------+
| UE 触发委托/事件             |       |                                               |
| UObject::ProcessEvent        |------>| UDelegateHandler::ProcessEvent                |
| (或 Multicast)               |       | DelegateHandler.cpp / MulticastDelegateHandler.cpp |
+------------------------------+       +----------------------+------------------------+
                                                    |
                                                    v
                             +-----------------------------------------------+
                             | FCSharpDelegateDescriptor::CallDelegate       |
                             | FCSharpDelegateDescriptor.cpp                 |
                             | - 打包 UE Parms -> MonoArray                  |
                             | - Runtime_Invoke_Array(...)                   |
                             +----------------------+------------------------+
                                                    |
                                                    v
                             +-----------------------------------------------+
                             | FMonoDomain::Runtime_Invoke_Array             |
                             | FMonoDomain.cpp                               |
                             | -> mono_runtime_invoke_array 进入 C#           |
                             +----------------------+------------------------+
                                                    |
                                                    v
                             +-----------------------------------------------+
                             | C# 方法执行完返回（同步）                       |
                             | - 可回写 out/return 到 UE Parms buffer          |
                             +-----------------------------------------------+
```

---

## 3) “跨线程调度”的两条通道：UE 侧派发 & C# 侧派发

### 3.1 UE 侧：把敏感操作派发回 GameThread（AsyncTask）

当 internal call 可能从任意线程进入（例如 C# ThreadPool 线程），插件会对部分操作显式派发回 GameThread：

- 示例：容器解绑
  - `Plugins/UnrealCSharp/Source/UnrealCSharp/Private/Domain/InternalCall/FRegisterArray.cpp`
  - `UnRegisterImplementation(...)`：
    - `AsyncTask(ENamedThreads::GameThread, [...]{ RemoveContainerReference(...); })`

含义：**桥接层不保证线程安全时，作者会手动把它拉回 GameThread 执行。**

```
+--------------------------------------+    +---------------------------------------------------+    +---------------------------------------------+
| C# 后台线程 / ThreadPool              |--->| UnrealCSharp C++ InternalCall（当前线程）           |--->| TaskGraph 派发：AsyncTask(GameThread, ...)   |
|（await 后续 / 自定义线程）            |    | FRegisterArray::UnRegisterImplementation         |    |（入队，不立即执行）                           |
+--------------------------------------+    +---------------------------+-----------------------+    +----------------------+----------------------+
                                                             |                                                     |
                                                             |（internal call 返回 C#，异步挂起）                      v
                                                             |                                     +---------------------------------------------+
                                                             +------------------------------------>| UE GameThread 执行 lambda                 |
                                                                                                   | RemoveContainerReference(...)              |
                                                                                                   +---------------------------------------------+
```

### 3.2 C# 侧：把“需要触碰 UE 的代码”派发到主线程执行（SynchronizationContext）

如果你的游戏逻辑在 C# 后台线程算完了结果，下一步要改 UE 对象（Spawn/SetActorLocation/改材质等），推荐使用：

- `System.Threading.SynchronizationContext.Current.Post(...)` / `Send(...)`

其机制是：
- `Post/Send` 只是把任务塞到 `TaskList`
- 真正执行发生在 `SynchronizationContext.Tick()`
- `Tick()` 由 `FDomain::Tick()` 每帧在 GameThread 驱动

这就是 UnrealCSharp 在“C#-First（Unity-like）”模式下，最关键的“跨线程回主线程”通道。

```
+----------------------------------------+    +------------------------------------------+    +------------------------------------------+
| C# 后台线程 / ThreadPool                |--->| C# SynchronizationContext（任务队列）       |    | UE GameThread                             |
|（算完数据，准备修改 UE）                |    | Script.CoreUObject.SynchronizationContext |    |（驱动 FDomain::Tick）                      |
+----------------------------------------+    +---------------------------+--------------+    +----------------------+-------------------+
| SynchronizationContext.Current.Post     |    | Post/Send：只入队，不执行                   |                       |
+---------------------------+------------+    +---------------------------+--------------+                       |
                            |                                               |                                  |
                            v                                               |                                  v
                    +------------------------------------+                 |                    +------------------------------------------+
                    |（Post 返回，后台线程继续）           |                 +------------------->| FDomain::Tick(DeltaTime)                 |
                    +------------------------------------+                                      | -> SynchronizationContext.Tick()        |
                                                                                               +----------------------+-------------------+
                                                                                                                  |
                                                                                                                  v
                                                                                               +------------------------------------------+
                                                                                               |（GameThread）执行队列任务                  |
                                                                                               | -> 安全调用 UE API（internal call）       |
                                                                                               +------------------------------------------+
```

---

## 4) C# 调 UE（以及“什么时候回到 UE”）——以真实 C++ 调用点说明

当 C# 调用 UE API（比如代理类里某个 `K2_` 方法、`UGameplayStatics.*`），典型路径是：

1) C# 代理方法调用某个 internal call（形式在 C# 里表现为 `FFunctionImplementation.*` 或类似封装）。  
2) C++ 侧将参数打包成 `Params` buffer。  
3) 通过 `UObject::ProcessEvent`（脚本/蓝图函数）或 `UFunction::Invoke`（原生函数）执行。  
4) 执行完成后把返回值/out 参数写回 buffer。  
5) internal call 返回到 C#，C# 从 buffer 解包得到返回值/out。

在本仓库里，“C++ 调 UE 的核心动作”可以直接看到：

- `FUnrealFunctionDescriptor::Call*`：`Plugins/UnrealCSharp/Source/UnrealCSharp/Public/Reflection/Function/FUnrealFunctionDescriptor.inl`
  - 多处直接：`InObject->UObject::ProcessEvent(Function.Get(), Params);`
  - 或者构造 `FFrame` 调 `Function->Invoke(...)`

因此你可以把“什么时候回到 UE 运行环境”理解为：

- 当 C# 发起 internal call 后，C++ 进入 `ProcessEvent/Invoke` 那一刻，执行权回到 UE 的函数系统；
- 当 `ProcessEvent/Invoke` 返回后，回到 C++ 桥接层；
- 当桥接层返回后，回到 C# 调用点。

这整个过程是同步的（除非你显式用 TaskGraph/AsyncTask 自己做异步化）。

---

## 5) 终止流程（从 PIE/退出回收 Mono）

停用路径与激活相反：

- `FEngineListener::SetActive(false)`（Editor：CancelPIE；Packaged：OnPreExit）
  - `FUnrealCSharpCoreModule::SetActive(false)`：广播 Core InActive
  - `FUnrealCSharpModule::OnUnrealCSharpCoreModuleInActive()`：广播 UnrealCSharp InActive
  - `FCSharpEnvironment::OnUnrealCSharpModuleInActive()`：`Deinitialize()`
    - `delete Domain` → `FDomain::~FDomain()` → `FDomain::Deinitialize()` → `FMonoDomain::Deinitialize()`

这意味着：**退出 PIE / 进程退出时，Mono 域会被卸载，internal call/注册表被释放**（再次进入 PIE 会重新初始化）。

---

## 6) 一张“时序化总览图”（不区分 Editor/Packaged，仅展示关键切入点）

```
+-------------------------------+
| T0（GameThread）              |
| 模块加载：UnrealCSharpCore     |
|        UnrealCSharp            |
+---------------+---------------+
                |
                v
+----------------------------------------------+
| T1（GameThread）                              |
| Editor: PreBeginPIE / Packaged: PostDefault  |
| -> FEngineListener::SetActive(true)          |
+---------------+------------------------------+
                |
                v
+-------------------------------------------------------------+
| T2（GameThread）                                            |
| CoreModuleActive -> ModuleActive -> FCSharpEnvironment::Init |
+---------------+---------------------------------------------+
                |
                v
+-------------------------------------------------------------+
| T3（GameThread）                                            |
| new FDomain -> FMonoDomain::Initialize                       |
| - mono_jit_init / mono_add_internal_call                     |
+---------------+---------------------------------------------+
                |
                v
+-------------------------------------------------------------+
| T3.1（GameThread -> Mono）                                  |
| InitializeSynchronizationContext                             |
| -> Runtime_Invoke( SynchronizationContext.Initialize )       |
+---------------+---------------------------------------------+
                |
                v
+-------------------------------------------------------------+
| T4（每帧，GameThread -> Mono）                               |
| FDomain::Tick -> SynchronizationContext.Tick                 |
| - 执行 Post/Send 队列任务（回到 GameThread 安全区）           |
+---------------+---------------------------------------------+
                |
                +-------------------------------+----------------------------------+
                |                               |
                v                               v
+----------------------------------------------+  +----------------------------------------------+
| T5（GameThread）                              |  | T6（AsyncLoading/其他线程）                   |
| UObject::ProcessEvent（委托/事件触发）           |  | UObject created -> NotifyUObjectCreated       |
+---------------+------------------------------+  +---------------------------+------------------+
                |                                                 |
                v                                                 v
+----------------------------------------------+  +----------------------------------------------+
|（GameThread -> Mono）                        |  |（GameThread）                                 |
| mono_runtime_invoke_array(...) -> C# 回调     |  | OnAsyncLoadingFlushUpdate -> Bind + .ctor      |
+----------------------------------------------+  | -> mono_runtime_invoke(.ctor)                  |
                                                  +---------------------------+------------------+
                                                                              |
                                                                              v
                                                  +----------------------------------------------+
                                                  | T_end（GameThread）                          |
                                                  | CancelPIE/OnPreExit -> SetActive(false)      |
                                                  | Domain/Mono Deinitialize                      |
                                                  +----------------------------------------------+
```

---

## 7) 你应该如何用这张图做排障

- “为什么 C# 代码一定要在主线程改 UE 对象？”
  - 因为 UE 的 `ProcessEvent/UObject` 大多不是线程安全的；本插件也大量用 `IsInGameThread()` / `AsyncTask(GameThread, ...)` 暗示这一点。
- “为什么 async/Task 有时会随机崩？”
  - 因为 `await` 后续可能落到 ThreadPool；这时如果直接触碰 UE 对象，就绕过了 GameThread 约束。
- “我怎么把后台计算结果安全应用到 UE？”
  - 用 `SynchronizationContext.Post/Send`：它最终会在 `FDomain::Tick -> SynchronizationContext.Tick`（GameThread）里执行。
