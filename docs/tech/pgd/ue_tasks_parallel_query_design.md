# PGD 集成 UE Task 任务系统详细设计文档

> 文档版本：v1.0
> 最后更新：2026-01-30
> 核心文件：FTasksQuery.cpp

---

## 1. 概述与设计目标

### 1.1 系统定位

本系统旨在为 PGD（C# ECS 框架）的 `IQuery<T...>` 提供基于 **UE::Tasks** 的并行遍历能力，使 C# 脚本能够利用 UE5 的任务系统实现高性能并行处理。

### 1.2 核心设计目标

| 目标 | 说明 |
|-----|------|
| **调用体验一致** | API 形式与 `IQuery.ParallelForEach` 保持一致 |
| **非阻塞调度** | `ScheduleUeParallel` 返回句柄自行管理 |
| **生命周期管理** | 提供 `UETasksJobHandle` 用于异步任务管理 |
| **依赖合并** | 支持 `Combine` 多任务句柄合并，减少等待开销 |
| **调试友好** | Debug 构建自动回退单线程执行 |

### 1.3 与现有系统的关系

```
+----------------------+         +-----------------------+
| IQuery<T...>         |         | IQuery<T...>          |
|  ParallelForEach     |         |  ScheduleUeParallel   |
| (PGD 原生实现)       |         | (UE Tasks 调度)       |
+----------+-----------+         +-----------+-----------+
           |                                   |
           v                                   v
+----------------------+         +-----------------------+
| ParallelManager      |         | FTasksQuery (C++)     |
| ExecuteTasks         |         | ScheduleBatch         |
| (阻塞)               |         | 返回 UETasksJobHandle |
+----------+-----------+         +-----------+-----------+
           |                                   |
           v                                   v
+----------------------+         +-----------------------+
| RunnerPool           |         | UE::Tasks             |
| taskCount -> runner  |         | Worker Threads        |
+----------+-----------+         +-----------------------+
           |
           v
+----------------------+
| Worker Threads        |
+----------------------+
```

系统通过 `ScheduleUeParallel` 返回 `UETasksJobHandle`，实现非阻塞调度，调用方自行决定等待时机。

---

## 2. 架构分层

```
+------------------+     +------------------+     +------------------+
| PGD (C# ECS)     |     | PGD Extensions   |     | UnrealCSharp     |
| IQuery<T...>     |     | UETasksExtensions|     | InternalCall     |
+------------------+     +--------+---------+     +--------+---------+
                                  |                        |
                                  v                        v
+------------------+     +--------+---------+     +--------+---------+
| 用户业务代码     |     | QueryRunner      |<--->| FTasksQuery     |
| Lambda/Delegate  |     | (1/2/3 params)   |     | (C++)           |
+------------------+     +------------------+     +------------------+
                                  |
                                  v
                         +--------+---------+
                         | UE::Tasks        |
                         | FTask Launch     |
                         +------------------+
```

---

## 3. 核心组件详解

### 3.1 FTasksQuery（C++）

**文件路径**：`Plugins/UnrealCSharp/Source/UnrealCSharp/Private/Domain/InternalCall/FTasksQuery.cpp`

**职责**：
1. UE::Tasks 任务创建与调度
2. 任务句柄管理（全局 Map）
3. Thunk 缓存与执行
4. 托管运行时线程 attach/detach

**核心静态变量**：

```cpp
std::atomic<int64> NextHandleId{1};                           // 句柄 ID 生成器
FCriticalSection HandleMutex;                                // 句柄 Map 锁
TMap<int64, TArray<UE::Tasks::FTask>> HandleTasks;           // 句柄 → 任务列表
```

**核心方法**：

| 方法 | 职责 |
|-----|------|
| `ScheduleBatch` | 异步调度，返回句柄 ID |
| `IsHandleCompleted` | 查询句柄是否完成 |
| `WaitHandle` | 阻塞等待句柄 |
| `ReleaseHandle` | 释放句柄资源 |
| `CombineHandles` | 合并多个句柄 |

### 3.2 FManagedJobScope（C++）

**职责**：托管运行时线程上下文管理

```cpp
struct FManagedJobScope
{
    explicit FManagedJobScope()
        : bEntered(FMonoDomain::TryEnterManagedJobExecution())
        , bDetachOnExit(FMonoDomain::ShouldDetachAfterManagedJob() && !IsInGameThread())
    {
        if (bEntered)
        {
            FMonoDomain::EnsureThreadAttached();  // 附加到 Mono 运行时
        }
    }

    ~FManagedJobScope()
    {
        if (!bEntered) return;

        if (bDetachOnExit)
        {
            FMonoDomain::EnsureThreadDetached();  // 分离线程
        }

        FMonoDomain::LeaveManagedJobExecution();
    }
};
```

**关键行为**：
- 进入任务前：`TryEnterManagedJobExecution()` + `EnsureThreadAttached()`
- 离开任务后：根据策略 `EnsureThreadDetached()`

### 3.3 IUETasksQueryRunner / UETasksQueryRunner（C#）

**文件路径**：`Plugins/UnrealCSharp/Script/UE/Library/UETasksQueryRunner.cs`

**接口定义**：

```csharp
public interface IUETasksQueryRunner
{
    void ExecuteTask(int taskIndex);
}

public static class UETasksQueryRunner
{
    public static void ExecuteTask(nint stateHandle, int index)
    {
        var handle = GCHandle.FromIntPtr((IntPtr)stateHandle);
        if (handle.Target is not IUETasksQueryRunner runner)
        {
            throw new InvalidOperationException("UETasksQueryRunner state handle is invalid.");
        }
        runner.ExecuteTask(index);
    }
}
```

**职责**：接收 C++ 传递的 stateHandle，通过 GCHandle 还原 Runner 并调用任务执行。

### 3.4 UETasksJobHandle（C#）

**文件路径**：`Plugins/UnrealCSharp/Script/UE/Library/UETasksJobHandle.cs`

**职责**：异步任务句柄封装，提供生命周期管理

**关键属性/方法**：

| 成员 | 类型 | 说明 |
|-----|------|------|
| `HandleId` | long | 句柄 ID（0 表示已完成/无效） |
| `handle` | GCHandle | Runner 对象句柄 |
| `extraHandles` | GCHandle[] | Combine 后额外持有的句柄 |
| `IsCompleted` | bool | 查询是否完成 |
| `Wait()` | void | 阻塞等待 |
| `Dispose()` | void | 释放资源 |

### 3.5 UETasksQueryExtensions（C#）

**文件路径**：`Script/Game/PGD/Parallel/UETasksQueryExtensions.cs`

**核心扩展方法**：

```csharp
// 非阻塞调度
public static UETasksJobHandle ScheduleUeParallel<T1>(this IQuery<T1> query, ForEachEntity<T1> lambda)
```

**执行流程**：

```
1. CollectChunks(query)          → 收集所有 ArchetypeChunk
2. 创建 QueryRunner              → 封装 runner 对象
3. GCHandle.Alloc(runner)        → 固定托管对象
4. Call ScheduleBatch(C++侧)
5. 返回 UETasksJobHandle
```

### 3.6 UETasksJobHandleUtils（C#）

**文件路径**：`Script/Game/PGD/Parallel/UETasksJobHandleUtils.cs`

**Combine 工具方法**：

```csharp
public static UETasksJobHandle Combine(params UETasksJobHandle[] handles)
```

**职责**：合并多个任务句柄为单个句柄，减少等待次数。

---

## 4. 调度流程详解

### 4.1 ScheduleUeParallel 执行流程

```
用户调用
    |
    v
UETasksQueryExtensions.ScheduleUeParallel()
    |
    v
CollectChunks(query) → QueryRunner → GCHandle.Alloc(runner)
    |
    v
FTasksQueryImplementation.ScheduleBatch(stateHandle, count)
    |
    v
FTasksQuery.cpp: ScheduleBatchImplementation()
    |
    +-- IsDebugSingleThread()? --> 直接执行 ExecuteOneTask，返回 0
    |
    +-- Launch UE::Tasks (count 个 FTask)
    |       |
    |       +-- FManagedJobScope (Attach)
    |       |
    |       +-- ExecuteOneTask(stateHandle, thunk, index)
    |       |
    |       +-- FManagedJobScope (Detach)
    |
    +-- 生成 HandleId (原子递增)
    |
    +-- HandleTasks[HandleId] = TaskList  (线程安全)
    |
    v
返回 UETasksJobHandle(HandleId, GCHandle)
    |
    v
用户自行管理生命周期
    |
    +-- handle.IsCompleted  → 查询完成状态
    |
    +-- handle.Wait()       → 阻塞等待
    |
    +-- handle.Dispose()
```

### 4.2 单任务优化

当 `chunkCount == 1` 时，跳过 UE Tasks 调度，直接在调用线程执行：

```csharp
if (count == 1)
{
    runner.ExecuteTask(0);  // 直接执行，无调度开销
    return UETasksJobHandle.Completed;
}
```

---

## 5. 任务调度策略

### 5.1 Worker 线程数获取

```cpp
static int32 GetNumWorkerThreadsImplementation()
{
    return FTaskGraphInterface::Get().GetNumWorkerThreads();
}
```

### 5.2 Chunk 与 Task 的映射

当前实现采用 **1:1 映射**：

- 每个 ArchetypeChunk 对应一个 UE Task
- TaskIndex 即为 ChunkIndex
- `MinBatchSize` 在 C# 侧通过合并 Chunk 保证（简化 C++ 侧逻辑）

### 5.3 Debug 单线程回退

```cpp
static bool IsDebugSingleThread()
{
#if UE_BUILD_DEBUG
    return true;
#else
    if (const auto Setting = FUnrealCSharpFunctionLibrary::GetMutableDefaultSafe<UUnrealCSharpSetting>())
    {
        return Setting->IsEnableDebug();
    }
    return false;
#endif
}
```

---

## 6. 生命周期管理

### 6.1 GCHandle 管理

```
+------------------+     +------------------+     +------------------+
| C# Runner 对象   | --> | GCHandle.Alloc   | --> | C++ stateHandle  |
| (托管堆)         |     | (Pinned)         |     | (指针传递)       |
+------------------+     +------------------+     +------------------+
                                                          |
                                                          v
+------------------+     +------------------+     +------------------+
| Dispose()        | <-- | GCHandle.Free    | <-- | 任务完成         |
| 释放 GCHandle    |     | 解除固定         |     |                  |
+------------------+     +------------------+     +------------------+
```

### 6.2 Handle 生命周期

```
ScheduleBatch()
    |
    v
HandleId++ → Map[HandleId] = TaskList
    |
    v
用户调用
    |
    +-- IsCompleted(): 检查 TaskList 所有任务状态
    |
    +-- Wait():        UE::Tasks::Wait(TaskList)
    |
    +-- Dispose():
    |       |
    |       +-- Wait(TaskList)  (确保任务完成)
    |       |
    |       +-- Map.Remove(HandleId)
    |       |
    |       +-- GCHandle.Free()
    |
    v
Handle 失效
```

### 6.3 Combine 句柄合并

```
Handle A: [Task1, Task2] --\
                           --> Combine() --> NewHandle: [Task1, Task2, Task3, Task4]
Handle B: [Task3, Task4] --/
```

**实现逻辑**：

```cpp
static int64 CombineHandlesImplementation(MonoArray* InHandleIds)
{
    // 1. 收集所有 HandleId 对应的 TaskList
    // 2. 从 Map 中移除原 Handle
    // 3. 合并所有 TaskList
    // 4. 生成新 HandleId，存入 Map
    // 5. 返回新 HandleId
}
```

---

## 7. 异常处理

### 7.1 托管异常捕获

```cpp
static void ExecuteOneTask(void* StateHandle, void* Thunk, int32 TaskIndex)
{
    MonoObject* Exception = nullptr;
    TypedThunk(StateHandle, TaskIndex, &Exception);  // 调用 C# thunk
    if (Exception != nullptr)
    {
        FMonoDomain::Unhandled_Exception(Exception);  // 抛到主线程
    }
}
```

### 7.2 异常传播

当前实现使用 `FMonoDomain::Unhandled_Exception` 直接抛出异常，同步模式下会中断等待。

---

## 8. 性能优化

### 8.1 Thunk 缓存

```cpp
struct FManagedThunkCache
{
    FCriticalSection Mutex;
    uint64 CachedKey = 0;      // 基于 Domain/Image 的缓存 key
    void* CachedThunk = nullptr;
};

static void* GetManagedThunkCached(FManagedThunkCache& Cache, ...)
{
    const uint64 Key = GetManagedLookupCacheKey();
    if (Cache.CachedThunk != nullptr && Cache.CachedKey == Key)
    {
        return Cache.CachedThunk;  // 缓存命中
    }
    // ... 缓存未命中，重新查找
}
```

### 8.2 锁粒度优化

```cpp
// HandleMutex 保护范围最小化
{
    FScopeLock ScopeLock(&HandleMutex);
    HandleTasks.Add(HandleId, MoveTemp(TaskList));
}
// 非临界区操作不需要锁
```

### 8.3 内存预分配

```cpp
TArray<UE::Tasks::FTask> TaskList;
TaskList.Reserve(InTaskCount);  // 预分配，减少扩容开销
```

---

## 9. 相关文件清单

| 文件 | 路径 | 最后修改 | 职责 |
|-----|------|---------|------|
| FTasksQuery.cpp | `Plugins/UnrealCSharp/Source/UnrealCSharp/Private/Domain/InternalCall/FTasksQuery.cpp` | 2026-01-30 | C++ 核心调度逻辑 |
| UETasksQueryRunner.cs | `Plugins/UnrealCSharp/Script/UE/Library/UETasksQueryRunner.cs` | 2026-01-28 | 任务执行入口接口 |
| UETasksJobHandle.cs | `Plugins/UnrealCSharp/Script/UE/Library/UETasksJobHandle.cs` | 2026-01-30 | 句柄封装与生命周期 |
| UETasksQueryExtensions.cs | `Script/Game/PGD/Parallel/UETasksQueryExtensions.cs` | 2026-01-29 | IQuery 扩展方法 |
| UETasksJobHandleUtils.cs | `Script/Game/PGD/Parallel/UETasksJobHandleUtils.cs` | 2026-01-30 | Combine 工具方法 |
| 原有设计文档 | `docs/tech/pgd/ue_tasks_parallel_query_design.md` | 2026-01-28 | 初始方案设计 |

---

## 10. 使用示例

### 10.1 基本调度

```csharp
var handle = query.ScheduleUeParallel((ref Position pos, ref Velocity vel) =>
{
    pos.Value += vel.Value * DeltaTime;
});

// 业务逻辑...
handle.Wait();  // 显式等待完成
handle.Dispose();
```

### 10.2 多任务合并

```csharp
var handle1 = query1.ScheduleUeParallel(...);
var handle2 = query2.ScheduleUeParallel(...);
var handle3 = query3.ScheduleUeParallel(...);

// 合并等待，减少等待次数
var combined = UETasksJobHandleUtils.Combine(handle1, handle2, handle3);
combined.Wait();
combined.Dispose();
```

---

## 11. 后续优化方向

1. **TaskCount 参数外化**：支持用户指定并行度，而非完全依赖 Chunk 数
2. **跨 World 支持**：当前假设单一 World，需验证多 World 场景
3. **性能基准测试**：建立与单线程、PGD 原生并行的对比基准
4. **异常聚合**：Combine 场景下聚合多个任务的异常信息
