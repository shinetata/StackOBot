# UE TaskGraph 并行后端的最小性能评估方案（对齐 PGD 单测风格）

**TL;DR**：先别做复杂基准体系。用一个“PGD 单测同款”的做法：**准备固定数据**，然后**连续执行几十次遍历**（例如 32/100 次），用 `Stopwatch` 统计**总耗时**。每次改动后跑同一套参数，对比总时长即可。

参考用法入口（PGD 单测）：`C:\WorkSpace\GitHub\PGDCS\Test\PGDCore\Core\ParallelJob\ParallelJobComparisonTests.cs`

---

## 1) 评估目标（只回答一个问题）

同等数据量、同等迭代次数下：

- 方案 A（旧实现）总耗时是多少？
- 方案 B（新实现）总耗时是多少？

你只关心“更快/更慢”，不做细分统计。

---

## 2) 最小评估步骤（每次改动都照做）

### 2.1 固定参数（写死）

- 数据规模：例如 `entityCount = 10000` 或 `len = 10000`
- query 数量：例如 5 个（对齐单测多系统）
- 迭代次数：例如 `iterations = 32`（或 100）
- 并行模式：
  - 阻塞：`ParallelForEach` / `ExecuteBatch(wait=true)`
  - 非阻塞：`ScheduleParallel` + `CompleteAll`（未来对齐 PGD）

### 2.2 先 warmup（少量即可）

只为避免首次 JIT/初始化的噪声。建议 `warmup = 3` 或 `5`，不计时。

### 2.3 计时：只统计总耗时

伪代码（对齐 PGD 单测的“多 query、重复迭代”形态）：

```
PrepareWorldOrData(entityCount)
PrepareQueries(queryCount)

// warmup
for i in 0..warmup:
  RunQueriesOnce()

// measure
sw = Stopwatch.StartNew()
for i in 0..iterations:
  RunQueriesOnce()
sw.Stop()

Console.WriteLine($"Total: {sw.Elapsed.TotalMilliseconds} ms")
```

其中 `RunQueriesOnce()` 的推荐形态：

```
// 阻塞版（对齐 ParallelForEach）
query0.ParallelForEach(...)
query1.ParallelForEach(...)
...

// 非阻塞版（对齐 ScheduleParallel + barrier）
h0 = query0.ScheduleParallel(...)
h1 = query1.ScheduleParallel(...)
...
CompleteAll(h0,h1,...)
```

---

## 3) 基准纪律（只保留最关键的 4 条）

为了让“总耗时对比”可信，至少做到：

1) 固定数据规模与迭代次数（entityCount/queryCount/iterations 不变）
2) warmup 后再计时（否则首次成本会污染）
3) 测量区间内不输出日志（不要在遍历 lambda 里打印）
4) 每次只改一个变量（例如只改“缓存查找”，别同时改 chunkSize/任务数）

---

## 4) 最低可用产出

只要能稳定输出一行总耗时，就足够支撑后续优化迭代：

`Total: 12.34 ms (iterations=32, entityCount=10000, queryCount=5)`

后续你要做的（缓存查找、thunk、池化）都能用同一套参数回归对比“总耗时变化”。
