# UE::Tasks（方案A：Slice Thunk）设计说明与实测结果（含 Managed Pinned / NativeBuffer）

> 文档目的：把“用 UE::Tasks 在 worker 线程执行 C# slice 逻辑”的整体设计思路、关键链路、任务切分方式、使用方式与实测结果讲清楚（面向新手）。
>
> 结论先行（基于本文附带日志）：在给定参数下，UE::Tasks slice 方案整体上 **明显快于 Parallel.For**；与 Task.Run 的相对关系随参数变化（有时接近，有时 UE::Tasks 更慢/更快）。
>
> 说明：本文记录的结果来自用户提供的 Output Log；运行环境（CPU、线程数、是否 Debug、是否 Editor、是否开启调试器）未记录，因此本文只对“同机同环境下的相对趋势”负责。

## 1. 背景与目标

我们要解决的问题是：**让 UE::Tasks 在引擎的 worker 线程上执行 C# 逻辑**，并与 C# 原生并行（`Task.Run`、`Parallel.For`）在“同一份工作”下做性能对比。

### 1.1 关键约束

- **不能让 C++ 每处理一个元素就跨边界调用一次 C#**（per-element thunk）  
  这会把跨边界成本放大到“元素数量级”，性能会非常差。
- 因此采用 **按 slice（区间）一次调用 C#** 的模式：  
  C++ 负责把数组分成多个 slice，并发执行；每个 slice 只 thunk 进 C# 一次；C# 在 slice 内循环处理元素。

### 1.2 两个版本（同一设计，数据来源不同）

为了分别观察 GC/Pin 对性能的影响，提供两套独立测试用例：

1) **Managed Pinned 版**：输入是 `int[]`（托管数组），通过 `GCHandleType.Pinned` 固定地址，再把指针交给 C++。  
2) **NativeBuffer 版**：输入是 `NativeBuffer<int>`（native heap 连续内存），直接把 `Ptr` 交给 C++，不需要 pin。

## 2. 整体设计（方案A：C++ 切分 + C# slice 处理）

### 2.1 文件与入口速查

- C++（internal call + UE::Tasks 切分调度）  
  `Plugins/UnrealCSharp/Source/UnrealCSharp/Private/Domain/InternalCall/FTasksSlice.cpp`
- C#（internal call 声明）  
  `Plugins/UnrealCSharp/Script/UE/Library/FTasksSliceImplementation.cs`
- C#（固定 thunk 入口 + handler 注入）  
  `Plugins/UnrealCSharp/Script/UE/Library/UETasksSliceBatch.cs`
- C#（性能对比用例：两套独立对比）  
  `Plugins/UnrealCSharp/Script/UE/Library/UETasksSlicePerfRunner.cs`

### 2.2 总流程图（ASCII）

```
------------------------------+
| C# 测试用例（Runner）       |
| UETasksSlicePerfRunner.*    |
+--------------+---------------+
               |
               v
------------------------------+
| C# 注入处理逻辑             |
| UETasksSliceBatch.Run*      |
| - 保存 handler              |
| - (Managed) Pin int[]       |
| - 调 internal call          |
+--------------+---------------+
               |
               v
------------------------------+
| C++ internal call           |
| FTasksSlice.ExecuteBatch    |
| (dataPtr, length, taskCount)|
| - 计算 chunkSize            |
| - 切分 slice                |
| - Launch UE::Tasks          |
+--------------+---------------+
               |
               v
------------------------------+
| UE worker 线程              |
| 每个任务：                  |
| - thunk 直调 C# ExecuteSlice|
|   (dataPtr, start, count)   |
+--------------+---------------+
               |
               v
------------------------------+
| C# 固定入口 ExecuteSlice     |
| - 调用已注入 handler         |
|   handler(data/ptr,start,count)|
+------------------------------+
```

### 2.3 为什么 C++ 不能“直接调用开发者传入的任意方法”

UE 侧要调用托管方法，必须有一个 **稳定可查找的入口点**（`MonoMethod` / thunk），因此 C++ 需要调用一个固定的托管函数（本文为 `UETasksSliceBatch.ExecuteSlice`）。  
开发者的逻辑通过 **C# 侧注入 handler** 的方式解耦：C++ 不需要知道逻辑是什么，只需要负责切分和调度。

> 结论（对应你之前的问题）：在“不增加新接口（把托管方法句柄/函数指针传入 C++ 并长期持有）”的前提下，C++ 很难做到“直接调用开发者传入的任意方法”。当前采取的“固定 thunk 入口 + C# 注入 handler”是兼顾**接口不变**与**解耦**的折中。

## 3. 任务切分规则（slice 如何计算）

在 C++ 中（`FTasksSlice.cpp`）使用以下规则把 `[0, length)` 切成 `taskCount` 份：

- `safeTaskCount = clamp(taskCount, 1, length)`
- `chunkSize = ceil(length / safeTaskCount)`
- 第 `k` 个 slice：
  - `start = k * chunkSize`
  - `count = min(chunkSize, length - start)`
  - slice 区间为 `[start, start + count)`

### 3.1 切分示意图（ASCII）

以 `length=10`，`taskCount=3` 为例：

```
---------+---------+--------+
 [0..4)    [4..8)    [8..10)
   4         4          2
```

## 4. 关键实现（带注释的简化示例）

> 注意：以下是“解释用的精简版”，真实代码以仓库文件为准。

### 4.1 C++：切分 + UE::Tasks 调度（每个 slice thunk 一次）

```cpp
// C++ 侧：接收 data 指针 + length + taskCount
// 计算 slice 并 Launch UE::Tasks
static void ExecuteBatchImplementation(const void* InData, int32 InLength, int32 InTaskCount, bool bWait)
{
    // 1) 通过 Mono 找到 C# 的固定入口：UETasksSliceBatch.ExecuteSlice(data,start,count)
    //    并获取 unmanaged thunk（避免 Runtime_Invoke 的高开销）。

    // 2) 计算 chunkSize 并切分 slice
    const int32 SafeTaskCount = FMath::Clamp(InTaskCount, 1, InLength);
    const int32 ChunkSize = FMath::DivideAndRoundUp(InLength, SafeTaskCount);

    // 3) 为每个 slice Launch 一个 UE::Tasks 任务
    for (int32 k = 0; k < SafeTaskCount; ++k)
    {
        const int32 Start = k * ChunkSize;
        const int32 Count = FMath::Min(ChunkSize, InLength - Start);
        if (Count <= 0) continue;

        UE::Tasks::FTask Task;
        Task.Launch(TEXT("UETasksSlice.ExecuteBatch"), [DataPtr, Start, Count, Thunk]()
        {
            // 4) worker 线程：thunk 直调 C# ExecuteSlice（一次调用处理一个 slice）
            //    关键点：跨 C++/C# 边界的调用次数 ≈ slice 数 ≈ taskCount，而不是 length。
            Thunk(DataPtr, Start, Count, &Exception);
        });
    }

    // 5) 同步等待（测试用例固定 wait=true）
    UE::Tasks::Wait(Tasks);
}
```

### 4.2 C#：RunManaged/RunNative 注入 handler（开发者只写这里的逻辑）

```csharp
// Managed：开发者写一个 handler，参数是 (int[] arr, int start, int count)
// - start/count 就是 C++ 侧切出来的 slice 区间
// - 该 handler 会在多个 worker 线程并发执行，所以必须线程安全
UETasksSliceBatch.RunManaged(data, taskCount, (arr, start, count) =>
{
    var end = start + count;
    for (var i = start; i < end; i++)
    {
        arr[i] += 1;
        // 这里可以替换成任意业务逻辑
    }
});

// Native：开发者写一个 handler，参数是 (nint ptr, int start, int count)
// - ptr 指向 NativeBuffer<int> 的 native 连续内存
// - 需要 unsafe 才能把 ptr 当作 int* 做指针运算
UETasksSliceBatch.RunNative(buf, taskCount, (ptr, start, count) =>
{
    unsafe
    {
        var p = (int*)ptr;
        var end = start + count;
        for (var i = start; i < end; i++)
        {
            p[i] += 1;
        }
    }
});
```

### 4.3 为什么托管数组必须 Pin

`int[]` 在托管堆上，GC 可能移动它的位置。  
UE::Tasks 在 worker 线程异步执行，C++ 必须在整个任务执行期间拿到**稳定不变**的指针，因此 Managed 版本用：

- `GCHandle.Alloc(data, GCHandleType.Pinned)` 固定数组地址
- 任务完成后 `handle.Free()`

NativeBuffer 版本不需要 pin，因为它的数据本来就在 native heap。

## 5. 性能测试方案（两套用例分开跑）

两个 Runner（独立用例，互不干扰）：

- 托管数组（Pinned）对比：  
  `UETasksSlicePerfRunner.RunManagedPinnedAddOneAndSumCompare(...)`
- NativeBuffer 对比：  
  `UETasksSlicePerfRunner.RunNativeBufferAddOneAndSumCompare(...)`

每个 Runner 内部都会分别跑三条路径（同任务、同切片规则）：

- UE::Tasks（slice thunk）  
- `Parallel.For`（`MaxDegreeOfParallelism = taskCount`）  
- `Task.Run`（创建 taskCount 个 Task 并 WaitAll）

统计口径：
- 每个 Round：按顺序 UE->PF->TR 依次测量一次总耗时
- 总结：取 rounds 的 **平均值（Avg）**

> 说明：这里的 `ue=xxms/pf=xxms/tr=xxms` 是“iterations 次循环的总耗时”，不是单次耗时。

## 6. 实测结果（来自用户提供日志）

### 6.1 Case A：length=10000, taskCount=3（iterations=500）

| 版本 | UE Avg (ms) | PF Avg (ms) | TR Avg (ms) | ratioPf | ratioTr |
| --- | --- | --- | --- | --- | --- |
| Managed Pinned | 8.734 | 16.012 | 8.635 | 1.833 | 0.989 |
| NativeBuffer | 8.293 | 15.959 | 7.534 | 1.924 | 0.909 |

### 6.2 Case B：length=10000, taskCount=4（iterations=500）

| 版本 | UE Avg (ms) | PF Avg (ms) | TR Avg (ms) | ratioPf | ratioTr |
| --- | --- | --- | --- | --- | --- |
| Managed Pinned | 9.181 | 16.994 | 6.234 | 1.851 | 0.679 |
| NativeBuffer | 8.787 | 16.652 | 6.101 | 1.895 | 0.694 |

### 6.3 Case C：length=10000, taskCount=2（iterations=500）

| 版本 | UE Avg (ms) | PF Avg (ms) | TR Avg (ms) | ratioPf | ratioTr |
| --- | --- | --- | --- | --- | --- |
| Managed Pinned | 9.260 | 11.387 | 9.292 | 1.230 | 1.003 |
| NativeBuffer | 7.658 | 11.436 | 8.153 | 1.493 | 1.065 |

### 6.4 Case D：length=200000, taskCount=6（iterations=500）

| 版本 | UE Avg (ms) | PF Avg (ms) | TR Avg (ms) | ratioPf | ratioTr |
| --- | --- | --- | --- | --- | --- |
| Managed Pinned | 31.157 | 33.173 | 39.414 | 1.065 | 1.265 |
| NativeBuffer | 26.312 | 33.097 | 36.480 | 1.258 | 1.386 |

## 7. 结果解读（只基于本文日志）

1) **UE::Tasks vs Parallel.For：稳定优势**  
在所有 Case 中，PF 都明显更慢（ratioPf > 1）。

2) **UE::Tasks vs Task.Run：随参数变化**  
小数据/低并行度时 TR 可能更接近 UE 或更快；数据量增大后 UE 更有优势（Case D 中 UE 显著快于 TR）。

3) **NativeBuffer 通常更快**  
对比 Managed Pinned 与 NativeBuffer，NativeBuffer 大多更快，符合“减少 GC/Pin 干扰”的直觉。

> 注意：Task.Run 的波动可能更大（线程池调度、其他系统线程竞争、Editor 状态等都会影响）。

## 8. 使用与扩展建议（开发者怎么写自己的逻辑）

如果开发者要定义“别的处理逻辑”，只需要写一个 handler：

- Managed：`(int[] arr, int start, int count) => { ... }`
- Native：`(nint ptr, int start, int count) => { unsafe { ... } }`

建议（避免踩坑）：
- handler 必须是 **线程安全** 的（会并发执行多个 slice）
- 尽量不要在 handler 内频繁 `Console.WriteLine`（I/O 会吞掉并行收益）
- handler 内不要抛异常（异常会被 UE 侧视为 Unhandled，可能中断测试）

## 9. 怎么运行这两套测试用例（本仓库最小步骤）

本仓库里已经把两套 Runner 临时挂在主菜单关卡的 `BeginPlay` 里：

- 入口脚本：`Script/Game/Game/StackOBot/UI/MainMenu/MainMenu_C.cs`
- 调用点：`MainMenu_C.ReceiveBeginPlay()`
  - `UETasksSlicePerfRunner.RunManagedPinnedAddOneAndSumCompare(...)`
  - `UETasksSlicePerfRunner.RunNativeBufferAddOneAndSumCompare(...)`

操作步骤（面向第一次跑的人）：

1) 打开 UE Editor，进入主菜单关卡（StackOBot 默认项目启动会进入该关卡）
2) 点击 `Play`
3) 打开 `Output Log`，过滤关键字：`UETasksSlice`
4) 观察每个 round 的 `ue/pf/tr`，以及最终 summary 的 `ueAvg/pfAvg/trAvg` 与比值

> 注意：这两个测试会占用一段时间（它们跑了 `iterations=500`，并做 warmup/rounds）。如果你不希望每次进 PIE 都跑，后续可以改成按钮触发或 Console Command 触发。

## 10. 附录：本次日志原文（用户提供）

> 下面日志用于“对照文档中的 summary 表格是否抄写正确”。如果你后续换了机器/参数重新跑，建议把新的完整日志也附在工单里，方便回溯。

```text
LogUnrealCSharp: [UETasksSliceManagedRound] round=1/5 order=UE->PF->TR ue=9.512ms pf=16.399ms tr=9.536ms sumOk=True
LogUnrealCSharp: [UETasksSliceManagedRound] round=2/5 order=UE->PF->TR ue=8.582ms pf=16.047ms tr=8.335ms sumOk=True
LogUnrealCSharp: [UETasksSliceManagedRound] round=3/5 order=UE->PF->TR ue=8.681ms pf=15.957ms tr=8.197ms sumOk=True
LogUnrealCSharp: [UETasksSliceManagedRound] round=4/5 order=UE->PF->TR ue=8.568ms pf=15.463ms tr=8.929ms sumOk=True
LogUnrealCSharp: [UETasksSliceManagedRound] round=5/5 order=UE->PF->TR ue=8.328ms pf=16.195ms tr=8.176ms sumOk=True
LogUnrealCSharp: [UETasksSliceManagedSummary] length=10000 taskCount=3 iterations=500 warmup=5 rounds=5 ueAvg=8.734ms pfAvg=16.012ms trAvg=8.635ms ratioPf=1.833 ratioTr=0.989
LogUnrealCSharp: [UETasksSliceNativeRound] round=1/5 order=UE->PF->TR ue=8.216ms pf=16.035ms tr=8.457ms sumOk=True
LogUnrealCSharp: [UETasksSliceNativeRound] round=2/5 order=UE->PF->TR ue=8.441ms pf=15.429ms tr=6.354ms sumOk=True
LogUnrealCSharp: [UETasksSliceNativeRound] round=3/5 order=UE->PF->TR ue=8.326ms pf=16.052ms tr=7.485ms sumOk=True
LogUnrealCSharp: [UETasksSliceNativeRound] round=4/5 order=UE->PF->TR ue=8.216ms pf=15.944ms tr=7.513ms sumOk=True
LogUnrealCSharp: [UETasksSliceNativeRound] round=5/5 order=UE->PF->TR ue=8.266ms pf=16.331ms tr=7.862ms sumOk=True
LogUnrealCSharp: [UETasksSliceNativeSummary] length=10000 taskCount=3 iterations=500 warmup=5 rounds=5 ueAvg=8.293ms pfAvg=15.959ms trAvg=7.534ms ratioPf=1.924 ratioTr=0.909

LogUnrealCSharp: [UETasksSliceManagedRound] round=1/5 order=UE->PF->TR ue=10.601ms pf=18.931ms tr=5.460ms sumOk=True
LogUnrealCSharp: [UETasksSliceManagedRound] round=2/5 order=UE->PF->TR ue=8.758ms pf=16.302ms tr=5.725ms sumOk=True
LogUnrealCSharp: [UETasksSliceManagedRound] round=3/5 order=UE->PF->TR ue=8.720ms pf=16.215ms tr=5.361ms sumOk=True
LogUnrealCSharp: [UETasksSliceManagedRound] round=4/5 order=UE->PF->TR ue=8.990ms pf=16.840ms tr=5.575ms sumOk=True
LogUnrealCSharp: [UETasksSliceManagedRound] round=5/5 order=UE->PF->TR ue=8.837ms pf=16.683ms tr=9.047ms sumOk=True
LogUnrealCSharp: [UETasksSliceManagedSummary] length=10000 taskCount=4 iterations=500 warmup=5 rounds=5 ueAvg=9.181ms pfAvg=16.994ms trAvg=6.234ms ratioPf=1.851 ratioTr=0.679
LogUnrealCSharp: [UETasksSliceNativeRound] round=1/5 order=UE->PF->TR ue=8.828ms pf=16.842ms tr=6.301ms sumOk=True
LogUnrealCSharp: [UETasksSliceNativeRound] round=2/5 order=UE->PF->TR ue=8.453ms pf=16.279ms tr=5.280ms sumOk=True
LogUnrealCSharp: [UETasksSliceNativeRound] round=3/5 order=UE->PF->TR ue=9.308ms pf=16.541ms tr=5.334ms sumOk=True
LogUnrealCSharp: [UETasksSliceNativeRound] round=4/5 order=UE->PF->TR ue=8.788ms pf=16.924ms tr=7.893ms sumOk=True
LogUnrealCSharp: [UETasksSliceNativeRound] round=5/5 order=UE->PF->TR ue=8.557ms pf=16.673ms tr=5.696ms sumOk=True
LogUnrealCSharp: [UETasksSliceNativeSummary] length=10000 taskCount=4 iterations=500 warmup=5 rounds=5 ueAvg=8.787ms pfAvg=16.652ms trAvg=6.101ms ratioPf=1.895 ratioTr=0.694

LogUnrealCSharp: [UETasksSliceManagedRound] round=1/5 order=UE->PF->TR ue=8.631ms pf=11.754ms tr=10.366ms sumOk=True
LogUnrealCSharp: [UETasksSliceManagedRound] round=2/5 order=UE->PF->TR ue=11.132ms pf=11.245ms tr=9.508ms sumOk=True
LogUnrealCSharp: [UETasksSliceManagedRound] round=3/5 order=UE->PF->TR ue=10.467ms pf=11.035ms tr=10.973ms sumOk=True
LogUnrealCSharp: [UETasksSliceManagedRound] round=4/5 order=UE->PF->TR ue=8.277ms pf=11.645ms tr=8.350ms sumOk=True
LogUnrealCSharp: [UETasksSliceManagedRound] round=5/5 order=UE->PF->TR ue=7.795ms pf=11.259ms tr=7.261ms sumOk=True
LogUnrealCSharp: [UETasksSliceManagedSummary] length=10000 taskCount=2 iterations=500 warmup=5 rounds=5 ueAvg=9.260ms pfAvg=11.387ms trAvg=9.292ms ratioPf=1.230 ratioTr=1.003
LogUnrealCSharp: [UETasksSliceNativeRound] round=1/5 order=UE->PF->TR ue=7.352ms pf=11.302ms tr=6.991ms sumOk=True
LogUnrealCSharp: [UETasksSliceNativeRound] round=2/5 order=UE->PF->TR ue=7.740ms pf=11.306ms tr=7.416ms sumOk=True
LogUnrealCSharp: [UETasksSliceNativeRound] round=3/5 order=UE->PF->TR ue=7.663ms pf=11.783ms tr=7.904ms sumOk=True
LogUnrealCSharp: [UETasksSliceNativeRound] round=4/5 order=UE->PF->TR ue=7.604ms pf=11.393ms tr=9.495ms sumOk=True
LogUnrealCSharp: [UETasksSliceNativeRound] round=5/5 order=UE->PF->TR ue=7.932ms pf=11.398ms tr=8.961ms sumOk=True
LogUnrealCSharp: [UETasksSliceNativeSummary] length=10000 taskCount=2 iterations=500 warmup=5 rounds=5 ueAvg=7.658ms pfAvg=11.436ms trAvg=8.153ms ratioPf=1.493 ratioTr=1.065

LogUnrealCSharp: [UETasksSliceManagedRound] round=1/5 order=UE->PF->TR ue=36.370ms pf=37.046ms tr=40.988ms sumOk=True
LogUnrealCSharp: [UETasksSliceManagedRound] round=2/5 order=UE->PF->TR ue=28.403ms pf=30.923ms tr=39.128ms sumOk=True
LogUnrealCSharp: [UETasksSliceManagedRound] round=3/5 order=UE->PF->TR ue=30.942ms pf=32.369ms tr=39.021ms sumOk=True
LogUnrealCSharp: [UETasksSliceManagedRound] round=4/5 order=UE->PF->TR ue=30.737ms pf=31.598ms tr=38.686ms sumOk=True
LogUnrealCSharp: [UETasksSliceManagedRound] round=5/5 order=UE->PF->TR ue=29.334ms pf=33.929ms tr=39.246ms sumOk=True
LogUnrealCSharp: [UETasksSliceManagedSummary] length=200000 taskCount=6 iterations=500 warmup=5 rounds=5 ueAvg=31.157ms pfAvg=33.173ms trAvg=39.414ms ratioPf=1.065 ratioTr=1.265
LogUnrealCSharp: [UETasksSliceNativeRound] round=1/5 order=UE->PF->TR ue=26.220ms pf=31.694ms tr=37.267ms sumOk=True
LogUnrealCSharp: [UETasksSliceNativeRound] round=2/5 order=UE->PF->TR ue=26.716ms pf=32.526ms tr=31.397ms sumOk=True
LogUnrealCSharp: [UETasksSliceNativeRound] round=3/5 order=UE->PF->TR ue=26.951ms pf=32.457ms tr=37.264ms sumOk=True
LogUnrealCSharp: [UETasksSliceNativeRound] round=4/5 order=UE->PF->TR ue=26.866ms pf=33.787ms tr=37.590ms sumOk=True
LogUnrealCSharp: [UETasksSliceNativeRound] round=5/5 order=UE->PF->TR ue=24.808ms pf=35.020ms tr=38.884ms sumOk=True
LogUnrealCSharp: [UETasksSliceNativeSummary] length=200000 taskCount=6 iterations=500 warmup=5 rounds=5 ueAvg=26.312ms pfAvg=33.097ms trAvg=36.480ms ratioPf=1.258 ratioTr=1.386
```
