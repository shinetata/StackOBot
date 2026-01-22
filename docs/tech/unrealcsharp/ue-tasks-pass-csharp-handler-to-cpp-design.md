# UE::Tasks：在 C++ 参数中传入 C# 处理函数（delegate -> MonoMethod -> unmanaged thunk）设计草案

> 面向读者：完全不熟悉 UnrealCSharp/Mono embedding 的同学（小白）。
>
> 目标一句话：**让 C# 把一个“处理 slice 的函数”作为参数传给 C++，C++ 在 UE::Tasks worker 线程里直接用 thunk 调用该函数**，从而避免把处理逻辑写死在固定入口（例如 `ExecuteSlice`）里。

## 0. 你想要的是什么（问题复述）

你希望 `FTasksSlice.ExecuteBatchImplementation(...)` 这种 C++ 入口，在参数里直接接收一个 C# 函数（例如 `Action<nint,int,int>`），然后：

1) C++ 仍负责切分 `[0,length)` 为多个 slice，并发调度到 UE worker；
2) 每个 worker 任务对一个 slice 调用“你传进来的 C# 函数”；
3) 不想把处理逻辑固定写死在 `UETasksSliceBatch.ExecuteSlice` 这种固定入口里。

## 1. UnrealCSharp 插件内部是否已有“C++ 接收 C# 函数作为参数”的先例？

有，而且是插件原生功能（不是我们后来加的 TaskGraph/Tasks）。

UnrealCSharp 内部已经支持：**C++ internal call 里接收 `MonoObject* InDelegate`（也就是 C# delegate），并把它解析成 `MonoMethod*` 存起来**。

最直观的例子是 Delegate 绑定系统：

- 接收 delegate 参数（internal call）：
  - `Plugins/UnrealCSharp/Source/UnrealCSharp/Private/Domain/InternalCall/FRegisterDelegate.cpp`
    - `BindImplementation(..., MonoObject* InDelegate)`
- 从 delegate 解析出 `MonoMethod*`：
  - `Plugins/UnrealCSharp/Source/UnrealCSharpCore/Private/Domain/FMonoDomain.cpp`
    - `FMonoDomain::Delegate_Get_Method(MonoObject* InDelegate)`

它最后是用通用的 `Runtime_Invoke_Array` 去调用（更通用，但开销更大）：

- `Plugins/UnrealCSharp/Source/UnrealCSharp/Private/Reflection/Function/FCSharpDelegateDescriptor.cpp`
  - `Runtime_Invoke_Array(InMethod, target, paramsArray)`

> 结论：**“把 C# 函数作为参数传进 C++”这件事，UnrealCSharp 插件内部已经验证过是可行的**。

## 2. 但你要的是“极致性能”：为什么不能直接复用 Delegate 系统的调用方式？

Delegate 系统的调用链路是为了“功能完整、参数/返回值通用”而设计的，所以它会：

- 把参数打包成 `object[]`
- 走 `mono_runtime_invoke_array`（`Runtime_Invoke_Array`）

这对性能不友好，尤其在你这种“短小、重复、纯计算”的 loop 中会被放大。

你现在追求的是：

- 用 `mono_method_get_unmanaged_thunk` 拿到 thunk；
- 在 C++ 里把 thunk cast 成函数指针直接调用；
- 避免 `Runtime_Invoke(_Array)`。

因此我们需要一套“delegate 仅用于确定方法身份，但实际执行走 thunk”的设计。

## 3. 设计目标（方案：delegate -> MonoMethod -> thunk）

核心思路：

1) C# 把一个 delegate 作为参数传给 internal call；
2) C++ 收到 `MonoObject* delegate` 后，用 `FMonoDomain::Delegate_Get_Method` 拿到 `MonoMethod*`；
3) C++ 对该 `MonoMethod*` 取 `FMonoDomain::Method_Get_Unmanaged_Thunk` 得到 thunk；
4) UE::Tasks worker 中直接调用 thunk（一次处理一个 slice）。

ASCII 流程图：

```
C# (调用方)
  |
  | 1) 传 dataPtr + length + taskCount + handler(delegate)
  v
C++ internal call (FTasksSlice.*)
  |
  | 2) handler(delegate) -> MonoMethod*
  | 3) MonoMethod* -> unmanaged thunk
  | 4) 切分 slice, Launch UE::Tasks
  v
UE::Tasks worker 线程
  |
  | 5) thunk(dataPtr, start, count, &exc)
  v
C# handler 方法体（开发者自定义逻辑）
```

## 4. 关键约束（必须讲清楚，否则必崩）

为了让 thunk 直调“稳定、可预测、快”，我们要加非常强的约束：

### 4.1 handler 必须是 static 且不捕获（禁止闭包/实例方法）

原因：

- `mono_method_get_unmanaged_thunk` 对 instance method 的调用约定更复杂（隐含 `this`/target），C++ 需要额外传 target；
- 捕获 lambda 在 C# 本质是“生成一个 closure 类 + 实例方法”，同样会变成 instance；
- 为了极致性能和实现简单，建议只支持 static。

如何判断（C++ 侧）：

- `MonoMethodSignature* sig = FMonoDomain::Method_Signature(method);`
- `FMonoDomain::Signature_Is_Instance(sig)` 若为 true，则拒绝（要求 static）。

### 4.2 handler 的签名必须固定（建议：`void Handler(nint data, int start, int count)`）

原因：

- C++ 需要把 thunk cast 成“确定的函数指针类型”；
- 签名不固定就无法安全 cast（错了就是野指针崩溃）。

推荐固定签名：

- 返回：`void`
- 参数：`(nint data, int start, int count)`

其中 `data` 指向连续内存（可以来自 NativeBuffer，也可以来自 pin 过的托管数组）。

### 4.3 “异步不等待（wait=false）”会引入生命周期问题（建议先只做同步版本）

如果 internal call 返回到 C#，但 UE::Tasks 仍在 worker 执行：

- 托管数组可能已经 unpin；
- 传进来的 delegate 可能被 GC 回收；
- 造成概率性崩溃。

因此，最小可行且安全的版本建议：

- 只提供同步 API（等价于 `wait=true`，内部强制等待）。

如果一定要支持异步，需要额外设计“把 data 和 delegate 都用 GCHandle 固定/持有直到任务完成”的机制（这会增加接口和复杂度）。

## 5. API 设计（两种选择）

### 5.1 选择 A：直接修改现有 `ExecuteBatch` 签名（会破坏兼容）

把 C# internal call 从：

- `FTasksSlice_ExecuteBatchImplementation(nint data, int length, int taskCount, bool wait)`

改成：

- `FTasksSlice_ExecuteBatchImplementation(nint data, int length, int taskCount, bool wait, Action<nint,int,int> handler)`

优点：入口少，看起来“就是在原方法参数里加了 handler”。  
缺点：需要同步改 C# 声明与所有调用点，属于破坏性变更。

### 5.2 选择 B：新增一个带 handler 的 internal call（推荐，兼容旧用例）

新增一个方法名：

- `FTasksSlice_ExecuteBatchWithHandlerImplementation(nint data, int length, int taskCount, bool wait, Action<nint,int,int> handler)`

优点：不破坏已有 `ExecuteBatch`、测试用例可渐进迁移。  
缺点：多一个入口（但对稳定性更友好）。

> 如果你后续确定旧入口不再需要，可以再做一次清理合并。

## 6. C++ 侧实现骨架（伪代码，解释用）

> 说明：下面是“设计草案”的伪代码，不代表仓库已实现。

关键点：

- 参数中接收 `MonoObject* InDelegate`（delegate 实例）
- 解析 `MonoMethod*`
- 校验 static + 参数个数（必要时校验参数类型）
- 获取 thunk 并在 worker 调用

```cpp
static void ExecuteBatchWithHandlerImpl(const void* Data, int32 Length, int32 TaskCount, bool bWait, MonoObject* InDelegate)
{
    // 0) 基础检查（Domain/Fence/参数合法性）

    // 1) delegate -> MonoMethod*
    MonoMethod* Method = FMonoDomain::Delegate_Get_Method(InDelegate);
    if (!Method) return;

    // 2) 校验：必须 static + paramCount == 3
    MonoMethodSignature* Sig = FMonoDomain::Method_Signature(Method);
    if (!Sig) return;
    if (FMonoDomain::Signature_Is_Instance(Sig)) return; // 禁止 instance
    if (FMonoDomain::Signature_Get_Param_Count(Sig) != 3) return;

    // 3) MonoMethod* -> unmanaged thunk
    void* ThunkRaw = FMonoDomain::Method_Get_Unmanaged_Thunk(Method);
    if (!ThunkRaw) return;

    using FHandlerThunk = void (*)(void*, int32, int32, MonoObject**);
    FHandlerThunk Thunk = reinterpret_cast<FHandlerThunk>(ThunkRaw);

    // 4) 切分 slice 并 Launch UE::Tasks
    // 5) worker：Attach + Thunk(data,start,count,&exc)
}
```

## 7. C# 侧用法（copy/paste 示例）

### 7.1 NativeBuffer：推荐路径（数据在 native，天然稳定）

```csharp
private static unsafe void AddOneKernel(nint data, int start, int count)
{
    var p = (int*)data;
    var end = start + count;
    for (var i = start; i < end; i++)
    {
        p[i] += 1;
    }
}

public static void RunNative(NativeBuffer<int> buf, int taskCount)
{
    // 这里把方法组 AddOneKernel 作为 delegate 传入（它是 static，不捕获）
    FTasksSliceImplementation.FTasksSlice_ExecuteBatchWithHandlerImplementation(
        buf.Ptr, buf.Length, taskCount, wait: true, AddOneKernel);
}
```

### 7.2 托管数组：必须 pin（否则指针不稳定）

```csharp
private static unsafe void AddOneKernel(nint data, int start, int count)
{
    var p = (int*)data;
    var end = start + count;
    for (var i = start; i < end; i++)
    {
        p[i] += 1;
    }
}

public static unsafe void RunManagedPinned(int[] arr, int taskCount)
{
    // 用 fixed 或 GCHandleType.Pinned，把 int[] 固定住再把指针传给 C++
    fixed (int* p = arr)
    {
        FTasksSliceImplementation.FTasksSlice_ExecuteBatchWithHandlerImplementation(
            (nint)p, arr.Length, taskCount, wait: true, AddOneKernel);
    }
}
```

> 注意：上面的 handler 访问的是 `int*` 指针；因此“托管数组版”也可以复用同一个 handler（只要你先 pin 并把指针传进来）。

## 8. 性能与缓存建议（可选）

如果你每帧/每轮都把同一个 handler 传给 C++，那么：

- `Delegate_Get_Method` + `Method_Get_Unmanaged_Thunk` 的成本虽然不算巨高，但会被循环放大；
- 可以在 C++ 侧做一个简单 cache：key 使用 `MonoMethod*`（并附带 DomainKey），value 是 thunk 指针。

注意点：

- Domain reload/PIE 重启后 `MonoMethod*` 可能失效，所以 cache 需要包含 DomainKey（类似我们现在 `FTasksSlice.cpp` 的 `GetManagedLookupCacheKey()` 思路）。

## 9. FAQ

### 9.1 为什么必须 pin / fixed 才能把托管数组指针给 C++？

因为 `int[]` 在托管堆上，GC 可能移动它；只要你把指针交给 C++ 并跨线程异步使用，就必须保证这段内存地址稳定。

### 9.2 为什么禁止捕获 lambda？

捕获 lambda 会生成 closure 对象，本质是 instance method；为了 thunk 直调简单和快，我们先只支持 static。

### 9.3 以后能支持“非 static + 捕获变量”的 handler 吗？

能，但实现会显著复杂（你需要同时把 target 对象传给 C++ 并在 worker 里正确传入/保活），并且通常会逼近 `delegate invoke` 的通用路径，性能会下降。建议先把 static 版本榨到极致再谈易用性扩展。

