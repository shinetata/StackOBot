# PGD 对标 Unity ECS：Managed/Unmanaged 双存储设计——改动点与可选方案清单

> 目标：回答“PGD 如果对标 Unity ECS（DOTS Entities），为了同时支持 **unmanaged（可走 native kernel）** 与 **managed（不可走 native kernel）** 两类组件数据，需要改哪些地方、每个点有哪些方案、各自代价是什么？”。
>
> 本文只做设计与改动清单，不直接改代码。

---

## 0. 结论先行（最重要的判断）

1. **Unity ECS 确实采用了“至少两种存储结构”**：Chunk 内存中存放 unmanaged 组件的真实数据；managed 组件不直接存在 chunk 中，而是在 `World` 级的 `ManagedComponentStore` 里集中存储，chunk 里只存 **int 索引**。
2. **“两种底层存储结构并存”不是问题本身**；问题在于：
   - 是否能在 API/调度层做到“硬隔离”：并行/Job 路径永远不触达 managed 数据。
   - 是否能在热点循环做到“无分支/少分支”：不要在每个元素上判断用哪条存储路径，而是在 query/系统调度时选好路径。
   - 是否能定义清晰的生命周期与结构变更规则：native 任务运行时不得扩容/搬迁/释放底层内存（或必须有 fence + 延迟释放）。
3. PGD 若要“对标 Unity ECS”，可以分成 3 个对标等级（越往后改动越大）：
   - **L0（最小可用）**：仅支持 `unmanaged struct` 组件走 NativeBuffer/native kernel；其余组件继续走 `T[]`（或直接禁止非 unmanaged 组件）。这是“验证路线可行”的最低成本版本。
   - **L1（Unity 式双存储，推荐作为长期架构）**：引入 `ManagedComponentStore` + chunk 内 `int` 索引数组，使 managed 组件（包括 `class`）与 unmanaged 组件在同一 archetype/chunk 框架下并存。
   - **L2（更深度对标 DOTS）**：补齐 TypeHandle/版本系统/Safety/依赖图（JobHandle）/work stealing 等。这会显著扩大工程量，且不一定与 UE TaskGraph 的目标一致。

---

## 1. Unity ECS 的“managed/unmanaged 双存储”到底是什么（交叉验证）

### 1.0 本地源码包路径（本次分析依据）

- Unity Entities 源码包（本地）：`C:\WorkSpace\GitHub\PGDCS\Test\Entities101\Library\PackageCache\com.unity.entities@1.3.2`
  - 主要源码：`...\Unity.Entities\`
  - 随包文档：`...\Documentation~\`

### 1.1 随包文档（公开资料）明确描述

在 `com.unity.entities@1.3.2/Documentation~` 中：

- `Documentation~/components-managed.md` 明确说明：
  - managed component **不能在 jobs/Burst 中访问**；
  - managed component **不直接存储在 chunk**；
  - Unity 在 `World` 级维护一个“大数组”，chunk 存的是该数组的索引（因此访问多一次 indirection，性能更差）。

### 1.2 源码实现（内部机制）与文档一致

- `Unity.Entities/ManagedComponentStore.cs`
  - `ManagedComponentStore` 持有 `object[] m_ManagedComponentData` 作为 managed 数据存储。
- `Unity.Entities/ChunkDataUtility.cs`
  - 复制/迁移 chunk 时，对 managed component 的处理读取/写入的是 `int`（索引），而不是对象本体。
- `Unity.Entities/Iterators/ArchetypeChunkArray.cs`
  - `ManagedComponentAccessor<T>` 内部就是 `NativeArray<int> (index array) + ManagedComponentStore.object[]` 的组合。

### 1.3 Unity 的核心“冲突解决策略”

用一句话总结：**在数据层允许并存，但在并行执行层严格限制**。

```
                 +-------------------------------+
                 | Chunk (per archetype chunk)   |
                 |-------------------------------|
  unmanaged data | Position[] Velocity[] ...     |  <-- 连续/可指针化
                 |-------------------------------|
  managed index  | RenderMeshIndex[] ...         |  <-- int 索引数组
                 +-------------------------------+
                                 |
                                 v
                 +-------------------------------+
                 | ManagedComponentStore (World) |
                 | object[] m_ManagedComponentData|
                 +-------------------------------+
```

并行/Job/Burst 路径只对 **unmanaged 连续内存**开放；managed 组件必须用单独 API/单独运行方式（通常 `.Run`）访问。

---

## 2. PGD 当前形态（与 Unity 对比的“基线”）

> 以你给出的 PGDCS 代码为样本（PGD_Core）。

### 2.1 组件类型约束

- PGD 当前大量 API 使用 `where T : struct, IComponent`（示例：`PGD_Core/src/Entity/IEntity.cs`）。
- `struct` **并不等价于 unmanaged**（struct 可以包含引用类型字段），这意味着：
  - 你可以写出“看起来是值类型，但本质上是 managed payload”的组件；
  - 这种组件无法安全解释为 `T*` 交给 native kernel 做并行写入。

### 2.2 Archetype 存储与遍历形态

- `PGD_Core/src/Base/Archetype/PgdArray.generic.cs`：
  - `PgdArray<T>` 使用 `T[] components` 存储组件。
- `PGD_Core/src/Query/Generated/ArchetypeChunk.g2.cs`：
  - `ArchetypeChunk<T1,T2>` 本质是对 `T1[]/T2[]/entityIds[]` 的切片（Span），并不区分 managed/unmanaged。
- `PGD_Core/src/Query/Generated/IQuery.g2.cs`：
  - `ParallelForEach` 把 chunk 打包为 `ParallelTask`，由 `ParallelRunner` 固定线程池执行。

这套设计对“纯 C# 并行”成立；但要把底层换成 NativeBuffer 并交给 UE TaskGraph/native kernel，需要新增一整条“unmanaged 专用路径”。

---

## 3. 改动点总览（按模块拆分）

下面每一项都给出：**为什么必须改**、**可选方案**、**代价/风险**。你们可以按 L0/L1/L2 对标等级裁剪。

### 3.1 类型系统与元数据（必须）

**为什么必须改**
- native kernel 需要 `T*` + `stride` + `length`；必须能在注册阶段确认“这个组件能否被指针化、能否跨线程写、是否需要析构/深拷贝”。

**方案**
- 方案 A（L0 推荐）：在组件注册/Metadata 阶段为每个组件类型记录 `ComponentCategory`：
  - `Unmanaged`：满足 `unmanaged`（或 runtime blittable 检查通过）
  - `Managed`：其它情况（包括含引用字段的 struct）
  - （可选）`ManagedClass`：若未来要支持 `class` 组件
- 方案 B（更强硬）：把对外 API 统一改成 `where T : unmanaged, IComponent`，彻底禁止 managed 组件进入 ECS。

**代价/风险**
- A：保留兼容性，但复杂度上升（后续会出现“双存储/双路径”）。
- B：简单、性能最干净，但会破坏现有允许“非 unmanaged struct”的用法（如果团队已有此类组件）。

### 3.2 组件容器（PgdArray）双后端（必须）

**为什么必须改**
- 你要用 NativeBuffer 替换 `T[]` 才能把底层数据交给 UE TaskGraph/native kernel。
- 但只有 unmanaged 组件能走 NativeBuffer；其余组件必须留在托管世界（或被禁止）。

**方案**
- 方案 A（L0/L1）：把 `PgdArray<T>` 拆成两个实现（避免热点分支）：
  - `PgdArrayManaged<T>`：内部 `T[]`
  - `PgdArrayNative<T>`：内部 `NativeBuffer<T>`（或等价的 unmanaged memory owner）
  - `PgdArray` 抽象基类维持现有生命周期接口：`Move/Copy/Rearrange/Default/Clear...`
- 方案 B（不推荐）：单个 `PgdArray<T>` 内部做 union（`T[]` + `NativeBuffer<T>` + flag），每次访问都 if/else。

**代价/风险**
- A：类数量增加，但热点路径可以保持更“直线化”（对性能更友好）。
- B：实现简单，但容易把 if/else 带进最内层循环，压缩掉你从 native kernel 得来的收益。

### 3.3 Archetype 扩容/搬迁/删除的内存语义（必须）

**为什么必须改**
- 当前 `PgdArray<T>.RearrangeComponents` 是分配新 `T[]` 并复制 Span。
- 对 `NativeBuffer<T>`，扩容意味着“重新分配 + memcpy + swap”，并且可能与并行任务冲突。

**方案**
- 方案 A（L0 推荐）：仍保持“archetype 一块可扩容数组”的思路，但加 fence：
  - **native 并行任务运行时禁止扩容与结构变更**（与 Unity 一致：结构变更必须在安全点）。
  - 扩容只能发生在“没有任何 native job/TaskGraph 任务在跑”的时刻。
- 方案 B（更接近 Unity）：引入固定容量 `Chunk`（比如 16KB/32KB），archetype 由多个 chunk 组成，减少扩容频率与搬迁成本。

**代价/风险**
- A：改动较小，但仍可能在“实体爆发式增长”时频繁 realloc。
- B：会牵扯 Query/EntityMove/Lookup/Prefab/CommandQueue 等大量逻辑重写，是 L2 级别工程量。

### 3.4 Query/代码生成（必须）

**为什么必须改**
- 现有生成代码（如 `IQuery.g*.cs`）默认认为底层是 `T[]`，并用 `Span<T>` 访问。
- 对 native kernel 路径，你需要额外信息：`void* ptr`、`length`、多组件指针等；并且要在生成时选对路径，避免每元素分支。

**方案**
- 方案 A（L0 推荐）：对每个 Query 生成两套入口：
  - `ForEachEntity` / `ParallelForEach`：继续走纯 C#（Span）路径（兼容旧逻辑）
  - `ParallelForEachNativeKernel`（或 `ScheduleTaskGraph`）：仅在 **T1..Tn 全部 Unmanaged** 时可用，否则直接抛异常或回退到 managed path（由调用者显式决定）
- 方案 B（L1 体验更好）：生成时根据组件类型自动选择：
  - 全 unmanaged：走 native kernel
  - 含 managed：走 managed 线程池/单线程
  - 注意：不要在最内层循环里判别；在 query 初始化/获取 chunk 时就确定执行器。

**代价/风险**
- A：调用点更清晰，不会“默默走错路径”导致误导性能结论。
- B：用户体验好，但必须非常谨慎：自动策略一旦不稳定，容易出现“默认更慢/难排查”。

### 3.5 ManagedComponentStore（可选，但这是“对标 Unity”的关键）

**为什么要做**
- 如果你希望 PGD 支持真正意义上的 managed component（`class` 组件，或含引用字段的 struct 组件），同时仍保持 chunk 化的遍历结构，那么 Unity 的解法是最成熟的：
  - chunk 存 int 索引
  - world 存 object[]

**方案**
- 方案 A（L1 对标 Unity）：引入 `ManagedComponentStore`
  - `object[]` 存组件对象
  - archetype/chunk 内为每个 managed 组件存 `int[]` 索引
  - 提供 `ManagedComponentAccessor<T>`（读写时通过 store 更新，并维护 clone/dispose hooks）
- 方案 B（更克制）：不引入 class 组件；只把“非 unmanaged struct”当成 managed，但仍存在 `T[]` 中；并明确规定这类组件永远不进入 native kernel。

**代价/风险**
- A：工程量明显增大（复制/克隆/销毁/序列化/比较/调试都要考虑），但架构清晰、路线与 Unity 一致。
- B：工程量小，但“managed struct”仍在 chunk 内，会有 GC 引用，容易误用并行（必须靠约束防止）。

### 3.6 并行调度与依赖/Barrier（必须，且要与 UE TaskGraph 对齐）

**为什么必须改**
- 你们的目标不是“在 C# 里跑并行”，而是“让 UE TaskGraph 调度 native kernel”。
- 这意味着：PGD 层必须输出可被 TaskGraph 消费的数据切片（指针 + 长度 + 多组件指针）。

**方案**
- 方案 A（L0 推荐）：保留 PGD 的 `ParallelRunner` 作为 managed 路径；新增 UE TaskGraph 专用执行器：
  - `NativeKernelScheduler`：把 chunk/section 切分结果打包给 C++ internal call，内部使用 TaskGraph 并行执行
  - 明确要求：worker 不进入 Mono（与你们现有 benchmark 的前提一致）
- 方案 B（更深度对标 DOTS）：自研 JobHandle 依赖图、work stealing、读写冲突检测（L2）。

**代价/风险**
- A：与当前“native kernel vs managed parallel”验证方案一致，风险可控。
- B：工程量巨大，且在 UE 内可能与 TaskGraph 的既有机制重复建设。

### 3.7 生命周期与结构变更规则（必须）

**为什么必须改**
- 有两套存储后，最容易出错的是：任务执行期间发生结构变更（增删实体、换 archetype、扩容）导致指针悬空或数据竞态。

**方案（建议直接对齐 Unity 的约束）**
- 查询/并行期间禁止结构变更（你们 PGD 已有 `runningQueriesCount` 的锁思路，可以扩展为“native job 运行期计数/fence”）。
- CommandQueue/ECB 类似机制用于延迟结构变更，统一在安全点 playback。

### 3.8 拷贝/克隆/销毁语义（必须，尤其在引入 ManagedStore 时）

**为什么必须改**
- PGD 现有 `CopyEntityComponentTo` 已经考虑了 deep clone（`RuntimeCloner.CreateDeepCloner<T>()`）和 CopyMethod。
- 一旦引入 managed store/索引数组，你需要明确：
  - 复制实体时 managed component 是共享引用？深拷贝？是否要求 `ICloneable`？
  - 销毁实体时是否调用 `IDisposable`？

**方案**
- 方案 A（对齐 Unity 文档建议）：managed component 鼓励实现 `ICloneable/IDisposable`；框架提供默认策略（浅拷贝 + 不 dispose），并允许注册钩子覆盖。
- 方案 B（收敛范围）：managed component 不支持 entity clone（或 clone 时直接共享引用并明确告警），先把目标聚焦到 unmanaged/native kernel。

---

## 4. 推荐的落地路线（按阶段交付、可验证、可回滚）

### P0：把“unmanaged 与非 unmanaged”定义清楚（1~2 天）
- 在组件注册/Metadata 中加入 `IsUnmanaged` 标记（编译期 or runtime 检测）。
- 在所有需要“可指针化”的入口处加硬检查：不满足直接报错，避免误导性能结论。

### P1：PgdArray 双后端（NativeBuffer vs T[]）（3~7 天）
- 新增 `PgdArrayNative<T>`（仅 `where T : unmanaged`）。
- 先只替换 1~2 个核心组件（比如 Position/Velocity），跑通“同一 archetype 里存在 native 与 managed 的混合存储”。
- 明确 structural change fence：native job 运行期禁止扩容与搬迁。

### P2：Query 生成支持 native kernel 入口（3~7 天）
- 生成 `ParallelForEachNativeKernel`（仅 unmanaged 组合）。
- 在 UE 侧用 TaskGraph internal call 执行核心循环（与你们现有 StackOBot 测试基准一致）。

### P3：决定是否做 ManagedComponentStore（按产品需求）
- 若确实需要 class 组件/复杂托管字段：按 Unity 模式加 store + index array。
- 若目标只是 ECS 纯数据计算：直接禁止 managed component 进入 ECS，体系最简单、性能最稳定。

---

## 5. 这是否意味着“PGD 内部两种存储结构一定会有问题”？

不必然。

Unity 的实践表明：**可以并存且可维护**，前提是你们必须在设计上做到：

1. **类型分层清晰**：哪些组件允许 native kernel，哪些永远不允许。
2. **API 分层清晰**：并行入口（TaskGraph/native kernel）只接受 unmanaged；managed 入口明确标识并限制调度方式。
3. **生命周期规则可审计**：任务运行时的禁止事项（扩容/搬迁/结构变更）必须是“硬错误”，不能靠约定。
4. **不要在最内层循环里做路径选择**：路径选择应发生在 query 构建/获取 chunk 阶段，否则收益会被分支与抽象抹平。

---

## 6. 附：你们当前“NativeBuffer 替换 PgdArray”的语境如何映射到 Unity 的术语

| 你们的目标表述 | Unity ECS 术语/机制 | PGD 建议做法 |
| --- | --- | --- |
| “native kernel 只处理 ECS 数据，不碰 UE 对象” | Jobs/Burst 只处理 unmanaged chunk 数据 | native kernel API 只接受 unmanaged 组件 |
| “替换托管数组为 NativeBuffer” | chunk 内存（unmanaged） | `PgdArrayNative<T>` + fence |
| “managed 组件保留旧路径” | ManagedComponentStore + chunk index array | L1 做 store；或 L0/P1 先直接保留 `T[]` 但禁止进入 native kernel |
| “两种底层存储结构会不会有问题” | DOTS 明确就是两套 | 关键在隔离与调度规则，而非“是否两套” |
