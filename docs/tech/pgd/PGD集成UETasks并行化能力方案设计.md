# PGD继承UE Tasks并行化能力方案设计

## 将PGD引入到UnrealCSharp中
`UnrealCSharpPlugins/UnrealCSharp/Source/UnrealCSharpCore/Private/Common/FUnrealCSharpFunctionLibrary.cpp`的`GetFullAssemblyPublishPath()`方法中通过`GetFullCustomProjectsPublishPath()`读取配置里的 CustomProjects 列表生成路径：
```cpp
TArray<FString> FUnrealCSharpFunctionLibrary::GetFullAssemblyPublishPath()
{
    return TArrayBuilder<FString>().
           Add(GetFullUEPublishPath()). // UE.dll
           Add(GetFullGamePublishPath()).// Game.dll
           Append(GetFullCustomProjectsPublishPath()). // 自定义程序集
           Build();
}
  
TArray<FString> FUnrealCSharpFunctionLibrary::GetFullCustomProjectsPublishPath()
{
    TArray<FString> FullCustomProjectsPublishPath;

    if (const auto UnrealCSharpSetting = GetMutableDefaultSafe<UUnrealCSharpSetting>())
    {
        // 读取 DefaultUnrealCSharpSetting.ini中配置的CustomProjects
        for (const auto& [Name] : UnrealCSharpSetting->GetCustomProjects())
        {
            FullCustomProjectsPublishPath.Add(GetFullPublishDirectory() / Name + DLL_SUFFIX);
        }
    }

    return FullCustomProjectsPublishPath;
}
```
先将PGD.dll以及其他自定已程序集放入项目根目录的Libraries/目录下统一维护，并在Scripts的Game.csproj（视项目实际命名）中添加reference：
```c#
<ItemGroup>
  <Reference Include="PGD">
      <HintPath>..\..\Libraries\PGD.dll</HintPath>
  </Reference>
</ItemGroup>
```
然后在根目录`Config/DefaultUnrealCSharpSetting.ini`中添加`+CustomProjects=(Name="PGD")`:
```ini
[/Script/UnrealCSharpCore.UnrealCSharpSetting]
bEnableDebug=False
Host=127.0.0.1
Port=50000
+CustomProjects=(Name="PGD")
```
测试PGD是否被引入：
```cs
var world = new IECSWorld();
var entity = world.CreateEntity(10);
Console.WriteLine($"=====ecsworld entity num: {world.EntityNum}, id: {entity.Id}");

// 打出日志
=====ecsworld entity num: 1, id: 10
```

## PGD集成UE Tasks并行化能力

### 2.1 设计目标与背景

#### 2.1.1 PGD原生并行能力的局限性

PGD作为一个纯C#实现的ECS框架，其原生并行能力依赖.NET的`Task`和`Parallel`类进行任务调度。这种方案在独立运行时表现良好，但集成到Unreal Engine后存在一些固有限制：

1. **调度隔离**：.NET ThreadPool与UE的线程系统是两套独立的调度模型，无法协同工作，导致线程资源竞争和调度低效。
2. **上下文切换开销**：托管线程到原生线程的频繁切换增加了不必要的开销。
3. **缺乏与引擎的集成**：PGD的并行任务无法与UE引擎系统的执行模型相配合。

#### 2.1.2 UE Tasks的优势

UE5引入的`UE::Tasks`是一个现代化的C++任务调度系统（注意：**不是**较老的TaskGraph/FTaskGraphInterface），具有以下优势：

1. **现代化设计**：基于C++标准库风格设计的API，类型安全且易于使用。
2. **任务窃取（Work Stealing）**：实现了工作窃取算法，空闲线程可以从其他线程的任务队列中窃取任务，提高负载均衡。
3. **原生性能**：直接在原生线程上执行，避免了托管线程的开销。
4. **与UE5深度集成**：UE5的许多系统正在迁移到UE::Tasks，这是UE5推荐的任务调度方式。

#### 2.1.3 设计目标

本方案的设计目标是：**在UE Tasks中执行PGD的Chunk遍历逻辑**，实现：

1. **统一的调度模型**：PGD的并行任务在UE Tasks上执行，与UE5的现代化任务模型保持一致。
2. **最小的跨语言开销**：通过Thunk和GCHandle等技术，减少C#/C++边界的调用开销。
3. **非阻塞式任务调度**：提供`ScheduleUeParallel`方法，返回`UETasksJobHandle`用于任务生命周期管理。
4. **安全的托管上下文管理**：通过FManagedJobScope确保Worker线程安全地执行托管代码。

### 2.2 整体架构

#### 2.2.1 四层架构设计

PGD集成UE Tasks并行化能力采用四层架构设计：

```
+------------------------------------------------------------------+
|                    用户代码层（User Code Layer）                   |
|                                                                   |
|  var handle = query.ScheduleUeParallel(                           |
|      (ref PGDPosition pos, IEntity entity) =>                     |
|  {                                                                |
|      // 用户自定义的Component处理逻辑                             |
|      pos.x += 1.0f;                                               |
|  });                                                              |
|  handle.Wait();                                                   |
+-------------------------------------+-----------------------------+
                                      | 扩展方法调用
                                      v
+------------------------------------------------------------------+
|              PGD.Parallel 扩展层（Extensions Layer）               |
|                                                                   |
|  UETasksQueryExtensions.ScheduleUeParallel<T>()                   |
|  └── 收集ArchetypeChunk[]        // 收集所有ArchetypeChunk         |
|  └── new QueryRunner1/2         // 创建任务执行器                  |
|  └── GCHandle.Alloc()           // 固定runner在内存中             |
+-------------------------------------+-----------------------------+
                                      | P/Invoke 调用
                                      v
+------------------------------------------------------------------+
|          UnrealCSharp P/Invoke 层（Interoperability Layer）         |
|                                                                   |
|  FTasksQueryImplementation.FTasksQuery_ScheduleBatchImplementation |
|  └── [MethodImpl(MethodImplOptions.InternalCall)]                |
|                                                                   |
|  UETasksQueryRunner.ExecuteTask()                                  |
|  └── GCHandle.FromIntPtr()      // 还原runner对象                 |
+-------------------------------------+-----------------------------+
                                      | InternalCall 注册
                                      v
+------------------------------------------------------------------+
|              FTasksQuery.cpp C++ 层（Native Layer）                 |
|                                                                   |
|  ScheduleBatchImplementation()                                    |
|  └── ValidateManagedContext()    // 验证托管上下文                |
|  └── GetManagedThunkCached()     // 获取Thunk缓存                 |
|  └── UE::Tasks::FTask::Launch()  // 启动UE任务                    |
|      └── FManagedJobScope        // 管理托管上下文                |
|          └── EnsureThreadAttached()    // Attach到Mono            |
|          └── ExecuteOneTask()    // 执行单个任务                  |
|          └── EnsureThreadDetached()    // Detach从Mono            |
|  └── 返回HandleId              // 存储到HandleTasks Map           |
+-------------------------------------+-----------------------------+
                                      | 任务调度
                                      v
+------------------------------------------------------------------+
|              UE::Tasks 任务执行层                                   |
|                                                                   |
|  Worker Thread 1          Worker Thread 2        Worker Thread N  |
|  └── ExecuteOneTask()     └── ExecuteOneTask()   └── ...        |
+------------------------------------------------------------------+
```

#### 2.2.2 数据流转

```
+--------------+     +----------------+     +----------------+     +---------------+
| IQuery<T>    | --> | ArchetypeChunk | --> | QueryRunner    | --> | GCHandle      |
| (用户Query)  |     | [] 数组         |     | (持有chunks)   |     | (固定在内存)   |
+--------------+     +----------------+     +----------------+     +---------------+
                                                                         |
                                                                         v
+--------------+     +----------------+     +----------------+     +---------------+
| 用户Lambda   | <-- | ExecuteTask()  | <-- | Thunk调用      | <-- | nint Handle   |
| (处理逻辑)   |     | (按chunk索引)  |     | (C++ -> C#)    |     | (传给C++)     |
+--------------+     +----------------+     +----------------+     +---------------+
```

#### 2.2.3 跨语言调用链路

```
C# 用户代码
    |
    | ScheduleUeParallel(query, lambda)
    v
C# UETasksQueryExtensions
    |
    | 1. 收集ArchetypeChunk数组
    | 2. new QueryRunner1(world, chunks, count, lambda)
    | 3. GCHandle.Alloc(runner) -> handle
    v
C# P/Invoke (FTasksQueryImplementation)
    |
    | FTasksQuery_ScheduleBatchImplementation(handle, count)
    v
C++ FTasksQuery.cpp
    |
    | 1. ValidateManagedContext()
    | 2. GetManagedThunkCached() -> thunk
    | 3. UE::Tasks::FTask::Launch(...)
    | 4. 返回HandleId
    v
C# 返回 UETasksJobHandle
    |
    | 持有HandleId和GCHandle
    v
用户代码继续执行
    |
    | handle.Wait()
    v
UE::Tasks 任务调度
    |
    | 分发任务到Worker线程
    v
Worker Thread (原生线程)
    |
    | FManagedJobScope 构造
    | 1. TryEnterManagedJobExecution()
    | 2. EnsureThreadAttached() -> mono_thread_attach()
    v
C++ ExecuteOneTask
    |
    | TypedThunk(stateHandle, taskIndex, &exception)
    v
C# UETasksQueryRunner.ExecuteTask
    |
    | 1. GCHandle.FromIntPtr(handle) -> runner
    | 2. runner.ExecuteTask(taskIndex)
    v
C# QueryRunner.ExecuteTask
    |
    | 1. chunks[taskIndex] -> chunk
    | 2. RunChunk(chunk, 0, chunk.Length)
    | 3. 遍历chunk.Components，调用用户lambda
    v
C# 用户Lambda (实际执行)
    |
    | 处理每个Entity的Component
    v
C++ FManagedJobScope 析构
    |
    | 1. EnsureThreadDetached() -> mono_thread_detach()
    | 2. LeaveManagedJobExecution()
```

### 2.3 核心组件详解

#### 2.3.1 UETasksQueryExtensions（扩展方法入口）

`UETasksQueryExtensions`是整个系统的入口，提供了扩展方法用于在UE Tasks中执行PGD查询。位于`Script/Game/PGD/Parallel/UETasksQueryExtensions.cs`。

**核心方法**：

1. **ScheduleUeParallel** - 非阻塞式任务调度（主要方法）
2. **ExecuteUeParallel** - 阻塞式执行（中间状态，内部调用ScheduleUeParallel后立即Wait）

**ScheduleUeParallel代码示例**（简化版）：

```csharp
public static UETasksJobHandle ScheduleUeParallel<T1>(this IQuery<T1> query, ForEachEntity<T1> lambda)
    where T1 : struct
{
    // 1. 参数校验
    if (lambda == null) throw new ArgumentNullException(nameof(lambda));

    // 2. 收集所有Chunk
    var chunkCount = query.ChunkCount;
    if (chunkCount <= 0) return UETasksJobHandle.Completed;

    var chunks = new ArchetypeChunk<T1>[chunkCount];
    var index = 0;
    foreach (var chunk in query.ArchetypeChunk)
    {
        chunks[index++] = chunk;
    }

    // 3. 创建任务执行器（持有chunks数组和用户lambda）
    var runner = new QueryRunner1<T1>(query.World, chunks, chunkCount, lambda);

    // 4. 单Chunk优化路径：直接在当前线程执行
    if (chunkCount == 1)
    {
        runner.ExecuteTask(0);
        return UETasksJobHandle.Completed;
    }

    // 5. 固定runner在内存中，防止GC移动
    var handle = GCHandle.Alloc(runner);
    var handleId = FTasksQueryImplementation.FTasksQuery_ScheduleBatchImplementation(
        (nint)GCHandle.ToIntPtr(handle),
        chunkCount);

    if (handleId == 0)
    {
        handle.Free();
        return UETasksJobHandle.Completed;
    }

    // 6. 返回JobHandle，由用户管理生命周期
    return new UETasksJobHandle(handleId, handle);
}
```

#### 2.3.2 QueryRunner（任务执行器）

`QueryRunner`是实际执行用户逻辑的类，实现了`IUETasksQueryRunner`接口。它持有一个Chunk数组和用户Lambda，在Worker线程上被调用时执行特定索引的Chunk。

**接口定义**：

```csharp
public interface IUETasksQueryRunner
{
    /// <summary>
    /// 执行任务。
    /// </summary>
    /// <param name="taskIndex">任务索引，实际对应chunkIndex（用于从chunks数组中获取对应的ArchetypeChunk）。</param>
    void ExecuteTask(int taskIndex);
}
```

**实现示例**（QueryRunner1）：

```csharp
private sealed class QueryRunner1<T1> : IUETasksQueryRunner
    where T1 : struct
{
    private readonly IECSWorld world;
    private readonly ArchetypeChunk<T1>[] chunks;
    private readonly int chunkCount;
    private readonly ForEachEntity<T1> action;

    public QueryRunner1(IECSWorld world,
        ArchetypeChunk<T1>[] chunks,
        int chunkCount,
        ForEachEntity<T1> action)
    {
        this.world = world;
        this.chunks = chunks;
        this.chunkCount = chunkCount;
        this.action = action;
    }

    public void ExecuteTask(int taskIndex)
    {
        // 边界检查
        if (taskIndex < 0 || taskIndex >= chunkCount) return;

        // 获取对应索引的Chunk
        var chunk = chunks[taskIndex];

        // 执行Chunk遍历
        RunChunk(chunk, 0, chunk.Length);
    }

    private void RunChunk(in ArchetypeChunk<T1> chunk, int start, int count)
    {
        var comps = chunk.Components1;  // 获取Component数组（直接内存访问）
        var ids = chunk.Ids;            // 获取Entity ID数组
        var end = start + count;

        // 遍历Chunk中的所有Entity
        for (int n = start; n < end; n++)
        {
            // 调用用户Lambda
            action(ref comps[n], world.GetEntityById(ids[n]));
        }
    }
}
```

#### 2.3.3 UETasksQueryRunner（P/Invoke桥接）

`UETasksQueryRunner`是C++到C#的桥接层，负责将C++传来的`IntPtr`还原为C#对象并调用。

```csharp
public static class UETasksQueryRunner
{
    public static void ExecuteTask(nint stateHandle, int index)
    {
        // 1. 从IntPtr还原GCHandle
        var handle = GCHandle.FromIntPtr((IntPtr)stateHandle);

        // 2. 获取目标对象并类型转换
        if (handle.Target is not IUETasksQueryRunner runner)
        {
            throw new InvalidOperationException("UETasksQueryRunner state handle is invalid.");
        }

        // 3. 调用ExecuteTask方法
        runner.ExecuteTask(index);
    }
}
```

#### 2.3.4 FTasksQueryImplementation（P/Invoke声明）

`FTasksQueryImplementation`声明了所有与C++层交互的extern方法。

```csharp
public static unsafe class FTasksQueryImplementation
{
    [MethodImpl(MethodImplOptions.InternalCall)]
    public static extern void FTasksQuery_ExecuteBatchImplementation(
        nint stateHandle, int taskCount, bool wait);

    [MethodImpl(MethodImplOptions.InternalCall)]
    public static extern long FTasksQuery_ScheduleBatchImplementation(
        nint stateHandle, int taskCount);

    [MethodImpl(MethodImplOptions.InternalCall)]
    public static extern bool FTasksQuery_IsHandleCompletedImplementation(long handleId);

    [MethodImpl(MethodImplOptions.InternalCall)]
    public static extern void FTasksQuery_WaitHandleImplementation(long handleId);

    [MethodImpl(MethodImplOptions.InternalCall)]
    public static extern void FTasksQuery_ReleaseHandleImplementation(long handleId);

    [MethodImpl(MethodImplOptions.InternalCall)]
    public static extern int FTasksQuery_GetNumWorkerThreadsImplementation();

    [MethodImpl(MethodImplOptions.InternalCall)]
    public static extern int FTasksQuery_GetCurrentNativeThreadIdImplementation();
}
```

#### 2.3.5 FTasksQuery.cpp（C++实现）

`FTasksQuery.cpp`是C++层的核心实现，负责创建UE Tasks并管理托管上下文。

**FManagedJobScope** - 托管上下文管理器：

```cpp
struct FManagedJobScope
{
    explicit FManagedJobScope()
        : bEntered(FMonoDomain::TryEnterManagedJobExecution())
        , bDetachOnExit(FMonoDomain::ShouldDetachAfterManagedJob() && !IsInGameThread())
    {
        if (bEntered)
        {
            // 将当前线程附加到Mono运行时
            FMonoDomain::EnsureThreadAttached();
        }
    }

    ~FManagedJobScope()
    {
        if (!bEntered)
        {
            return;
        }

        if (bDetachOnExit)
        {
            // 从Mono运行时分离当前线程（关键：避免Editor卡死）
            FMonoDomain::EnsureThreadDetached();
        }

        FMonoDomain::LeaveManagedJobExecution();
    }

    bool IsEntered() const
    {
        return bEntered;
    }

private:
    bool bEntered = false;
    bool bDetachOnExit = false;
};
```

**ExecuteBatchImplementation** - 阻塞式执行：

```cpp
static void ExecuteBatchImplementation(const void* InStateHandle,
                                       const int32 InTaskCount,
                                       const bool bWait)
{
    if (InTaskCount <= 0) return;
    if (!ValidateManagedContext()) return;

    // 获取Thunk缓存（避免每次都查找方法）
    static FManagedThunkCache ExecuteCache;
    void* FoundThunk = nullptr;
    if (!GetExecuteThunk(ExecuteCache, FoundThunk)) return;

    void* const StateHandle = const_cast<void*>(InStateHandle);

    // Debug单线程模式：直接顺序执行
    if (IsDebugSingleThread())
    {
        for (int32 TaskIndex = 0; TaskIndex < InTaskCount; ++TaskIndex)
        {
            ExecuteOneTask(StateHandle, FoundThunk, TaskIndex);
        }
        return;
    }

    // 创建UE Tasks
    TArray<UE::Tasks::FTask> TaskList;
    TaskList.Reserve(InTaskCount);

    for (int32 TaskIndex = 0; TaskIndex < InTaskCount; ++TaskIndex)
    {
        UE::Tasks::FTask Task;
        Task.Launch(TEXT("UETasksQuery.ExecuteBatch"), [StateHandle, FoundThunk, TaskIndex]()
        {
            // 管理托管上下文（关键！）
            FManagedJobScope ManagedScope;

            if (!ManagedScope.IsEntered())
            {
                return;
            }

            // 执行单个任务
            ExecuteOneTask(StateHandle, FoundThunk, TaskIndex);
        });

        TaskList.Add(MoveTemp(Task));
    }

    // 阻塞等待所有任务完成
    if (bWait)
    {
        UE::Tasks::Wait(TaskList);
    }
}
```

**ScheduleBatchImplementation** - 非阻塞式调度：

```cpp
static int64 ScheduleBatchImplementation(const void* InStateHandle, const int32 InTaskCount)
{
    // ... 前置校验同 ExecuteBatchImplementation ...

    // 创建UE Tasks（不阻塞）
    TArray<UE::Tasks::FTask> TaskList;
    TaskList.Reserve(InTaskCount);

    for (int32 TaskIndex = 0; TaskIndex < InTaskCount; ++TaskIndex)
    {
        UE::Tasks::FTask Task;
        Task.Launch(TEXT("UETasksQuery.ScheduleBatch"), [StateHandle, FoundThunk, TaskIndex]()
        {
            FManagedJobScope ManagedScope;
            if (!ManagedScope.IsEntered()) return;
            ExecuteOneTask(StateHandle, FoundThunk, TaskIndex);
        });

        TaskList.Add(MoveTemp(Task));
    }

    // 生成唯一HandleId并存储到全局Map
    const int64 HandleId = NextHandleId.fetch_add(1);
    {
        FScopeLock ScopeLock(&HandleMutex);
        HandleTasks.Add(HandleId, MoveTemp(TaskList));
    }

    return HandleId;
}
```

#### 2.3.6 UETasksJobHandle（任务句柄）

`UETasksJobHandle`是非阻塞式调度返回的句柄，用于等待和释放任务。

```csharp
public sealed class UETasksJobHandle : IDisposable
{
    // 已完成的句柄（单例，避免分配）
    public static readonly UETasksJobHandle Completed = new(0, default, true);

    private long handleId;      // C++层的任务ID
    private GCHandle handle;    // C#层的runner引用
    private bool released;      // 是否已释放

    public long HandleId => handleId;

    public bool IsCompleted
    {
        get
        {
            if (handleId == 0) return true;
            return FTasksQueryImplementation.FTasksQuery_IsHandleCompletedImplementation(handleId);
        }
    }

    public void Wait()
    {
        if (handleId == 0) return;
        FTasksQueryImplementation.FTasksQuery_WaitHandleImplementation(handleId);
    }

    public void Dispose()
    {
        if (released) return;

        released = true;

        // 释放C++层的任务
        if (handleId != 0)
        {
            FTasksQueryImplementation.FTasksQuery_ReleaseHandleImplementation(handleId);
            handleId = 0;
        }

        // 释放C#层的GCHandle
        if (handle.IsAllocated)
        {
            handle.Free();
        }
    }
}
```

### 2.4 执行流程详解

#### 2.4.1 ScheduleUeParallel 非阻塞式调度（主要执行流程）

**流程图**：

```
用户代码
    |
    v
+---------------------------+
| var handle =              |
|   query.ScheduleUeParallel|
|   ((ref pos, entity)=>{}) |
+-----------+---------------+
            |
            v
+---------------------------+
| 1. 收集Chunk数组          |  遍历query.ArchetypeChunk
|    chunks[] = query...    |  直接分配数组
+-----------+---------------+
            |
            v
+---------------------------+
| 2. new QueryRunner1       |  创建任务执行器
|    (world, chunks,        |  持有chunks和lambda
|     count, lambda)        |
+-----------+---------------+
            |
            v
            count == 1 ?
           /           \
         Yes            No
         |               |
         v               v
+----------------+  +-------------------------+
| runner.Execute |  | 3. GCHandle.Alloc       |
| Task(0)        |  |    handle = Alloc(...)  |
| 直接执行       |  +-----------+-------------+
| 返回Completed  |              |
+----------------+              v
                        +-------------------------+
                        | 4. ScheduleBatchImpl    |
                        |    (handle, count)      |
                        +-----------+-------------+
                                    |
                                    v
                        +-------------------------+
                        | C++: 创建 UE::FTask[]   |
                        |    for each chunk       |
                        +-----------+-------------+
                                    |
                                    v
                        +-------------------------+
                        | 生成HandleId，存储到   |
                        | HandleTasks Map         |
                        +-----------+-------------+
                                    |
                                    v
                        +-------------------------+
                        | 返回 UETasksJobHandle   |
                        | (handleId, gchandle)    |
                        +-----------+-------------+
                                    |
                                    v
                        用户代码继续执行
                        (可调度更多任务/执行其他逻辑)
                                    |
                                    v
                        +-------------------------+
                        | handle.Wait()           |
                        |    或 handle.Dispose()   |
                        +-----------+-------------+
                                    |
                                    v
                    +---------------+---------------+
                    |               |               |
                Thread 1        Thread 2        Thread N
                    |               |               |
                    v               v               v
            +-----------+   +-----------+   +-----------+
            | Attach    |   | Attach    |   | Attach    |
            | Mono      |   | Mono      |   | Mono      |
            +-----+-----+   +-----+-----+   +-----+-----+
                  |               |               |
                  v               v               v
            +-----------+   +-----------+   +-----------+
            | Thunk调用  |   | Thunk调用  |   | Thunk调用  |
            | ExecuteTask|   | ExecuteTask|   | ExecuteTask|
            +-----+-----+   +-----+-----+   +-----+-----+
                  |               |               |
                  v               v               v
            +-----------+   +-----------+   +-----------+
            | Runner.E  |   | Runner.E  |   | Runner.E  |
            | xecuteTask|   | xecuteTask|   | xecuteTask|
            +-----+-----+   +-----+-----+   +-----+-----+
                  |               |               |
                  v               v               v
            +-----------+   +-----------+   +-----------+
            | 用户Lambda |   | 用户Lambda |   | 用户Lambda|
            | 处理逻辑   |   | 处理逻辑   |   | 处理逻辑  |
            +-----+-----+   +-----+-----+   +-----+-----+
                  |               |               |
                  v               v               v
            +-----------+   +-----------+   +-----------+
            | Detach    |   | Detach    |   | Detach    |
            | Mono      |   | Mono      |   | Mono      |
            +-----+-----+   +-----+-----+   +-----+-----+
                  |               |               |
                  +---------------+---------------+
                                  |
                                  v
                        +-------------------------+
                        | 所有任务完成           |
                        +-------------------------+
```

**详细文字描述**：

1. **Chunk收集**：遍历`IQuery`的所有`ArchetypeChunk`，直接分配一个精确大小的数组。

2. **Runner创建**：创建`QueryRunner`实例，持有chunks数组、chunk数量和用户Lambda。

3. **单Chunk优化**：如果只有一个Chunk，直接在当前线程执行，返回`Completed`句柄。

4. **GCHandle分配**：使用`GCHandle.Alloc`将runner对象固定在内存中。

5. **C++调用**：调用`FTasksQuery_ScheduleBatchImplementation`，传入GCHandle的IntPtr和chunk数量。

6. **任务创建与存储**：C++层为每个Chunk创建`UE::Tasks::FTask`，生成唯一HandleId，将TaskList存储到`HandleTasks` Map。

7. **返回Handle**：返回`UETasksJobHandle`，持有HandleId和GCHandle。

8. **用户继续执行**：用户可以继续调度更多任务或执行其他逻辑。

9. **Wait/Dispose**：调用`handle.Wait()`或`handle.Dispose()`时，C++层会等待所有任务完成，然后释放资源。

#### 2.4.2 ExecuteUeParallel 阻塞式执行（中间状态）

`ExecuteUeParallel`是`ScheduleUeParallel`的简化版本，内部同样调用C++层的任务创建逻辑，但在任务创建后立即调用`UE::Tasks::Wait()`阻塞等待完成。这是一个中间状态，主要用于简化调用场景，实际使用中应优先使用`ScheduleUeParallel`以获得更灵活的任务管理能力。

#### 2.4.3 Handle 生命周期管理

**流程图**：

```
创建
    |
    v
+---------------------------+
| query.ScheduleUeParallel  |
|   return new UETasksJob   |
|   Handle(handleId, handle)|
+-----------+---------------+
            |
            v
+---------------------------+
| 状态: Active              |
| - handleId = 非0值        |
| - GCHandle 已分配         |
| - 存在于 HandleTasks Map  |
+-----------+---------------+
            |
            v
      +-----+-----+
      |           |
      v           v
  IsCompleted    Wait()
      |              |
      v              v
+---------------+   +-------------------+
| 查询 Map 中   |   | 从 Map 复制 TaskList |
| 所有 Task     |   | UE::Tasks::Wait()   |
| 是否完成      |   | 不移除 Map 条目     |
+---------------+   +----------+--------+
                            |
                            v
                    所有任务完成
                            |
                            v
                    +-------------------+
                    | 返回（用户继续）  |
                    +-------------------+
                            |
                            v
                        Dispose()
                            |
                            v
                    +-------------------+
                    | 从 Map 移除并等待 |
                    | ReleaseHandleImpl |
                    +----------+--------+
                               |
                               v
                    +-------------------+
                    | 释放 GCHandle     |
                    | handleId = 0      |
                    +-------------------+
```

**Wait vs Dispose 的区别**：

- `Wait()`：等待任务完成，但保持Handle有效，可以再次调用`Wait()`或检查`IsCompleted`。
- `Dispose()`：从HandleTasks Map中移除任务，等待完成，然后释放GCHandle。Dispose后Handle不可再用。

### 2.5 托管上下文管理（关键问题）

#### 2.5.1 问题描述

UnrealCSharp在UE Editor场景下依赖Mono JIT执行C#代码。Mono是单线程初始化的，默认只在主线程（Game Thread）上创建了运行时环境。当UE Tasks将任务调度到Worker线程执行C#代码时，会遇到以下问题：

1. **无托管环境**：Worker线程没有Mono环境，无法执行托管代码。
2. **线程复用**：UE的Worker线程是常驻复用的，重复attach可能触发断言。
3. **重复Play卡死**：不detach会导致第二次Play时Editor卡死。

#### 2.5.2 解决方案：FManagedJobScope

```cpp
struct FManagedJobScope
{
    explicit FManagedJobScope()
        : bEntered(FMonoDomain::TryEnterManagedJobExecution())
        , bDetachOnExit(FMonoDomain::ShouldDetachAfterManagedJob() && !IsInGameThread())
    {
        if (bEntered)
        {
            FMonoDomain::EnsureThreadAttached();  // 关键！
        }
    }

    ~FManagedJobScope()
    {
        if (!bEntered) return;

        if (bDetachOnExit)
        {
            FMonoDomain::EnsureThreadDetached();  // 关键！
        }

        FMonoDomain::LeaveManagedJobExecution();
    }

    bool IsEntered() const { return bEntered; }

private:
    bool bEntered = false;
    bool bDetachOnExit = false;
};
```

#### 2.5.3 Attach/Detach 机制

**Attach**（任务开始时）：

```cpp
void FMonoDomain::EnsureThreadAttached()
{
    if (Domain == nullptr) return;

    // UE Tasks worker 线程可能会复用；重复attach可能触发线程状态断言。
    // 这里只在当前线程尚未附着到 Mono 时才 attach。
    if (mono_thread_current() != nullptr)
    {
        return;  // 已经attach，跳过
    }

    (void)mono_thread_attach(Domain);
}
```

**Detach**（任务结束时）：

```cpp
void FMonoDomain::EnsureThreadDetached()
{
    if (Domain == nullptr) return;

    if (mono_thread_current() == nullptr)
    {
        return;  // 已经detach，跳过
    }

    mono_thread_detach(Domain);
}
```

#### 2.5.4 为什么需要 Detach

UE Editor进程中的Worker线程是常驻的，随Editor进程的销毁而销毁。如果只在任务开始时attach而不在结束时detach，会导致：

1. **Worker线程持有Mono环境**：即使停止运行游戏后，Worker线程仍然注册为Mono托管线程。
2. **可能正在执行托管代码**：Worker线程可能在后台执行GC或其他托管操作。
3. **第二次Play时卡死**：当重新Play时，Mono需要重新加载程序集，但此时Worker线程还持有旧的Mono环境，导致死锁。

通过在任务结束时detach，确保每次Play都是干净的执行环境。

**注意**：在开启C# Debug模式后，detach机制可能仍存在问题，但不影响性能测试。

### 2.6 性能优化点

#### 2.6.1 数组直接分配优化

在收集ArchetypeChunk时，直接分配精确大小的数组，避免List的动态扩容开销：

```csharp
// 优化：直接分配精确大小的数组
var chunkCount = query.ChunkCount;
var chunks = new ArchetypeChunk<T1>[chunkCount];
var index = 0;
foreach (var chunk in query.ArchetypeChunk)
{
    chunks[index++] = chunk;
}
```

**收益**：避免List动态扩容带来的内存分配和复制开销。

#### 2.6.2 Thunk 代替 Runtime_Invoke

```cpp
// 方案1：Runtime_Invoke（反射调用，开销大）
MonoObject* Exception = nullptr;
FMonoDomain::Runtime_Invoke(FoundMethod, nullptr, nullptr, &Exception);

// 方案2：Thunk（直接函数指针调用，开销小）
using FExecuteTaskThunk = void (*)(void*, int32, MonoObject**);
const auto TypedThunk = reinterpret_cast<FExecuteTaskThunk>(Thunk);
MonoObject* Exception = nullptr;
TypedThunk(StateHandle, TaskIndex, &Exception);
```

**收益**：Thunk是编译期生成的函数指针，直接调用，避免了反射查找的开销。

#### 2.6.3 Thunk 缓存机制

```cpp
struct FManagedThunkCache
{
    FCriticalSection Mutex;
    uint64 CachedKey = 0;
    void* CachedThunk = nullptr;
};

static void* GetManagedThunkCached(FManagedThunkCache& Cache,
                                   const TCHAR* InManagedClassName,
                                   const TCHAR* InMethodName,
                                   const int32 InParamCount)
{
    const uint64 Key = GetManagedLookupCacheKey();

    // 缓存命中
    if (Cache.CachedThunk != nullptr && Cache.CachedKey == Key)
    {
        return Cache.CachedThunk;
    }

    // 加锁查找并缓存
    FScopeLock ScopeLock(&Cache.Mutex);
    // ... 查找方法并获取Thunk ...
    Cache.CachedKey = Key;
    Cache.CachedThunk = FoundThunk;
    return Cache.CachedThunk;
}
```

**收益**：避免每次执行都进行方法查找，只在第一次执行时查找并缓存。

#### 2.6.4 单 Chunk 优化路径

```csharp
if (count == 1)
{
    runner.ExecuteTask(0);  // 直接在当前线程执行
    return;
}
```

**收益**：小Query（常见于调试）直接在当前线程执行，避免跨线程调用的开销。

#### 2.6.5 锁外 Wait

```cpp
static void WaitHandleImplementation(const int64 InHandleId)
{
    if (InHandleId <= 0) return;

    // 在锁外复制TaskList
    TArray<UE::Tasks::FTask> TasksCopy;
    {
        FScopeLock ScopeLock(&HandleMutex);
        const auto TasksPtr = HandleTasks.Find(InHandleId);
        if (TasksPtr == nullptr) return;
        TasksCopy = *TasksPtr;  // 复制，不持有锁
    }

    // 在锁外等待，不阻塞其他线程
    if (TasksCopy.Num() > 0)
    {
        UE::Tasks::Wait(TasksCopy);
    }
}
```

**收益**：等待操作不持有锁，避免阻塞其他线程对HandleTasks Map的访问。

#### 2.6.6 ReleaseHandleImplementation 先移除再等待

```cpp
static void ReleaseHandleImplementation(const int64 InHandleId)
{
    if (InHandleId <= 0) return;

    // 先移除，获取所有权
    TArray<UE::Tasks::FTask> TaskList;
    {
        FScopeLock ScopeLock(&HandleMutex);
        const auto TasksPtr = HandleTasks.Find(InHandleId);
        if (TasksPtr == nullptr) return;
        TaskList = MoveTemp(*TasksPtr);  // 移动语义
        HandleTasks.Remove(InHandleId);
    }

    // 锁外等待
    if (TaskList.Num() > 0)
    {
        UE::Tasks::Wait(TaskList);
    }
}
```

**收益**：立即从Map中移除，释放HandleId供重用；等待操作在锁外执行。

### 2.7 性能测试

#### 测试环境与参数

| 参数 | 值 | 说明 |
|-----|------|------|
| ENTITY_COUNT | 40,000 | 单种组件组合的实体数量 |
| ITERATION | 16 | 性能测试迭代次数 |
| EXECUTE_ROUND | 20 | 执行轮数（用于取平均值） |
| WORK | 128 | 单元素计算负载（模拟计算循环次数） |

#### 实体构建策略

测试使用 7 种组件组合类型，每种组合创建 `ENTITY_COUNT`（40,000）个实体，总计约 280,000 个实体：

```cs
// 1. PGDPosition + PGDRotation
// 2. PGDPosition + PGDRotation + Health
// 3. PGDPosition + PGDRotation + Mana
// 4. PGDPosition + PGDRotation + Mana + Health
// 5. PGDPosition + PGDRotation + Mana + PGDScale
// 6. PGDPosition + PGDRotation + Mana + PGDTransform
// 7. PGDPosition + PGDRotation + Mana + Health + PGDScale
```

这种多样化组合旨在模拟真实场景中 Entity 组件构成的差异性，测试 Query 在多 Archetype 下的遍历与调度性能。

#### 测试场景

##### ScheduleUeParallel + 多Query并行 + 逐个 Dispose

```cs
var handles = new List<UETasksJobHandle>();
var h1 = queryPos.ScheduleUeParallel((ref PGDPosition pos, IEntity entity) =>
{
    float v = pos.x;
    for (int k = 0; k < WORK; k++)
    {
        v = v * 1.001f + 0.1f;
    }
    pos.x = v;
});
var h2 = queryRot.ScheduleUeParallel((ref PGDRotation rot, IEntity entity) => { ... });
var h3 = queryhealth.ScheduleUeParallel((ref Health health, IEntity entity) => { ... });
var h4 = querymana.ScheduleUeParallel((ref Mana mana, IEntity entity) => { ... });

// 逐个 Dispose
for (int j = 0; j < handles.Count; j++)
{
    handles[j].Dispose();
}
```

##### ScheduleParallel对比测试

```cs
var h1 = queryPos.ScheduleParallel((ref PGDPosition pos, IEntity entity) => { ... });
ScheduleParallelHandleStore.Add(h1);
// ... h2, h3, h4
ScheduleParallelHandleStore.CompleteAll();
```

#### 测试结论

在中高负载的场景下，UE Tasks的优势非常明显。而在轻负载时，PGD内部的并行能力因调度路径更轻，整体效率会更高。