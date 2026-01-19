# TaskGraphVsCSharpPerfRunner 代码检视报告

## 1. 检视背景

### 1.1 检视目标
检视 `TaskGraphVsCSharpPerfRunner.RunAddOneAndSumInt32ParallelCompare` 的对比方案设计，评估其合理性、潜在偏差及改进空间。

### 1.2 涉及文件

| 文件 | 路径 |
| --- | --- |
| 对比入口 | `Plugins/UnrealCSharp/Script/UE/Library/TaskGraphVsCSharpPerfRunner.cs` |
| TaskGraph 实现 | `Plugins/UnrealCSharp/Script/UE/Library/TaskGraphNativeKernelPerf.cs` |
| C# Parallel 实现 | `Plugins/UnrealCSharp/Script/UE/Library/CSharpParallelPerf.cs` |

### 1.3 对比设计意图
根据 `docs/tech/pgd/pgd-taskgraph-vs-csharp-task-performance.md`，对比目标是：
- **方案 A**：`TaskGraph + native kernel`（worker 不进 Mono）
- **方案 B**：`C# 高性能 Task`（`Parallel.For` / `Task.Run`）

即 **"TaskGraph native kernel" vs "C# managed parallel"** 的综合性能对比。

---

## 2. 检视过程

### 2.1 代码走读要点

| 维度 | 观察点 |
| --- | --- |
| 参数配置 | `length/taskCount/iterations/warmup/rounds` |
| 交替执行 | `r % 2 == 0` 切换执行顺序 |
| 预热机制 | `warmup` 次预热后进入计时 |
| 结果校验 | 每轮比对 `sumTaskGraph == sumCSharp` |
| 统计方式 | 多轮取中位数 `Median` |

### 2.2 核心实现对照

**TaskGraph 实现（`TaskGraphNativeKernelPerf.cs:39-67`）**

```csharp
using var buf = new NativeBuffer<int>(length: length);
var span = buf.AsSpan();
for (var i = 0; i < span.Length; i++) { span[i] = i; }

sum = FNativeBufferTaskGraphImplementation
    .FNativeBufferTaskGraph_AddOneAndSumInt32ParallelNoLogImplementation(
        buf.Ptr, buf.Length, safeTaskCount);
```

**C# Parallel 实现（`CSharpParallelPerf.cs:68-91`）**

```csharp
var data = (int*)buf.Ptr;  // 已改为指针直写，无 Span 边界检查
var chunkSize = (len + taskCount - 1) / taskCount;

Parallel.For(0, taskCount, options,
    () => 0L,
    (taskIndex, _, local) => { /* chunk work */ },
    local => Interlocked.Add(ref sum, local));
```

---

## 3. 团队回应与交叉验证

### 3.1 团队确认有效的设计

| 设计 | 验证结论 |
| --- | --- |
| 交错顺序 (`r % 2 == 0`) | ✅ 有效降噪手段 |
| 预热 (`warmup`) | ✅ 确保 JIT 编译完成 |
| 多轮中位数 (`Median`) | ✅ 减少单次波动 |
| sum 校验 | ✅ 验证计算等价 |

### 3.2 需要修正的点

| 报告原结论 | 实际情况 | 修正 |
| --- | --- | --- |
| "C# 用 Span 有边界检查开销" | `CSharpParallelPerf.cs:71` 已改为 `int*` 指针直写 | **删除此条** |

### 3.3 命名与定位（已澄清）

| 维度 | 说明 |
| --- | --- |
| 报告原批评 | "不是 TaskGraph vs C# Task" |
| 项目文档定义 | `docs/tech/pgd/pgd-taskgraph-vs-csharp-task-performance.md` 明确定义为 **"TaskGraph native kernel vs C# 高性能 Task"** |
| 结论 | 报告的批评在**语义层面成立**，但与**项目文档定位一致**。现有实现是正确的，但命名 `TaskGraphVsCSharpPerfRunner` 容易误导，建议明确为 **"native kernel vs managed parallel"**。 |

---

## 4. 检视结果

### 4.1 设计优点

| 设计 | 价值 |
| --- | --- |
| **交替执行** | 抵消冷启动偏差，公平对比 |
| **预热机制** | 确保 JIT 编译完成，结果稳定 |
| **多轮中位数** | 减少单次波动，更具统计意义 |
| **正确性校验** | 每轮比对 sum，验证计算等价 |
| **指针直写** | C# 实现已改为 `int*`，无 Span 边界检查开销 |

### 4.2 核心结论：对比的是"综合性能"而非"纯调度"

| 对比维度 | TaskGraph + native kernel | C# Parallel.For / Task.Run |
| --- | --- | --- |
| **调度体系** | UE TaskGraph（引擎内统一） | .NET ThreadPool（引擎外） |
| **计算路径** | C++ tight loop | C# 托管代码 + 指针访问 |
| **线程模型** | UE Game Thread Pool | .NET ThreadPool |

**结论**：当前对比结果是 **"native kernel + TaskGraph + UE调度"** 相对于 **"managed parallel + .NET ThreadPool"** 的**综合性能**。这与 `docs/tech/pgd/pgd-taskgraph-vs-csharp-task-performance.md` 的定位一致。

### 4.3 已知系统环境因素（不是逻辑错误）

| 因素 | 说明 | 影响 |
| --- | --- | --- |
| **数据复用与缓存效应** | warmup + iterations 复用同一 buffer | 偏向稳态吞吐，两条路径等价，非偏置 |
| **ThreadPool 动态爬坡** | .NET ThreadPool 根据负载动态调整 Worker 线程数 | cs=7~10ms 离群值来源，交替顺序+中位数已显著降低影响 |

---

## 5. 优化建议

### 5.1 命名优化（推荐）

将类名改为更准确的表述，避免误导：

| 当前名称 | 建议名称 |
| --- | --- |
| `TaskGraphVsCSharpPerfRunner` | `NativeKernelVsManagedParallelPerfRunner` |

### 5.2 可选增强

| 建议 | 复杂度 | 说明 |
| --- | --- | --- |
| 新增 C# Task.Run 对照组 | 低 | `CSharpParallelPerf.cs` 已包含 `RunAddOneAndSumInt32TaskRun`，可在 Runner 中调用 |
| 增强统计输出 | 低 | 添加 `StdDev` / `P90` / `P95` 百分位 |
| 分离调度与计算开销 | 中 | 多轮调用摊薄调度开销，分别报告 |

---

## 6. 结论

### 6.1 整体评价

| 维度 | 结论 |
| --- | --- |
| **方案正确性** | ✅ 与项目文档定义一致 |
| **降噪手段** | ✅ 有效（交错+预热+中位数） |
| **数据对称性** | ✅ 已用 `int*` 指针，无 Span 边界检查 |
| **命名清晰度** | ⚠️ 建议改为 "native kernel vs managed parallel" |

### 6.2 最终结论

当前对比方案**设计合理且与项目目标一致**：

1. **对比的是"综合性能"**：native kernel + TaskGraph + UE调度 vs managed parallel + .NET ThreadPool
2. **降噪手段有效**：交错顺序、预热、中位数显著降低了系统波动影响
3. **数据对称**：C# 实现已改为指针直写，无 Span 开销
4. **命名建议**：为避免误读为"纯调度器对比"，建议明确为 "native kernel vs managed parallel"

---

## 7. 参考

- 原始代码：`Plugins/UnrealCSharp/Script/UE/Library/TaskGraphVsCSharpPerfRunner.cs`
- TaskGraph 实现：`Plugins/UnrealCSharp/Script/UE/Library/TaskGraphNativeKernelPerf.cs`
- C# Parallel 实现：`Plugins/UnrealCSharp/Script/UE/Library/CSharpParallelPerf.cs`
- 项目定义文档：`docs/tech/pgd/pgd-taskgraph-vs-csharp-task-performance.md`
