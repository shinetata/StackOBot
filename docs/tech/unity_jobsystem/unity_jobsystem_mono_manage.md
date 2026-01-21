### 1) Unity Job System 怎么把 C# 逻辑“放到”C++ 的 Job 线程上跑（公开信息能确认的部分）

  Unity 的 Job System 并不是把 IL“搬运”到 C++，而是用**“C++ 调度 +（托管/原生）函数入口”**的模型：

  - C# 侧 job 是 struct：例如实现 IJob.Execute() / IJobParallelFor.Execute(int)。
  - Schedule 时把两样东西交给 native JobSystem（从 UnityCsReference 可直接看到）：
      1. jobData 的地址（UnsafeUtility.AddressOf(ref jobData)）
      2. 一段“reflection data”（JobsUtility.CreateJobReflectionData(...) 的返回值），它描述了 native 在 worker 线程上要
         怎么调用你的 Execute。

  从 UnityCsReference（可公开读取）可以直接看到关键链路：

  - IJobExtensions.JobStruct<T>.Initialize() 里调用 JobsUtility.CreateJobReflectionData(typeof(T),
    (ExecuteJobFunction)Execute)，Execute 里最终是 data.Execute()（也就是你的 C# 逻辑）。
  - JobsUtility.Schedule(...)/ScheduleParallelFor(...) 绑定到 native free function：ScheduleManagedJob /
    ScheduleManagedJobParallelFor（名字就表明这是“托管 Job”被 native 调度执行）。

  执行时（engine 内部 C++ 实现未公开）可以合理抽象成：

  worker thread:
    pop job record
    call reflectionData.execute(jobDataPtr, ranges, jobIndex, ...)

  ### 2) Job worker 线程在 Unity Editor 下是否“常驻”？

  从公开接口/注释可以高度确信它是“线程池式常驻 + 可能有临时线程参与”，而不是“每个 job 创建一个线程”：

  - JobsUtility.JobWorkerCount/JobWorkerMaximumCount 是“worker 线程数量”的全局配置接口（线程池语义非常明显）。
  - JobsUtility.ThreadIndexCount 的注释明确提到：除了固定的 job worker 线程外，还可能存在“temporary workers”，原因
    是“arbitrary threads can execute jobs via work stealing”（任意线程可能通过 work-stealing 执行 job）。

  因此在 Editor 进程不退出的前提下：job worker 线程通常是常驻的；同时，确实存在“某些 job 可能在非 job worker 线程上跑”的
  公开表述（work-stealing）。

  ### 3) 最关键：Job 生命周期 vs Mono 执行环境（域重载时怎么办？）

  这里需要分两种 job：

  #### A) Burst 编译的 job（推荐理解为“脱离 Mono 托管执行”）

  Burst 文档明确：Burst 用 LLVM 把 .NET IL 编译成目标架构的高性能代码，并且最初就是为 Job System 设计的。
  当 job 被 Burst 编译后，worker 线程执行的是原生机器码入口，它不依赖“Mono 在该线程上运行托管 IL”。域重载对“已编译出的机
  器码”本身影响较小（但 job 的调度、类型/静态数据、回调注册等仍然要跟着域一起更新）。

  #### B) 非 Burst 的“托管 job”（Mono JIT 执行 IL）

  这类 job 的执行，本质上就是：native job worker 线程回调进 Mono 执行托管方法。公开资料可交叉验证两点：

  1. Unity 的 native job 调用托管入口这一点：
      - UnityCsReference 里 ScheduleManagedJob* + CreateJobReflectionData(..., managedJobFunction...) 就是“native 保存托
        管入口并在 worker 线程调用”的接口形态。
  2. Mono 对“外来线程进入托管”的处理机制：
      - Mono embedding 文档要求：应用自己创建的线程要与 Mono 交互需要 mono_thread_attach()。
      - 但对“native→managed wrapper/回调”这条路径，Mono 源码里存在 mono_threads_attach_coop / mono_threads_detach_coop，
        注释明确写了“called by native->managed wrappers”，也就是：外来线程在回调进入托管时会被 runtime 处理为可执行托管
        代码的线程（至少会做 attach/切域/GC 状态转换这一套）。

  ##### 域重载（Domain Reload）时的核心矛盾

  - 域重载会卸载并重新加载托管世界（Unity 文档对 domain reload 的行为有说明）。
  - 但 job worker 线程是 native 常驻线程；如果它继续持有“旧域的托管入口/类型数据”并回调，就会变成典型的“调用已卸载域”的
    灾难。

  ##### Unity 如何“必须”处理（哪些是公开可见、哪些是推断）

  公开可见的强信号：Unity 把“脚本 job”作为一类需要特殊调度/完成的对象看待：

  - JobHandle 的底层 native 方法名是 ScheduleBatchedScriptingJobs*（Complete/IsCompleted/CompleteAll 都会走它们）。这强
    烈暗示 Unity 有一个“scripting jobs 队列/批处理”，并且在需要同步点（如 Complete）时会统一 flush/complete。

  因此在域重载时，Unity 想要安全，最少要做到（这是工程必然性，具体实现细节未公开）：

  1. 阻止新的 scripting jobs 进入队列（停调度）。
  2. flush 并完成所有已提交的 scripting jobs（一个全局 barrier；从 ScheduleBatchedScriptingJobsAndComplete(All) 这类接口
     命名可以推断 Unity 内部有能力做这一步）。
  3. 确保没有 worker 线程正在托管世界里执行，也没有残留的“旧域托管入口”会在 reload 后被调用。
  4. reload 完成后，重新生成/注册新的 reflection data：
      - UnityCsReference 里 IJobExtensions.JobStruct<T> 用 SharedStatic<IntPtr> 缓存 jobReflectionData，并且每次
        GetReflectionData<T>() 都会 JobValidationInternal.CheckReflectionDataCorrect<T>(reflectionData)。这看起来就是
        为“防止/检测 reflection data 与当前托管类型不匹配”（比如域重载导致类型句柄变化）准备的安全网。

  域卸载前的硬要求是：不会再有任何线程在该域里执行托管代码，且线程的“当前域”不会在卸载后被恢复/切回
  到已卸载域。mono_thread_detach 是“把线程从 Mono runtime 注销”，通常用于线程即将退出或你明确要让它在一段时间内不再接触
  Mono。

  更具体地分两种：

  - 常驻 C++ 线程（线程池/Job 线程）：一般保持线程附着到 runtime（不 mono_thread_detach），但要保证它在每次进入托管前/后
    都处于“安全域”（通常是 root domain），并在域卸载前做一个全局 barrier：停止派发 → 等待所有托管回调返回 → 确保线程不再
    持有/恢复旧域（否则后续 native→managed wrapper 可能把域恢复到旧值，Mono 源码里确实存在“保存 orig domain、返回时恢
    复”的逻辑）。
  - 短命线程（用完就退出）：建议在退出前 mono_thread_detach(mono_thread_current())，避免 runtime 保留该线程的托管线程状
    态；如果在线程退出析构阶段可能触发托管交互，Mono 还提供 mono_thread_detach_if_exiting() 这种更“退出阶段友好”的兜底。

  一句话：域卸载要解决的是“所有线程都离开该域 + 不再回到该域”，不是强制把线程从 Mono runtime detach；只有当线程要结束或
  要彻底不再碰 Mono 时，才需要 detach。


  我用到的公开资料入口（便于你复核）

  - UnityCsReference：Runtime/Jobs/Managed/IJob.cs、Runtime/Jobs/ScriptBindings/Jobs.bindings.cs、Runtime/Jobs/
    ScriptBindings/JobHandle.bindings.cs
  - Burst 文档：https://docs.unity3d.com/Packages/com.unity.burst@1.8/manual/index.html
  - Mono embedding 文档：https://www.mono-project.com/docs/advanced/embedding/ + Mono 源码（mono/metadata/threads.c、
    mono/metadata/marshal.c）里对 native→managed wrappers 的线程 attach/GC 状态处理