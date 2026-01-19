# TaskGraph 性能收益验证：方案与结果汇总

本文仅汇总“截至目前已在 StackOBot 内实现并跑过的 TaskGraph 性能验证方案与过程”，并整理用户提供的测试结果（以表格记录）。所有结论均基于当前测试代码与日志结果。

## 1. 范围与目标

- 目标：验证 “TaskGraph + native kernel” 在纯 ECS 数据遍历任务中的性能收益，并与 C# 常用并行能力（Parallel.For / Task.Run）对比。
- 约束：
  - 不触碰 UE 对象（纯数据）。
  - 不引入 PGD（仅在 StackOBot 内模拟）。
  - 任务形态以 “组件遍历修改（IQuery.ForeachEntity 类似）” 为主。

## 2. 测试方法与通用配置

- 度量方式：
  - `warmup` 次预热后进行 `iterations` 次循环计时。
  - 以 `rounds` 多轮对比，输出中位数 `Median`。
  - 每轮比对 `sumOk=True`，验证三条路径计算等价。
- 输出字段：
  - `tgMedian`：TaskGraph + native kernel 中位数耗时（ms）
  - `pfMedian`：C# Parallel.For 中位数耗时（ms）
  - `trMedian`：C# Task.Run 中位数耗时（ms）
  - `ratioPf/ratioTr`：与 TaskGraph 的耗时比

## 3. 验证方案一：单数组 AddOneAndSum（NativeBuffer）

### 3.1 方案说明

- 任务：对 `NativeBuffer<int>` 做 `data[i] += 1` 并求和。
- 目的：验证 “native kernel + TaskGraph” 相对 “C# Parallel.For / Task.Run” 的基础收益。
- 入口路径：
  - TaskGraph：`Plugins/UnrealCSharp/Source/UnrealCSharp/Private/Domain/InternalCall/FNativeBufferTaskGraph.cpp`
  - C# 比较器：`Plugins/UnrealCSharp/Script/UE/Library/TaskGraphVsCSharpPerfRunner.cs`
  - C# 并行实现：`Plugins/UnrealCSharp/Script/UE/Library/CSharpParallelPerf.cs`
- 参数：`length=100000, taskCount=8, iterations=32, warmup=3, rounds=5`

### 3.2 方案设计要点（流程与公平性）

- 数据在 native 内存：`NativeBuffer<int>`，C++ 与 C# 都通过指针访问同一块内存。
- 切分策略一致：按 `taskCount` 均分 chunk，三条路径都处理同样的 `[start, end)` 区间。
- 顺序交错与中位数：`TG -> PF -> TR` 等交错顺序，`rounds` 取中位数抑制噪声。
- 正确性校验：每轮比较 `sumOk`，确保结果等价。

### 3.3 关键代码示例（节选）

TaskGraph native kernel（C++，按 chunk 分发）：

```cpp
// FNativeBufferTaskGraph.cpp
const int32 ChunkSize = (InLength + SafeTaskCount - 1) / SafeTaskCount;
for (int32 TaskIndex = 0; TaskIndex < SafeTaskCount; ++TaskIndex)
{
    const int32 Start = TaskIndex * ChunkSize;
    const int32 End = FMath::Min(Start + ChunkSize, InLength);
    Events.Add(FFunctionGraphTask::CreateAndDispatchWhenReady([Data, Start, End, TaskIndex, &PartialSums]()
    {
        int64 LocalSum = 0;
        for (int32 i = Start; i < End; ++i) { Data[i] += 1; LocalSum += Data[i]; }
        PartialSums[TaskIndex] = LocalSum;
    }, TStatId(), nullptr, ENamedThreads::AnyBackgroundThreadNormalTask));
}
```

C# Parallel.For（按 chunk 的等价实现）：

```csharp
// CSharpParallelPerf.cs (chunked)
Parallel.For(0, taskCount, options,
    () => 0L,
    (taskIndex, _, local) =>
    {
        var start = taskIndex * chunkSize;
        var end = Math.Min(start + chunkSize, len);
        for (var i = start; i < end; i++) { data[i] += 1; local += data[i]; }
        return local;
    },
    local => Interlocked.Add(ref sum, local));
```

C# Task.Run（按 chunk 的等价实现）：

```csharp
// CSharpParallelPerf.cs (Task.Run)
for (var taskIndex = 0; taskIndex < taskCount; taskIndex++)
{
    var start = taskIndex * chunkSize;
    var end = Math.Min(start + chunkSize, len);
    tasks[taskIndex] = Task.Run(() =>
    {
        long local = 0;
        for (var i = start; i < end; i++) { data[i] += 1; local += data[i]; }
        return local;
    });
}
Task.WaitAll(tasks);
```

对比器（交错顺序 + 中位数）：

```csharp
// TaskGraphVsCSharpPerfRunner.cs
var orderIndex = r % 3; // TG->PF->TR / PF->TR->TG / TR->TG->PF
// 每轮记录 tg/pf/tr 的耗时，并输出中位数
```

### 3.4 结果（5 组独立运行）

| 运行 | tgMedian(ms) | pfMedian(ms) | trMedian(ms) | ratioPf | ratioTr |
| --- | --- | --- | --- | --- | --- |
| 1 | 1.044 | 1.803 | 2.016 | 1.727 | 1.931 |
| 2 | 0.948 | 1.966 | 1.857 | 2.075 | 1.960 |
| 3 | 0.781 | 1.786 | 2.272 | 2.288 | 2.910 |
| 4 | 0.805 | 1.770 | 1.953 | 2.197 | 2.425 |
| 5 | 0.609 | 2.257 | 2.410 | 3.704 | 3.955 |

## 4. 验证方案二：ECS 多 archetype + 多 section（固定参数）

### 4.1 方案说明

- 任务：模拟 `Position/Velocity` 两组件更新：`pos[i] += vel[i] * dt`。
- archetype 长度：`[120000, 35000, 9000, 2500]`。
- 切分规则：`minParallelChunkSize=10000`。
- 入口路径：
  - TaskGraph：`Plugins/UnrealCSharp/Source/UnrealCSharp/Private/Domain/InternalCall/FNativeBufferTaskGraphEcs.cpp`
  - 数据构建与 C# 对比：`Plugins/UnrealCSharp/Script/UE/Library/EcsArchetypePerf.cs`
  - 运行器：`Plugins/UnrealCSharp/Script/UE/Library/TaskGraphVsCSharpEcsPerfRunner.cs`
- 参数：`taskCount=8, iterations=32, warmup=3, rounds=5, dt=1`

### 4.2 方案设计要点（更贴近 IQuery.ForeachEntity）

- 多 archetype：每个 archetype 有独立连续数组（Position/Velocity）。
- 多 section：每个 archetype 按 `minParallelChunkSize` 切分为多个 slice。
- 三条路径使用同一 `SliceDesc[]`，确保切分策略一致。

### 4.3 关键代码示例（节选）

切分规则（按 `minParallelChunkSize` 生成 slice）：

```csharp
// EcsArchetypePerf.BuildSlices
if (length < minParallelChunkSize)
{
    slices.Add(new SliceDesc { ArchetypeIndex = idx, Start = 0, Length = length });
}
else
{
    var sectionCount = Math.Min(taskCount, (length + minParallelChunkSize - 1) / minParallelChunkSize);
    var sectionSize = (length + sectionCount - 1) / sectionCount;
    for (var s = 0; s < sectionCount; s++)
    {
        var start = s * sectionSize;
        if (start >= length) break;
        slices.Add(new SliceDesc { ArchetypeIndex = idx, Start = start, Length = Math.Min(sectionSize, length - start) });
    }
}
```

TaskGraph native kernel（每个 slice 一个任务）：

```cpp
// FNativeBufferTaskGraphEcs.cpp
Events.Add(FFunctionGraphTask::CreateAndDispatchWhenReady([Archetypes, Slices, SliceIndex, &PartialSums]()
{
    const FSliceDesc& Slice = Slices[SliceIndex];
    const FArchetypeDesc& Arch = Archetypes[Slice.ArchetypeIndex];
    for (int32 i = Slice.Start; i < End; ++i)
    {
        Arch.Position[i] += Arch.Velocity[i] * InDt;
        LocalSum += Arch.Position[i];
    }
    PartialSums[SliceIndex] = LocalSum;
}, TStatId(), nullptr, ENamedThreads::AnyBackgroundThreadNormalTask));
```

### 4.4 结果（5 组独立运行）

| 运行 | tgMedian(ms) | pfMedian(ms) | trMedian(ms) | ratioPf | ratioTr |
| --- | --- | --- | --- | --- | --- |
| 1 | 1.383 | 3.184 | 4.468 | 2.302 | 3.230 |
| 2 | 1.494 | 3.895 | 3.510 | 2.607 | 2.349 |
| 3 | 1.330 | 3.103 | 3.125 | 2.333 | 2.350 |
| 4 | 1.261 | 3.333 | 3.413 | 2.644 | 2.707 |
| 5 | 1.626 | 3.377 | 3.013 | 2.077 | 1.853 |

## 5. 验证方案三：ECS 参数扫测（taskCount × minChunk）

### 5.1 方案说明

- 扫测参数：
  - `taskCount = [4, 8, 16]`
  - `minParallelChunkSize = [5000, 10000, 20000]`
- 参数：`iterations=32, warmup=3, rounds=5, dt=1`。
- 入口：`TaskGraphVsCSharpEcsPerfRunner.RunPosVelArchetypeCompareSweep(...)`。
- 备注：用户说明进行了 3 次扫测，但日志中仅包含 2 次完整 sweep 结果。以下整理以日志为准，记为 Run A / Run B。

### 5.2 关键代码示例（扫测入口）

```csharp
// TaskGraphVsCSharpEcsPerfRunner.cs
TaskGraphVsCSharpEcsPerfRunner.RunPosVelArchetypeCompareSweep(
    taskCounts: new[] { 4, 8, 16 },
    minParallelChunkSizes: new[] { 5_000, 10_000, 20_000 },
    iterations: 32,
    warmup: 3,
    rounds: 5,
    dt: 1);
```

### 5.3 Sweep Run A（9 组组合）

| taskCount | minChunk | tgMedian(ms) | pfMedian(ms) | trMedian(ms) | ratioPf | ratioTr |
| --- | --- | --- | --- | --- | --- | --- |
| 4 | 5000 | 0.945 | 4.580 | 4.576 | 4.845 | 4.840 |
| 4 | 10000 | 0.865 | 4.576 | 3.539 | 5.288 | 4.090 |
| 4 | 20000 | 0.690 | 4.681 | 3.546 | 6.789 | 5.142 |
| 8 | 5000 | 0.898 | 2.780 | 3.087 | 3.094 | 3.436 |
| 8 | 10000 | 0.623 | 2.904 | 3.197 | 4.663 | 5.134 |
| 8 | 20000 | 0.586 | 3.587 | 3.662 | 6.123 | 6.252 |
| 16 | 5000 | 0.796 | 2.935 | 3.886 | 3.686 | 4.880 |
| 16 | 10000 | 0.927 | 3.701 | 4.361 | 3.994 | 4.705 |
| 16 | 20000 | 0.564 | 3.773 | 4.675 | 6.687 | 8.286 |

### 5.4 Sweep Run B（9 组组合）

| taskCount | minChunk | tgMedian(ms) | pfMedian(ms) | trMedian(ms) | ratioPf | ratioTr |
| --- | --- | --- | --- | --- | --- | --- |
| 4 | 5000 | 0.667 | 4.335 | 4.831 | 6.495 | 7.238 |
| 4 | 10000 | 0.623 | 4.710 | 4.488 | 7.566 | 7.209 |
| 4 | 20000 | 0.601 | 4.518 | 3.797 | 7.523 | 6.322 |
| 8 | 5000 | 1.047 | 2.729 | 3.228 | 2.607 | 3.083 |
| 8 | 10000 | 0.902 | 3.443 | 3.637 | 3.817 | 4.032 |
| 8 | 20000 | 0.539 | 3.550 | 3.525 | 6.586 | 6.540 |
| 16 | 5000 | 1.142 | 3.438 | 3.419 | 3.012 | 2.995 |
| 16 | 10000 | 0.819 | 3.744 | 3.527 | 4.572 | 4.307 |
| 16 | 20000 | 0.565 | 3.579 | 3.540 | 6.332 | 6.263 |

## 6. 当前结论（仅基于本测试）

- 对 “组件遍历修改（IQuery.ForeachEntity 类似）” 这一类纯数据 ECS 任务：
  - TaskGraph + native kernel 在当前基准中稳定更快，且 `sumOk=True` 证明逻辑等价。
  - 优势幅度在不同粒度组合下变化明显，整体为 2x~8x 区间。
- 这些结果不能推出 “所有 ECS workload 都有相同收益”，但对 “纯遍历 + 原地写回” 场景具备直接参考意义。

## 7. 对应代码入口

- 单数组基准：
  - `Plugins/UnrealCSharp/Source/UnrealCSharp/Private/Domain/InternalCall/FNativeBufferTaskGraph.cpp`
  - `Plugins/UnrealCSharp/Script/UE/Library/TaskGraphVsCSharpPerfRunner.cs`
  - `Plugins/UnrealCSharp/Script/UE/Library/CSharpParallelPerf.cs`
- ECS 多 archetype 基准：
  - `Plugins/UnrealCSharp/Source/UnrealCSharp/Private/Domain/InternalCall/FNativeBufferTaskGraphEcs.cpp`
  - `Plugins/UnrealCSharp/Script/UE/Library/EcsArchetypePerf.cs`
  - `Plugins/UnrealCSharp/Script/UE/Library/TaskGraphVsCSharpEcsPerfRunner.cs`
- 运行入口：
  - `Script/Game/Game/StackOBot/UI/MainMenu/MainMenu_C.cs`
