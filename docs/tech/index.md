# docs/tech 文档索引

> 目的：集中管理本仓库的技术文档，避免 `docs/tech/` 根目录堆积；并在索引中尽量标注“最后修改日志”（可从 `task_record/index.md` 回溯则填写，否则留空）。

## 目录结构

- `docs/tech/unrealcsharp/`：UnrealCSharp 运行时、线程模型、与 UE 交互等
- `docs/tech/taskgraph/`：UE TaskGraph 基准、性能验证等
- `docs/tech/pgd/`：PGD 与 UE TaskGraph/NativeKernel 的对标、路线评估等
- `docs/tech/codecheck/`：代码检视与审计类文档
- `docs/tech/image/`：文档引用的图片资源

## 索引（按目录分组）

| 分类 | 标题 | 路径 | 最后修改日志（尽量回溯 task_record） |
| --- | --- | --- | --- |
| `meta` | docs/tech 文档索引 | `docs/tech/index.md` | 2026-01-19 16:04:27 - docs/tech 文档索引与目录归档（按主题子目录整理） `task_record/archive/2026-W04/code_change_task_20260119_160427.md` |
| `codecheck` | TaskGraphVsCSharpPerfRunner 代码检视报告 | `docs/tech/codecheck/taskgraph-vs-csharp-perf-runner-review.md` | 2026-01-19 14:30:00 - TaskGraphVsCSharpPerfRunner 代码检视 `task_record/code_change_task_20260119_143000.md` |
| `image` | TaskGraph分配C#任务至多线程.png | `docs/tech/image/TaskGraph分配C#任务至多线程.png` |  |
| `pgd` | PGD 对标 Unity ECS：Managed/Unmanaged 双存储设计——改动点与可选方案清单 | `docs/tech/pgd/pgd-align-unity-ecs-managed-unmanaged-storage.md` | 2026-01-19 15:29:13 - PGD 对标 Unity ECS：managed/unmanaged 双存储改动点与方案清单 `task_record/archive/2026-W04/code_change_task_20260119_152913.md` |
| `pgd` | PGD_Core ParallelJob × UE TaskGraph：并行模型对标与迁移落地建议（面向 UnrealCSharp） | `docs/tech/pgd/pgd-paralleljob-to-ue-taskgraph-mapping.md` | 2026-01-13 17:19:27 - 基于 PGD 单测/源码审视 TaskGraph 设计（极致性能优先） `task_record/code_change_task_20260113_171844.md` |
| `pgd` | 路线 B 评估：PGD 数据布局与“TaskGraph 只跑 native kernel”的高性能落地方案 | `docs/tech/pgd/pgd-route-b-native-kernel-assessment.md` | 2026-01-16 11:44:18 - 路线B3补充：并行粒度归属（PGD vs UE） `task_record/archive/2026-W03/code_change_task_20260116_114418.md` |
| `pgd` | PGD x UE TaskGraph x UnrealCSharp：TaskGraph 并行能力构建过程记录与问题复盘（面向负责人） | `docs/tech/pgd/pgd-taskgraph-unrealcsharp-exec-summary-for-leads.md` | 2026-01-16 10:02:53 - 在负责人复盘文档中补齐路线B3（native-backed）方案 `task_record/archive/2026-W03/code_change_task_20260116_100253.md` |
| `pgd` | PGD 并行对比：TaskGraph native kernel vs C# 高性能 Task（同一任务） | `docs/tech/pgd/pgd-taskgraph-vs-csharp-task-performance.md` | 2026-01-19 09:08:47 - TaskGraph vs C# Task 性能对比方案与代码示例 `task_record/archive/2026-W04/code_change_task_20260119_090847.md` |
| `pgd` | PGD 结合 UE TaskGraph 的 Native Kernel 方案设计与测试验证 | `docs/tech/pgd/pgd-taskgraph-native-kernel-solution.md` | 2026-01-19 16:55:33 - Unity ECS 双存储说明 + PGD 改动模块清单细化 `task_record/archive/2026-W04/code_change_task_20260119_165533.md` |
| `taskgraph` | TaskGraph 性能收益验证：方案与结果汇总 | `docs/tech/taskgraph/taskgraph-performance-verification-summary.md` | 2026-01-21 14:22:54 - 补充 UE::Tasks 托管用例实测结果 `task_record/code_change_task_20260121_142258.md` |
| `taskgraph` | UE TaskGraph 并行后端的最小性能评估方案（对齐 PGD 单测风格） | `docs/tech/taskgraph/ue-taskgraph-benchmarking-for-pgd.md` | 2026-01-13 18:50:41 - 精简 TaskGraph 性能评估方案为“总时长对比”（对齐 PGD 单测） `task_record/code_change_task_20260113_185033.md` |
| `unrealcsharp` | UnrealCSharp 最小可用闭环：`NativeBuffer<T>`（native-backed）+ internal call（C#→UE C++ 同进程遍历处理） | `docs/tech/unrealcsharp/unrealcsharp-nativebuffer-internalcall-minimal-loop.md` | 2026-01-16 10:45:53 - 补充“不缓存 Span<T>”解释与典型反例 `task_record/archive/2026-W03/code_change_task_20260116_104553.md` |
| `unrealcsharp` | UnrealCSharp × UE5：从项目启动到 C# 执行的完整运行时流程（代码级） | `docs/tech/unrealcsharp/unrealcsharp-runtime-execution-flow.md` | 2026-01-12 15:21:23 - 按 ASCII 规范优化运行时执行流程图 `task_record/code_change_task_20260112_152123.md` |
| `unrealcsharp` | UnrealCSharp × UE5：运行时线程模型与线程边界（C#-First 团队必读） | `docs/tech/unrealcsharp/unrealcsharp-runtime-threading-model.md` | 2026-01-12 12:41:59 - 新增 UnrealCSharp 运行时线程模型文档 `task_record/code_change_task_20260112_124159.md` |
| `unrealcsharp` | UnrealCSharp × UE TaskGraph：用 TaskGraph 并行执行“纯 C# 计算”的可行性与设计要点（讨论沉淀） | `docs/tech/unrealcsharp/unrealcsharp-taskgraph-parallel-csharp.md` | 2026-01-13 16:40:09 - 将非阻塞式 TaskGraph 方案沉淀到 PGD 对标文档 + 补齐本机路径 `task_record/code_change_task_20260113_163945.md` |
| `unrealcsharp` | UnrealCSharp × UE5：TaskGraph worker 执行 C# 时，二次 PIE 卡死（LoadFromStream/Game.dll）的定位思路与方案审视 | `docs/tech/unrealcsharp/unrealcsharp-taskgraph-worker-mono-attach-pie-freeze.md` | 2026-01-15 17:17:13 - 补充“线程状态不收敛/stop-the-world”ASCII 解释图 `task_record/archive/2026-W03/code_change_task_20260115_171713.md` |
| `unrealcsharp` | UnrealCSharp × UE TaskGraph：借鉴 Unity “scripting jobs”经验，缓解/解决 PIE 卡死（Managed 任务跑在 worker 上） | `docs/tech/unrealcsharp/unrealcsharp-taskgraph-worker-scripting-jobs-fence.md` | 2026-01-21 11:00:49 - 展开路线 B 的引擎改造方案细节 `task_record/code_change_task_20260121_110055.md` |
| `unrealcsharp` | UE TaskGraph 执行 C# 逻辑的探索总结（排除 Native Kernel） | `docs/tech/unrealcsharp/unrealcsharp-taskgraph-worker-managed-exploration-summary.md` | 2026-01-21 11:12:23 - 任务链路、PIE 安全机制与路线 A/B 总结 `task_record/code_change_task_20260121_111230.md` |
| `unrealcsharp` | UE::Tasks 托管执行测试方案（面向小白） | `docs/tech/unrealcsharp/ue-tasks-managed-test-plan.md` | 2026-01-21 17:42:31 - 新增 NativeBuffer 对比版本并改为取平均值 `task_record/code_change_task_20260121_174231.md` |
| `unrealcsharp` | UE::Tasks（方案A：Slice Thunk）设计说明与实测结果（含 Managed Pinned / NativeBuffer） | `docs/tech/unrealcsharp/ue-tasks-slice-batch-design-and-results.md` | 2026-01-21 19:57:00 - 补齐 UE::Tasks 方案A 文档（设计+流程+测试结果） `task_record/code_change_task_20260121_195615.md` |
| `unrealcsharp` | UnrealCSharp × Unreal Engine：交互机制与新手上手指南（以本仓库为例） | `docs/tech/unrealcsharp/unrealcsharp-ue-interaction-beginner-guide.md` | 2026-01-13 09:59:25 - 补充 C# 输出到 UE Output Log 的三种方式 `task_record/code_change_task_20260113_095925.md` |

## 维护规则（简版）

- 新增/移动/重命名 `docs/tech/**` 下文档时：必须同步更新本索引。
- “最后修改日志”优先填写 `task_record/index.md` 中最近一条涉及该文档的记录；若无法回溯可留空。
- 若本次工作修改了仓库文件（含文档）：仍遵循 `AGENTS.md` 的任务记录要求，补齐 `task_record/` 记录与索引。
