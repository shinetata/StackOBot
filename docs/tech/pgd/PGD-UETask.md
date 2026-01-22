本阶段主要穿刺内容：
1、跑通在UE Tasks worker线程稳定执行C#逻辑的链路；
2、性能对比测试（UE Tasks调用C#逻辑、Task.Run、Parallel.For）

## 在UE Tasks Worker线程执行C#逻辑
首先要在C++侧提供一个接口供C#调用，支持传入一个C# Delegate。为了快速验证能否跑通，在C++的worker线程中直接使用Runtime_Invoke调用这个Delegate。
```cpp
// cpp
static void ExecuteImplementation(MonoObject* InDelegate)
{
    MonoMethod* const FoundMethod = FMonoDomain::Delegate_Get_Method(InDelegate);

    if (FoundMethod == nullptr)
    {
        return;
    }

    const auto Signature = FMonoDomain::Method_Signature(FoundMethod);

    if (Signature == nullptr)
    {
        return;
    }

    TArray<UE::Tasks::FTask> TaskList;
    TaskList.Reserve(SafeTaskCount);

    UE::Tasks::FTask Task;
    Task.Launch(TEXT("UETasksSlice.ExecuteWithHandler"), [FoundMethod]()
    {
        // 获取GT线程和Worker线程ID，与C#中获取的进行对比
        // 若一致，则说明C#代码在Task Worker线程跑起来了
        UE_LOG(LogUnrealCSharp, Log,
           TEXT("[UETasksSliceDelegateInvoke] invoke on worker (GT=%d tid=%d)"),
           IsInGameThread() ? 1 : 0,
           static_cast<int32>(FPlatformTLS::GetCurrentThreadId()));
        MonoObject* Exception = nullptr;
        (void)FMonoDomain::Runtime_Invoke(FoundMethod, nullptr, nullptr, &Exception);

        if (Exception != nullptr)
        {
            FMonoDomain::Unhandled_Exception(Exception);
        }
    });
    TaskList.Add(MoveTemp(Task));

    UE::Tasks::Wait(TaskList);
}

// c#
public static void RunLogOnWorkerByRuntimeInvoke()
{
    FTasksSliceImplementation.FTasksSlice_ExecuteImplementation(
        handler: LogFromWorker);
}

private static void LogFromWorker()
{
    Console.WriteLine(
        $"[UETasksSliceDelegateInvokeDemo] managedTid={Thread.CurrentThread.ManagedThreadId} " +
        $"TaskThreadId={TaskGraphBatch.GetCurrentNativeThreadId()}");
}
```

在C#中调用`RunLogOnWorkerByRuntimeInvoke`这个方法，UE闪退，这是预期内的结果。Mono是在主线线程初始化的，Worker线程中没有Mono的环境，无法运行C#代码。
查阅业界主要C++和C#跨语言框架，针对于C++ worker线程调用C#逻辑的解决方案主要分为以下几种：
|框架| 调用方案|
| --- | --- |
| P/Invoke | C# 将一个委托（Delegate）传递给 C++，C++ 在子线程中通过函数指针调用|
|Mono Embedding |将托管线程附加到Mono运行时，使其能够执行托管代码 |
|UnmanagedCallersOnly |将 C# 静态方法直接导出为 C 风格函数指针 |

UnrealCSharp在UE Editor场景下依赖Mono JIT执行C#代码，所以参考Mono Embedding，在每个UE Task被分配到worker线程执行C#代码前，需要调用mono_thread_attach将当前线程注册为Mono Domain的托管线程，从而执行托管代码：
```cpp
// UnrealCSharpCore/Private/Domain/FMonoDomain.cpp
void FMonoDomain::EnsureThreadAttached()
{
    if (Domain == nullptr)
    {
       return;
    }

    // TaskGraph worker 线程可能会复用；重复attach可能触发线程状态断言。
    // 这里只在当前线程尚未附着到 Mono 时才 attach。
    if (mono_thread_current() != nullptr)
    {
       return;
    }

    (void)mono_thread_attach(Domain);
}

Task.Launch(..., [...]()
{
    FMonoDomain::EnsureThreadAttached();
    // 执行C#方法
});
```

再次执行`RunLogOnWorkerByRuntimeInvoke`，成功在UE Editor中打出日志：
```cpp
LogUnrealCSharp: [UETasksSliceDelegateInvoke] invoke on worker (GT=1 tid=41700)
LogUnrealCSharp: [UETasksDelegateInvokeDemo] managedTid=1 TaskThreadId=41700
```
Worker线程的Thread ID一致，说明C#代码已经在UE的Worker线程跑起来了。但随之遇到了另一个必现问题：首次点击Play，停止运行，再次点击Play的时候，整个UE Editor会卡死。

通过一层层Debug，发现卡死前代码走到了`FMonoDomain::LoadAssembly(const TArray<FString>& InAssemblies)`中，该方法会加载插件层程序集Game.dll，在调用到`Runtime_Invoke(AlcLoadFromStreamMethod, AssemblyLoadContextObject, Params)`时，也就是调用C#的`LoadFromStream`方法时未能正常返回，直接结束。

通过查阅.Net官方文档得知，通过`LoadFromStream`加载程序集时，要确保所有登记为托管线程的OS线程都进入可安全处理的状态，否则会持续等待。（推测大概率是死锁了）

同时了解了到UE Editor进程中Worker线程是常驻的，随Editor进程的销毁而销毁。UE Tasks只是负责任务调度，不负责管理线程生命周期。而在任务开始时执行了一次`mono_thread_attach`后对该线程的Mono环境就没再做任何处理，导致即使停止运行游戏后Worker线程还持有Mono环境，甚至在执行某些托管代码（GC），导致第二次Play的时候卡死。

需要解决这个问题以支持重复调试，这里花了不少时间也绕了不少弯路，比如在插件侧监听Play Stop事件等等，但是没有办法从根本上去拿到Worker线程生命周期的钩子，因此都存在多多少少的问题，这里不做赘述。为了尽快开始性能测试，最后的解决方案是，在任务结束后手动调用一次`mono_thread_detach`，将该线程移出Mono托管线程，这样保证每次都是安全的执行环境。（在开启C# Debug模式后还是会有问题，但是不影响性能测试）

之后反复测试Play->Stop，可以稳定运行，进入性能对比测试阶段。

## 性能对比测试
测试的总体思路是：模拟PGD中对于大段连续内存的遍历和处理，对比通过UE Tasks调度C#代码和C#原生的并行化能力Task/Parallel的性能。UE Tasks的测试设计：

1、C#侧定义和实例化数组、任务数量和任务逻辑，传递至C++层；

2、C++层按任务数量对数组进行切分，每段去执行定义好的任务逻辑（遍历并对元素+1）；

为了尽可能减少跨语言调用带来的开销，需要做两件事情：

1、用thunk直接调用函数指针代替Runtime_Invoke；

2、每个数组切片的遍历放在C#层，C++层针对每个任务只需要调用一次方法。而不是针对每个数组中的元素调用一次任务逻辑；

先设计C++侧的接口，支持传入数组指针、长度、任务数量和C#侧的Delegate:
```cpp
static void ExecuteWithHandlerImpl(
    const void* Data, 
    int32 Length, 
    int32 TaskCount, 
    MonoObject* InDelegate)
{
    // 1) delegate -> MonoMethod*
    MonoMethod* Method = FMonoDomain::Delegate_Get_Method(InDelegate);
    if (!Method) return;

    // 2) 校验是否为静态方法以及参数数量
    MonoMethodSignature* Sig = FMonoDomain::Method_Signature(Method);
    if (!Sig) return;
    if (FMonoDomain::Signature_Is_Instance(Sig)) return; 
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
C#侧定义一个unsafe的静态方法，用于处理切片，作为C++侧的Delegate：
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
```
最后再定义一个用于C#侧执行的方法，传入一个托管数组和任务数量，内部调用C++层暴露出来的方法：
```csharp
public static unsafe void RunManagedPinned(int[] arr, int taskCount)
{
    // 用 fixed把 int[] 内存地址固定住再把指针传给 C++，防止GC移动
    fixed (int* p = arr)
    {
        FTasksSliceImplementation.FTasksSlice_ExecuteWithHandlerImplementation(
            (nint)p, arr.Length, taskCount, AddOneKernel);
    }
}
```

考虑到托管数组存在GC的开销，不排除其会在跨语言调用时被放大，这里简单设计了一套原生数组，让C#直接持有一块连续的native内存，将指针和长度传给C++处理，并且同样提供一个执行方法：

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

// 直接处理Native数组，无需fixed
public static void RunNative(NativeBuffer<int> buf, int taskCount)
{
    FTasksSliceImplementation.FTasksSlice_ExecuteBatchWithHandlerImplementation(
        buf.Ptr, buf.Length, taskCount, wait: true, AddOneKernel);
}
```


C#原生的Task/Parallel同理，切片处理和任务分配都在C#层，用Task.Run和Parallel.For去执行任务，这里不再罗列。测试用例中提供不同长度的数组，分别使用不同的任务数量，迭代500轮。执行5次取执行时长的平均值，结果如下：

> `ratioPf/ratioTr` 的含义：`PF/UE`、`TR/UE`（>1 表示 PF/TR 比 UE 慢）。

| Case | length | taskCount | 数组类型 | UE Avg (ms) | PF Avg (ms) | TR Avg (ms) | ratioPf | ratioTr |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | 10,000 | 2 | Managed Pinned | 9.260 | 11.387 | 9.292 | 1.230 | 1.003 |
| 2 | 10,000 | 3 | Managed Pinned | 8.734 | 16.012 | 8.635 | 1.833 | 0.989 |
| 3 | 10,000 | 4 | Managed Pinned | 9.181 | 16.994 | 6.234 | 1.851 | 0.679 |
| 4 | 20,000 | 4 | Managed Pinned | 9.912 | 14.528 | 9.436 | 1.466 | 0.952 |
| 5 | 40,000 | 4 | Managed Pinned | 13.084 | 23.670 | 15.417 | 1.809 | 1.178 |
| 6 | 200,000 | 4 | Managed Pinned | 31.238 | 35.858 | 35.428 | 1.148 | 1.134 |
| 7 | 200,000 | 6 | Managed Pinned | 31.157 | 33.173 | 39.414 | 1.065 | 1.265 |
| 1 | 10,000 | 2 | NativeBuffer | 7.658 | 11.436 | 8.153 | 1.493 | 1.065 |
| 2 | 10,000 | 3 | NativeBuffer | 8.293 | 15.959 | 7.534 | 1.924 | 0.909 |
| 3 | 10,000 | 4 | NativeBuffer | 8.787 | 16.652 | 6.101 | 1.895 | 0.694 |
| 4 | 20,000 | 4 | NativeBuffer | 10.046 | 14.034 | 9.392 | 1.397 | 0.935 |
| 5 | 40,000 | 4 | NativeBuffer | 12.012 | 22.612 | 14.500 | 1.882 | 1.207 |
| 6 | 200,000 | 4 | NativeBuffer | 30.530 | 37.447 | 34.405 | 1.227 | 1.127 |
| 7 | 200,000 | 6 | NativeBuffer | 26.312 | 33.097 | 36.480 | 1.258 | 1.386 |

可以看出在所有 Case 中，Parallel.For都明显更慢（ratioPf > 1）。而在小数据/低并行度时C# Task与接近UE Tasks接近或更快，数据量增大且并行度提高后 UE Tasks更有优势。
同时相比托管数组，Native数组性能更高。
