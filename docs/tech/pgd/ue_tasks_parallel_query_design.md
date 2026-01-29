# 基于 UE Tasks 的 IQuery.ParallelForEach 方案设计（独立方案）

最后更新：2026-01-28

## 目标与边界
- 目标：提供一套“基于 UE Tasks”的并行化方案，调用体验尽量与 `IQuery.ParallelForEach` 一致，作为 `IQuery` 的扩展方法。
- 边界：这是独立方案，不需要与 PGD 内部并行系统做兼容或桥接，仅参考其使用形式。
- 语言与插件：基于 UnrealCSharp（C# 托管代码 + C++ internal call），运行在 UE5.x。

## 需要用户确认的关键信息
1. UE 版本（5.2/5.3/5.4/5.5）与目标平台（Win64/Console/Mobile）。
2. PGDCS 的 `IQuery` 真实签名与并行语义（是否允许写组件、是否允许结构变更）。
3. 你期望的 API 形式（是否要求与 PGD 原版 `ParallelForEach` 完全同名/同参数）。
4. 是否接受“默认同步等待”（异步需要额外的生命周期管理）。

## 本仓库内可参考的 UE Tasks 示例
- `Plugins/UnrealCSharp/Source/UnrealCSharp/Private/Domain/InternalCall/FTasksSlice.cpp`
- `Plugins/UnrealCSharp/Script/UE/Library/UETasksSliceBatch.cs`
- `Plugins/UnrealCSharp/Script/UE/Library/UETasksBatch.cs`

这些示例已经验证了“UE::Tasks worker 线程 + C# 托管执行 + Mono 线程 attach/detach”的可行路径。

## 设计概览
核心思路是把 Query 遍历“切片化”，再由 UE Tasks 调度切片并行执行，切片内的逐元素处理仍由 C# 执行。

```
+---------------------------+
| IQuery<T...>.ParallelForEach |
+-------------+-------------+
              |
              v
+---------------------------+
| QuerySliceBuilder (C#)    |
| - 收集 chunk/连续内存片段  |
+-------------+-------------+
              |
              v
+---------------------------+
| FTasksQuery (C++ internal)|
| - UE::Tasks 任务切分/调度  |
+-------------+-------------+
              |
              v
+---------------------------+
| UE::Tasks Worker Threads  |
| - EnsureThreadAttached    |
| - 调用托管 Slice Thunk     |
+-------------+-------------+
              |
              v
+---------------------------+
| 用户 Action/Delegate (C#) |
+---------------------------+
```

## API 设计（C# 侧）
> 注意：C# 不支持 `Action<ref T>`，需要自定义 delegate。

```csharp
namespace PGD.Parallel
{
    public delegate void QueryAction<T1>(ref T1 c1);
    public delegate void QueryAction<T1, T2>(ref T1 c1, ref T2 c2);

    public struct UEParallelOptions
    {
        public int TaskCount;        // 0 or <0 -> auto
        public int MinBatchSize;     // 默认 128 或 256
        public bool Wait;            // 默认 true
        public bool AllowCapture;    // true: Runtime_Invoke；false: unmanaged thunk
    }

    public static class QueryParallelExtensions
    {
        public static void ParallelForEach<T1>(
            this IQuery<T1> query,
            QueryAction<T1> action,
            UEParallelOptions? options = null);

        public static void ParallelForEach<T1, T2>(
            this IQuery<T1, T2> query,
            QueryAction<T1, T2> action,
            UEParallelOptions? options = null);

        // 可选：异步版本（需要额外的生命周期策略）
        // public static UETaskHandle ParallelForEachAsync(...)
    }
}
```

### 使用示例（与现有 ParallelForEach 风格对齐）
```csharp
query.ParallelForEach((ref Position pos, ref Velocity vel) =>
{
    pos.Value += vel.Value * dt;
}, new UEParallelOptions
{
    TaskCount = 0,        // 自动推导
    MinBatchSize = 256,
    Wait = true,
    AllowCapture = false // 走高性能 thunk
});
```

## 核心流程（执行路径）
1. **QuerySliceBuilder（C#）**
   - 从 `IQuery` 中提取 Chunk/Archetype 的连续内存段。
   - 构造 `QuerySlice` 列表（每个 slice 记录组件指针、stride、count）。
   - 合并小切片（低于 `MinBatchSize`）以减少调度开销。

2. **InternalCall（C++）**
   - 接收 `QuerySlice[]` 的 pinned 指针与数量。
   - 使用 UE::Tasks 创建 `FTask` 列表，按 `TaskCount` 切分 slices。
   - 在 worker 线程执行 `ManagedThunk`。

3. **托管调用（C#）**
   - `ExecuteSlice` 负责读取 slice 中的组件指针并调用用户 action。
   - 如果是 `AllowCapture=true`，走 Runtime_Invoke（可闭包、慢）。
   - 如果 `AllowCapture=false`，仅允许 static 方法（走 unmanaged thunk，快）。

## QuerySlice 结构建议
> 具体字段依赖 PGD 数据布局，下述为参考形态。

```csharp
// 仅示意
[StructLayout(LayoutKind.Sequential)]
internal struct QuerySlice
{
    public nint Comp0;
    public nint Comp1;
    public int Stride0;
    public int Stride1;
    public int Count;
}
```

如果 PGDCS 的 Query 能提供“chunk + base pointer + count + stride”，可直接映射到以上结构；否则需要在 C# 侧先做一次复制/重排（代价更高）。

## UE Tasks 调度策略（建议）
- `TaskCount = 0` 时自动推导：优先使用 `UE::Tasks::GetNumWorkerThreads()`；若无法暴露，则退化为 `Environment.ProcessorCount - 1`。
- `MinBatchSize` 小于阈值时合并 slice，保证单任务工作量足够。
- `TaskCount` clamp：`[1, TotalSlices]`。

## 线程与托管运行时
- 每个 worker 线程进入 C# 前调用 `FMonoDomain::EnsureThreadAttached()`。
- 任务结束后根据策略 `EnsureThreadDetached()`，避免 Editor Play/Stop 复用线程导致的问题。
- 建议沿用 `FTasksSlice` 的 `FManagedJobScope` 方案（见现有实现）。

## 异常与取消
- UE Tasks 无直接“取消 token”，建议在 C# 侧提供 `Volatile<bool>` 或 `CancellationToken` 轮询。
- 异常处理：
  - 方案 A：C++ 捕获首个 `MonoObject* Exception`，记录并在主线程抛出。
  - 方案 B：C# `ConcurrentQueue<Exception>` 汇总，Wait 后统一抛 `AggregateException`。

## 性能与易用性取舍
- **快路径（推荐）**：`AllowCapture=false`，用户传 static 方法；走 unmanaged thunk，开销最低。
- **易用路径**：`AllowCapture=true`，允许闭包但走 Runtime_Invoke，性能略低。
- **数据访问**：尽量让 slice 内循环在 C# 侧完成，避免 C++/C# 频繁往返。

## 验证与测试建议
1. 使用 `UETasksSlicePerfRunner` 的对比模式做基线测试。
2. 加入多轮 Play/Stop 验证线程 attach/detach 稳定性。
3. 使用大数组与高并发切片测试异常处理与任务分配稳定性。

## 待确认的问题（请补充）
1. PGDCS `IQuery` 的真实 API 和可访问的“chunk/连续内存”接口。
2. `ParallelForEach` 是否允许写组件、是否允许结构变更（Add/Remove Component）。
3. 是否需要异步版本（不 Wait），以及可接受的生命周期管理成本。
