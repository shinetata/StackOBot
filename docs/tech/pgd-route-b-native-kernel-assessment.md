# 路线 B 评估：PGD 数据布局与“TaskGraph 只跑 native kernel”的高性能落地方案

本文目标：在“高性能优先”的前提下，基于 PGD 源码对其 ECS 数据存储与遍历方式做一次定性/定量维度的评估，然后回答一个问题：

> 如果我们想让 UE TaskGraph worker **完全不进入托管运行时**（从而避开二次 PIE 卡死的 attach 路径风险），把计算放到 C++（native kernel）里执行，是否可行？代价是什么？有哪些方案能做到“尽量零拷贝”？

结论先说在前面：**在 PGD 当前“组件存储=托管数组 T[]”的前提下，路线 B 只有两种实现方式：要么复制（高概率性能灾难），要么 pin（稳定性/性能风险极高且难以规模化）**。如果路线 B 要成为长期主线，最可能的正确方向是：让 PGD 的热路径组件存储改为“native-backed 的连续内存”（C# 以 `Span<T>` 视图访问），从根上消除“跨语言拷贝/跨 GC”的性能与稳定性风险。

---

## 0) 参考源码与入口（本机路径）

PGD 仓库：`C:\WorkSpace\GitHub\PGDCS`

核心源码（本评估使用的关键文件）：

- Archetype 与组件存储：
  - `C:\WorkSpace\GitHub\PGDCS\PGD_Core\src\Base\Archetype\Archetype.cs`
  - `C:\WorkSpace\GitHub\PGDCS\PGD_Core\src\Base\Archetype\PgdArray.generic.cs`
- Query chunk 视图与遍历（生成代码）：
  - `C:\WorkSpace\GitHub\PGDCS\PGD_Core\src\Query\Generated\ArchetypeChunk.g1.cs`
  - `C:\WorkSpace\GitHub\PGDCS\PGD_Core\src\Query\Generated\IQuery.g1.cs`
  - `C:\WorkSpace\GitHub\PGDCS\PGD_Core\src\Query\Generated\ParallelTask.g1.cs`
- 并行切分策略（阈值/section）：
  - `C:\WorkSpace\GitHub\PGDCS\PGD_Core\src\Extensions\ParallelJob\ParallelManager.cs`

StackOBot 侧相关背景（路线 A 的二次 PIE 卡死）：

- `C:\WorkSpace\GitHub\StackOBot\docs\tech\unrealcsharp-taskgraph-worker-mono-attach-pie-freeze.md`

---

## 1) PGD 的数据存储：它为什么快（以及路线 B 不能破坏什么）

### 1.1 Archetype = “结构相同的一组实体” + “每种组件一个连续数组”

在 PGD 中，`Archetype` 持有：

- `entityIds: int[]`：实体 ID 连续存放
- `pgdArrays: PgdArray[]`：每种组件一个 `PgdArray<T>`
- `PgdArray<T>.components: T[]`：组件值以托管数组连续存放

对应源码：

- `Archetype.Components<TComponent>()` 直接把 `PgdArray<TComponent>.components` 以 `Span<TComponent>` 暴露出去（不拷贝）。
- `PgdArray<T>.components` 以 `new T[CapacityConfig.MinCapacity]` 初始化，并随容量扩容而整体替换为新数组（拷贝旧数据）。

这意味着：PGD 的“连续性”是 **每个组件类型维度的连续（SoA）**，而且是 **托管堆上的数组连续**。

### 1.2 Query 的“Chunk”并不是独立内存块，而是对 Archetype 数组的切片视图

生成代码中的 `ArchetypeChunk<T...>` 本质是一个 view：

- 内部保存 `T[] archetypeComponentsX`
- 内部保存 `int[] entityIds`
- 通过 `(start, length)` 表达切片
- 对外暴露 `Span<T>`：`new(archetypeComponentsX, start, Length)`

这件事很关键：所谓 “chunk” 并不是 Unity DOTS 那种固定大小的 chunk 内存块；它更像是 “Archetype 数组的连续区间视图”。

### 1.3 ParallelForEach 的切分策略：先按 archetype 分，再按 section 分（带阈值）

生成代码里有两种并行切法：

1) `ParallelForEach(lambda)`：按 chunk（即按 archetype）分配任务；每批凑满 `taskCount` 就执行一次。
2) `ParallelForEach(lambda, taskCount)`：对每个 chunk 再按 section 切分：
   - `ParallelManager.MIN_PARALLEL_CHUNK_SIZE = 10000`
   - `chunkLength < 10000` 时强制单线程（`taskCount = 1`）

这背后的性能含义是：PGD 明确在避免“任务过短导致调度开销吞掉并行收益”。

---

## 2) 路线 B 的“真实含义”：native kernel 要吃到什么数据？

路线 B 的口径是：“TaskGraph worker 不进入托管运行时，只跑 native kernel”。

要做到这一点，native kernel 必须能拿到：

- `length`：处理多少实体
- `entityIds`：`int*` 或 `int[]` 的连续视图
- 每个组件的连续 buffer：`T1* comps1, T2* comps2, ...`
- （可选）start 偏移：用于 section 切片

在 PGD 的现状下，这些数据都存在于 **托管数组** 里。

因此路线 B 立刻遇到一个不可回避的选择题：

```
托管数组 (T[])  ---->  native kernel (T*)

要么：复制（copy into native）
要么：固定（pin managed array）并借用指针
要么：从一开始就让组件存储在 native 内存（C# 只拿 Span 视图）
```

---

## 3) 路线 B 的三种实现方式（按“性能优先级”排序）

### 方案 B1：每次执行把 PGD 数据复制到 C++ buffer，再写回（不推荐）

做法（概念）：

1) C# 侧拿到 `Span<T>`（其实背后是 `T[]`）
2) 拷贝到 C++ 侧的 `T*` 连续 buffer
3) TaskGraph worker 在 C++ buffer 上并行计算
4) 把结果拷贝回 C# 的 `T[]`

性能风险（高优先级标注）：

- [高风险] **额外的 O(N) 内存带宽**：同一份数据至少多走 1~2 次 memcpy，通常会直接把并行收益吃光。
- [高风险] **双份内存占用**：组件越多，峰值内存越大；可能触发更多 GC 与 cache miss。
- [高风险] **破坏“数据在热路径只读/只写一次”的假设**：写回阶段还会引入额外 cache 污染。

结论：除非数据规模极小/或 kernel 极重（算力远大于内存带宽），否则 B1 在 ECS 遍历场景几乎必定性能劣化。

### 方案 B2：pin PGD 的托管数组，C++ 直接用指针读写（理论零拷贝，但风险极高）

做法（概念）：

1) C# 侧对每个需要的 `T[] components` 做 `GCHandle.Alloc(array, GCHandleType.Pinned)`
2) 把 `IntPtr` 传给 C++（或由 C++ 通过 internal call 回来拿）
3) C++ TaskGraph worker 在 pinned 指针上就地读写
4) 任务完成后释放 pin（`handle.Free()`）

性能/稳定性风险（必须明确）：

- [高风险] **pin/unpin 的固定开销**：每个 chunk、每个组件类型都要 pin；在多 archetype、多组件的 query 下会爆炸。
- [高风险] **GC 碎片化与停顿风险**：大量 pinned 对象会降低 GC 的整理效率，可能导致更长的停顿（尤其在 Editor 场景）。
- [高风险] **生命周期复杂**：TaskGraph 是异步并行，pin 必须覆盖整个 job 生命周期；任何漏释放都会造成长期 pin（性能与内存风险叠加）。
- [高风险] **类型约束**：如果组件不是纯 unmanaged/blittable（含引用、string、数组等），C++ 就算拿到指针也无法安全处理，更谈不上“高性能”。

额外注意：PGD 的 `IsSimpleLayoutType`帮助的是“C# 内的复制策略”，它把 `string` 也视为 “Continuous”，这对 C++ 来说并不等价于可安全按字节处理。路线 B 必须以“unmanaged/blittable”作为硬门槛。

结论：B2 可以作为“短期验证某个 very small 核心组件的 kernel”用，但不适合作为长期主线；规模化后性能/稳定性风险不可控。

### 方案 B3：让 PGD 的热路径组件存储从一开始就位于 native 连续内存（推荐方向，但代价是修改 PGD）

做法（概念）：

把 `PgdArray<T>.components: T[]` 改为类似：

- `void* data`（native buffer）
- `int capacity`
- C# 侧以 `Span<T>(data, length)` 暴露给遍历/生成代码
- 扩容用 `NativeMemory.Realloc` 等方式在 native 侧完成

下面把 B3 展开成“具体需要改哪里、数据怎么走、怎么避免踩坑”的可执行描述（不写实现细节代码，但做到能据此开工）。

#### B3.1 改动范围（你需要改哪些地方）

这条路线的改动主要发生在 **PGDCS/PGD_Core**（存储后端）+ **UE/UnrealCSharp 的 native kernel 调度层**（让 TaskGraph worker 跑 C++ 而不是跑 C#）。

**PGDCS（必改）：把组件存储改成 native-backed**

| 区域 | 参考文件（PGDCS） | 需要做什么 | 影响范围 |
| --- | --- | --- | --- |
| 组件数组容器 | `PGD_Core/src/Base/Archetype/PgdArray.generic.cs` | 用“native buffer + length/capacity”替代 `T[]`；提供 `Span<T>` 视图；实现扩容/释放/复制 | 所有组件读写路径都会触达 |
| Archetype 结构与结构变更 | `PGD_Core/src/Base/Archetype/Archetype.cs` | 创建/扩容时走新后端；`MoveEntityTo` 等结构变更需要能在 native buffer 间搬运 | 结构变更、创建实体、扩容等热/冷路径 |
| Chunk 视图 | `PGD_Core/src/Query/Generated/ArchetypeChunk.g1.cs` | `Span<T>` 的来源从 `new Span<T>(T[], ...)` 变为“存储后端返回的 Span 切片” | Query/ForEach/并行切分的遍历入口 |

**StackOBot/UnrealCSharp（配套改）：让 TaskGraph worker 只跑 native kernel**

| 区域 | 参考位置（StackOBot） | 需要做什么 | 影响范围 |
| --- | --- | --- | --- |
| TaskGraph 调度入口 | `Plugins/UnrealCSharp/Source/.../InternalCall/*` | 新增“执行 native kernel 批次”的 internal call（worker 侧不进入 Mono，不调用托管方法） | 路线 B 的调度后端 |
| C# 侧桥接层 | `Script/` 或 `Plugins/UnrealCSharp/Script/UE/*` | 定义 kernel 的调用形态（传递 buffer 指针/偏移/长度/stride 等）并返回完成信号 | 业务侧/PGD 扩展层 |

> 注意：B3 的“存储后端改造”是路线 B 的前置条件，但它本身并不等于“你已经能跑任意系统的 native kernel”。B3 解决的是“**数据在 native，C++ 可零拷贝吃到**”；kernel 仍需要单独设计/生成。

#### B3.2 数据结构：native-backed PgdArray 的最小形态（建议）

目标：对 PGD 的上层代码（Query/ForEach/生成代码）仍然暴露 `Span<T>`，但 `Span<T>` 背后不是 `T[]`，而是 unmanaged 连续内存。

推荐的最小字段（概念）：

- `nint Data`：指向 `T` 连续区的指针（byte address）
- `int Length`：当前有效长度（实体数）
- `int Capacity`：已分配容量（>= Length）
- `int Version`：每次 realloc/resize 增加，用于调试/断言“旧指针视图已失效”
- `bool IsNativeBacked`：是否启用 native 后端（只对 `unmanaged` 组件启用）

`Span<T>` 的暴露规则：

- 只在需要时创建临时 `Span<T>`（不要把 `Span<T>` 缓存在对象字段里）
- section 切片由 `Span<T>.Slice(start, length)` 完成

ASCII：同一份数据同时被 C#（Span）与 C++（pointer）消费

```
+-------------------+         +--------------------------+
| PgdArray<T>        |         | C++ kernel (TaskGraph)   |
| - Data: nint  -----+-------->+ - T* comps               |
| - Length/Capacity  |         | - start/len              |
| - Version          |         | - for(i) tight loop      |
+-------------------+         +--------------------------+
          |
          | Span<T> view (on demand)
          v
  C# Query/ForEach tight loop
```

#### B3.3 数据处理方式：一次系统更新里“数据怎么走”

把“结构变更/调度/读写”拆开讲清楚，避免隐含假设：

**A) 世界准备阶段（通常在 GameThread/主线程）**

1) 创建/扩容 Archetype 时，组件 buffer 在 native 侧分配好（一次性预留足够 capacity，尽量减少运行时 realloc）。
2) Entity 创建/销毁导致 `Length` 变化；新增区域用 `default(T)` 初始化（等价于清零）。

**B) 计算阶段（TaskGraph worker：只跑 C++ kernel）**

1) C#（或 PGD 扩展层）按 chunk/section 切分出任务批次：每个任务只有 `(archetypeId, start, len)` 这类索引信息。
2) 调用 internal call，把“要处理的 archetype + 组件 buffer 指针 + start/len”打包给 C++。
3) C++ 在 TaskGraph worker 上执行 kernel：只做 `for` 循环读写连续 buffer，不调用托管、不触碰 UE UObject。

**C) 收敛阶段（回到主线程/或显式 barrier）**

1) 等待 job 完成（阻塞或非阻塞 handle），之后 C#/UE 才能继续读写同一组组件（避免数据竞争）。

核心点：B3 让“B 阶段”完全不需要 pin，也不需要 `mono_thread_attach`；数据读写发生在 C++，直接写回同一份 native buffer，C# 看到的是同一份内存的视图。

#### B3.4 结构变更与扩容：必须定义的“禁止区”（不然会写出竞态）

只要存在下面任意一种行为，就必须在语义上建立 barrier（等价于“不要在 job 运行时做这些事”）：

- **realloc/resize**：扩容会移动 `Data` 指针；worker 里拿到的旧指针会立刻悬空
- **MoveEntityTo/结构变更搬迁**：会在 buffer 间 memcpy/move，和并行读写冲突
- **销毁 archetype/释放 buffer**：会让指针失效

因此 B3 在工程上通常需要一个很明确的规则（可以先从强约束开始）：

```
规则：当存在未完成的 native kernel job 时，
  - 禁止结构变更（create/destroy/move）
  - 禁止任何会触发扩容的写入路径
  - 允许的只有：读写“已固定容量内的组件数据”（且要约束读写集合）
```

这条规则听起来“像 Unity ECS”，但它是 B3 要成立的最低代价；否则你会在最底层 Debug 悬空指针与数据撕裂。

##### 这句话到底“像在哪里”：Unity ECS vs 方案 B3（把口径说准）

这句“像 Unity ECS”指的不是“我们要复刻 Unity 的全部实现”，而是指 **同一类工程约束**：当你把可并行执行的计算放到 worker 线程上跑时，你必须避免在 job 生命周期内做会让“数据指针/布局/归属”失效的操作（结构变更、扩容、释放）。

为了避免误解，这里把两者的“视图关系”用更准确的表述摊开：

**Unity ECS（DOTS Entities）的常见认知可以这样理解：**

- `NativeArray<T>` 等 NativeContainer 是 **C# 侧对 native 内存的视图**（典型就是 `T* + length + allocator + safety handle` 这一类要素）。
- Entities 的核心数据最终也在 native 内存上（chunk/组件连续存储），C# 侧通过这些容器/句柄拿到视图进行遍历与写入；配合 Job System/Burst，热路径的 C# job 可以被编译成机器码在 worker 上执行。
- 你说的“Unity C++ 底层提供存储结构，C# 只是视图”在工程直觉上**能帮助理解 NativeArray**，但要注意：Entities 的很多“ECS 语义”（chunk 组织、query、结构变更边界、依赖收敛）并不等价于“全在引擎 C++ 黑盒里”，它有大量可审计的 C# 包侧实现与约束体系。

**方案 B3（我们这里要达成的关系）应当这样描述：**

- 不是“UE C++ 要拿到 *C# 托管数组存储结构（`T[]`）* 的视图”，而是：**PGD 的组件存储从根上就改为 native-backed**，然后提供两类视图：
  - C#：用 `Span<T>` 作为遍历/生成代码的视图（背后是同一段 native buffer）
  - UE C++：拿到同一段 native buffer 的 `T* + start/len`，在 TaskGraph worker 上执行 native kernel（不进入 Mono）
- 所以它并不是“Unity 的反过来”，更像是：**我们让“数据”站在 native 这一侧，然后同时给 C# 和 C++ 两边提供视图**，只是两边的消费方式不同（C# 继续负责上层语义/切分；C++ 负责并行 kernel）。

**为什么这会自然导出“像 Unity ECS 的规则”：**

- Unity 的 Job/ECB 模型要求：job 运行期间不要做会改变 chunk 结构的事情，结构变更被延迟到 barrier（ECB playback / dependency 完成）再做。
- B3 也一样：只要你允许 worker 拿着 `T*` 运行，你就必须把 realloc/结构变更/释放定义为“需要 barrier 才能发生”的事件；否则就是悬空指针或数据竞争。

一句话总结两者共同点：

> **并行 job 期间数据必须“指针稳定 + 布局稳定 + 生命周期可控”，结构变更必须在明确的同步点发生。**

#### B3.5 类型约束：B3 能覆盖哪些组件（必须把门槛写死）

B3 建议只对 **unmanaged/blittable** 组件启用 native-backed：

- 允许：`int/float`、纯数学向量/矩阵、只含值类型字段的 `struct`
- 禁止：含 `string`、`object`、数组、`List<>`、任何引用字段的 `struct`

工程化做法（概念）：

- 在 PGD 侧用 `RuntimeHelpers.IsReferenceOrContainsReferences<T>()` 作为硬门槛
- 不满足门槛：强制走原来的 `T[]` 后端（也意味着“不能走路线 B 的 native kernel”，最多只能走 C# 执行）

另外还要把“布局一致性”写成硬约束：C++ kernel 只有在 **完全知道 `T` 的内存布局** 时才可能零拷贝读写（`sizeof(T)`、对齐、字段偏移一致）。工程上通常有两种做法：

- **手写对齐**：只挑少数稳定的数学组件（`Position/Velocity` 等），在 C++ 定义同构 struct，与 C# 保持 `LayoutKind.Sequential`/字段顺序一致。
- **生成布局信息**：由 C# source generator 导出每个组件的 `Size/Align/Offsets`（或生成一份 C++ header），让 C++ 侧能用同一份“事实”来解释 buffer。

这也是为什么 B3 最终会自然演化为“混合后端”（见 Phase 4）：不是一刀切。

#### B3.6 最小原型的验收标准（建议按这个拆任务）

为了避免一上来就改完整套 PGD，把原型切成可验证的小块：

1) **存储后端原型**（PGDCS）：选 1~2 个典型 unmanaged 组件（如 `Position/Velocity`），让它们走 native-backed；能跑通基本的 Create/Resize/ForEach 读写（不涉及结构变更）。
2) **指针可观测性**：在主线程打印 `Data` 指针与 `Version`；触发一次扩容，确认指针变化、旧视图不可复用。
3) **native kernel 原型**（StackOBot/UnrealCSharp）：写一个最小 C++ kernel（例如 `pos += vel * dt`），通过 internal call 把指针+start/len 交给 TaskGraph worker 执行，回到 C# 验证结果。
4) **同步点**：明确在同一帧里 native job 完成前不允许 C# 读写同一组件；用一个最小 handle/wait 先把语义锁死。

ASCII 示意：

```
            +----------------------+
            |     Archetype        |
            |  entityIds: int[]    |
            |  comps[T1]: native*  |----+
            |  comps[T2]: native*  |--+ |
            +----------------------+  | |
                                       | |
                 C# Span<T> views      | |    C++ kernel reads/writes
                                       | |
  +------------------------------+     | |
  | foreach ArchetypeChunk       |     | |
  |   Span<T1> = span(native*)   |<----+ |
  |   Span<T2> = span(native*)   |<------+
  +------------------------------+
```

为什么这更可能是“高性能正确方向”：

- 彻底消除“跨 GC 的指针借用”，不需要 pin，不会引入 GC 碎片化压力。
- C++ kernel 直接吃 native 指针，TaskGraph worker 也不需要进入托管运行时。
- 对于“纯数值组件”（unmanaged）可以最大化 cache locality 与 SIMD/编译器优化空间。

代价与风险（必须标注）：

- [高风险] **需要修改 PGD 源码**（至少 `PgdArray<T>`、扩容/清零/复制等路径要重写）。
- [中风险] **组件类型必须受限**：建议只对 unmanaged 组件开放路线 B；含引用的组件仍留在 C# 路线。
- [中风险] **与现有功能交互复杂**：例如 deep clone / lookup / 结构变更复制逻辑可能需要重新评估其对 native storage 的适配成本。

结论：如果路线 B 要成为长期主线，B3 是唯一能“长期稳定 + 高性能可控”的落地方式；其难点不在 TaskGraph，而在 PGD 的存储后端改造与类型约束。

---

## 4) 路线 B 是否值得做：基于 PGD 现状的建议结论

### 4.1 “把 PGD 的数据结构全部转换成 C++ 数据”的真实成本

以 PGD 当前实现来看，“转换”意味着：

- 每个 archetype、每个组件类型都要提供 native 侧连续 buffer
- 需要解决扩容、结构变更（MoveEntityTo）、默认值填充、批量创建实体等路径
- 需要保证 query 遍历与并行分片时拿到的数据视图一致

如果选择 B1（复制），性能很可能直接崩；如果选择 B2（pin），稳定性/性能不可控；因此“全量转换”只有在 B3 这种“根上 native-backed”方案里才成立。

### 4.2 这对我们当前 StackOBot/UnrealCSharp 的卡死问题意味着什么？

路线 B 的价值点很清晰：

- TaskGraph worker 不进入运行时 -> 不触发 `EnsureThreadAttached()` -> 二次 PIE 卡死的主要触发条件被绕开。

但路线 B 的代价同样清晰：

- 它无法执行“任意 C# lambda 的业务逻辑”，除非我们能把这段逻辑也变成 native kernel。

也就是说，路线 B 在“产品形态”上更像：

- 一组高性能内置 kernel（手写或生成），而不是“开发者随便写个 C# lambda 就能跑到 TaskGraph 上”。

---

## 5) 一个可执行的路线 B 方案（按阶段推进，优先保证性能不劣化）

### Phase 0：先把约束说死（避免后来返工）

- 只对 `unmanaged`（无引用字段）的组件开放 native kernel；否则直接判定“不支持路线 B”。
- 路线 B 的 kernel 不允许触达 UE UObject/引擎 API；只做纯数据计算。

### Phase 1：选一个最小闭环的“纯数值系统”作为样板

选择一个典型的 PGD 系统：只读写 `Position/Velocity/Rotation` 这类纯数值组件。

目标：能在不引入额外拷贝的情况下完成一次 “C# 发起 -> TaskGraph 并行 -> C++ kernel -> 回到 C# 观察结果”。

### Phase 2：验证 B2（pin）是否能作为短期桥接（明确量化损失）

这一步不是为了长期使用，而是为了回答一个关键问题：

> 在我们的真实 workload 下，pin/unpin + job 生命周期管理的成本到底有多大？

如果 B2 的开销已经明显吞掉并行收益，就不应该继续沿着 B2 做工程化，而应该直接转 B3。

### Phase 3：评估并实施 B3（native-backed storage）原型

这一步需要在 PGDCS 仓库内进行（属于 PGD 侧改造，不在 StackOBot 内直接落地）。

最小原型要求：

- `PgdArray<T>` 支持 native-backed（至少 for unmanaged T）
- Query 生成的 `ArchetypeChunk<T...>` 能拿到 `Span<T>`（来自 native buffer）
- `MoveEntityTo/Resize/SetDefault` 等关键路径性能不劣化（尤其是 resize 的 memcpy 成本与触发频率）

### Phase 4：形成“混合后端”策略（避免一刀切）

建议的长期形态：

- unmanaged 热路径组件：native-backed，支持路线 B
- 含引用/复杂组件：继续 C# 存储与执行（路线 C 或受控的路线 A）

---

## 6) 性能风险清单（需要在后续设计/实现中持续打标）

- [高风险] B1：任何“每帧复制大块组件数组”的方案，极大概率性能劣化。
- [高风险] B2：大规模 pin 会引入 GC 碎片与停顿风险；并行 job 生命周期越复杂，风险越高。
- [高风险] “组件含引用字段”的 native kernel：不安全或需要复杂 marshalling，几乎必然性能/稳定性劣化。
- [中风险] B3：native-backed 后端改造涉及 PGD 基础设施（扩容/结构变更/默认值/复制/lookup），需要谨慎做性能回归。
- [中风险] 多 archetype、多组件 query：即使零拷贝，kernel 的参数打包与 dispatch 粒度也可能成为新瓶颈（要沿用 PGD 的 section 阈值策略）。
