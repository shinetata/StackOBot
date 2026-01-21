# UE::Tasks 托管执行测试方案（面向小白）

> 目标：用 **UE::Tasks** 在 UE worker 线程上执行 **C# 逻辑**，并与 `Task.Run`、`Parallel.For` 在**同一任务**下做性能对比。
>
> 适用范围：本仓库 `Plugins/UnrealCSharp/` + `Script/` 的 UE::Tasks 测试链路。

## 这套测试在做什么

一句话：**把“同一份工作”交给三种调度方式来跑，再看耗时**。

- UE::Tasks（UE 原生任务系统）
- Task.Run（.NET 任务）
- Parallel.For（.NET 并行循环）

这三条路径做的“工作内容”完全一致：
- 对同一段数组做 `+1`，并把结果累加成 `sum`。

## 关键链路：UE::Tasks 如何调用 C#

这条链路是整个测试的核心。你可以把它理解为 **“C# 调 C++，C++ 再反调 C#”**。

**主链路（简化后的唯一接口）：**

1) C# 调用 `UETasksBatch.ExecuteBatch(...)`
2) 走 internal call → C++ 的 `FTasks::ExecuteBatch(...)`
3) C++ 使用 UE::Tasks 启动 worker 任务
4) worker 线程里通过 **thunk** 直接调用 C# `UETasksBatch.ExecuteTask(...)`
5) C# 执行具体的工作逻辑（对数组切片做 `+1` 并累加）

## 任务是怎么切分的

这里只有 **一个层级**，逻辑任务和 UE::Tasks 任务 **一一对应（1:1）**：

- **C# 层**决定逻辑任务数量：`taskCount`。
- **C++ 层**直接创建 `taskCount` 个 UE::Tasks 任务。
- 每个 UE 任务只负责一个逻辑任务索引 `index`。
- C# 根据 `index` 计算数组切片并执行工作。

## 流程图（ASCII）

```
C# 测试入口
UETasksManagedVsCSharpPerfRunner.RunManagedAddOneAndSumCompare
                |
                v
C# 封装层
UETasksBatch.ExecuteBatch(action, taskCount)
                |
                v
C# -> C++ internal call
FTasks.ExecuteBatch(stateHandle, taskCount, wait)
                |
                v
C++ 侧切分 UE::Tasks 任务
for (each UE worker task)
    launch UE::Tasks::FTask
                |
                v
UE worker 线程
thunk 直调 C# ExecuteTask(stateHandle, index)
                |
                v
C# 执行业务
action(index)
```

## 示例代码（带注释）

### C#：测试入口（简化版）
文件：`Plugins/UnrealCSharp/Script/UE/Library/UETasksManagedVsCSharpPerfRunner.cs`

```csharp
// 这里仅展示关键逻辑，完整实现以仓库文件为准
private static long RunUeTasksOnce(int[] data, int taskCount)
{
    // 逻辑任务：把数组切成 taskCount 份
    var len = data.Length;
    var chunkSize = (len + taskCount - 1) / taskCount;

    long sum = 0;

    // UE::Tasks 只接收“逻辑任务索引”，具体切片仍在这里完成
    UETasksBatch.ExecuteBatch(index =>
    {
        var start = index * chunkSize;
        var end = Math.Min(start + chunkSize, len);
        long local = 0;

        // 真实工作：数组 +1，并累加
        for (var i = start; i < end; i++)
        {
            data[i] += 1;
            local += data[i];
        }

        Interlocked.Add(ref sum, local);
    }, taskCount);

    return sum;
}
```

### C#：UETasksBatch（唯一测试接口）
文件：`Plugins/UnrealCSharp/Script/UE/Library/UETasksBatch.cs`

```csharp
public static void ExecuteBatch(Action<int> executeIndex, int taskCount)
{
    // 1) 把 C# 状态封装为 GCHandle，交给 C++ 持有
    var state = new BatchState { ExecuteIndex = executeIndex };
    var handle = GCHandle.Alloc(state);

    try
    {
        // 2) 走 internal call -> C++ 的 FTasks.ExecuteBatch
        FTasksImplementation.FTasks_ExecuteBatchImplementation(
            (nint)GCHandle.ToIntPtr(handle),
            taskCount,
            wait: true);
    }
    finally
    {
        // 3) 任务结束释放 GCHandle
        handle.Free();
    }
}

// C++ 会通过 thunk 直接调用这个方法
public static void ExecuteTask(nint stateHandle, int index)
{
    var handle = GCHandle.FromIntPtr((IntPtr)stateHandle);
    if (handle.Target is not BatchState state)
    {
        throw new InvalidOperationException("UETasksBatch state handle is invalid.");
    }

    state.ExecuteIndex(index);
}
```

### C++：UE::Tasks 侧核心逻辑（简化版）
文件：`Plugins/UnrealCSharp/Source/UnrealCSharp/Private/Domain/InternalCall/FTasks.cpp`

```cpp
// 这里只展示核心流程，完整实现以仓库文件为准
static void ExecuteBatchImplementation(const void* StateHandle, int32 TaskCount, bool bWait)
{
    // 1) 找到 C# 的 ExecuteTask，并拿到 unmanaged thunk
    void* Thunk = GetManagedThunkCached(Cache, TEXT("UETasksBatch"), TEXT("ExecuteTask"), 2);
    if (Thunk == nullptr)
    {
        return;
    }

    // 2) 生成 UE::Tasks 任务并在 worker 上执行（1:1）
    TArray<UE::Tasks::FTask> Tasks;
    Tasks.Reserve(TaskCount);

    for (int32 TaskIndex = 0; TaskIndex < TaskCount; ++TaskIndex)
    {
        UE::Tasks::FTask Task;
        Task.Launch(TEXT("UETasks.ExecuteBatch"), [StateHandle, TaskIndex, Thunk]()
        {
            // 3) worker 线程：通过 thunk 直调 C# ExecuteTask
            using FExecuteTaskThunk = void (*)(void*, int32, MonoObject**);
            auto Execute = reinterpret_cast<FExecuteTaskThunk>(Thunk);

            MonoObject* Exception = nullptr;
            Execute(const_cast<void*>(StateHandle), TaskIndex, &Exception);

            if (Exception != nullptr)
            {
                FMonoDomain::Unhandled_Exception(Exception);
            }
        });

        Tasks.Add(MoveTemp(Task));
    }

    if (bWait)
    {
        UE::Tasks::Wait(Tasks);
    }
}
```

## 你应该怎么跑这套测试

入口函数：
`UETasksManagedVsCSharpPerfRunner.RunManagedAddOneAndSumCompare(...)`（托管数组版）
`UETasksManagedVsCSharpPerfRunner.RunNativeBufferAddOneAndSumCompare(...)`（NativeBuffer 版）

关键参数：
- `length`：数组大小（工作总量）
- `taskCount`：逻辑任务数量
- `iterations`：正式测量次数
- `warmup`：预热次数
- `rounds`：重复轮次（取平均值）

**建议起步参数：**
- `length = 500_000`
- `taskCount = 16`

## 常见问题（FAQ）

**Q1：为什么不用 Runtime_Invoke？**
A：当前实现使用 `unmanaged thunk` 直调以减少开销。代价是异常处理不如 `Runtime_Invoke` 直观，需要更谨慎处理托管异常。

## 相关文件速查

- C++ internal call：`Plugins/UnrealCSharp/Source/UnrealCSharp/Private/Domain/InternalCall/FTasks.cpp`
- C# 内部调用声明：`Plugins/UnrealCSharp/Script/UE/Library/FTasksImplementation.cs`
- C# 封装层：`Plugins/UnrealCSharp/Script/UE/Library/UETasksBatch.cs`
- 性能对比入口：`Plugins/UnrealCSharp/Script/UE/Library/UETasksManagedVsCSharpPerfRunner.cs`
