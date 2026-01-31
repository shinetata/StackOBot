using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Script.CoreUObject;
using Script.Engine;
using Script.Library;
using PGD;
using PGD.Parallel;

namespace Script.Game.StackOBot.UI.MainMenu
{
    public struct Health   : IComponent { public int HP; }
    public struct Mana     : IComponent { public int MP; }
    /*
     * As this is purely cosmetic and only in this main menu level the level blueprint is a nice and easy place to change the robots face every few seconds.
     * For gameplay you might want to look for a more flexible way that the actors can communicate with each other.
     * Like using interfaces, components or building managing objects for certain features.
     */
    [PathName("/Game/StackOBot/UI/MainMenu/MainMenu.MainMenu_C")]
    [Override]
    public partial class MainMenu_C : ALevelScriptActor, IStaticClass
    {
        private const int ENTITY_COUNT = 40_000;
        private const int ITERATION = 16;
        private const int WRAM_UP_TIMES = 50;
        private const int EXECUTE_ROUND = 20;
        private const int WORK = 256;
        public new static UClass StaticClass()
        {
            return StaticClassSingleton ??=
                UObjectImplementation.UObject_StaticClassImplementation(
                    "/Game/StackOBot/UI/MainMenu/MainMenu.MainMenu_C");
        }

        [Override]
        public override void ReceiveBeginPlay()
        {
            var OutActors = new TArray<AActor>();

            UGameplayStatics.GetAllActorsOfClass(this, ASkeletalMeshActor.StaticClass(), ref OutActors);

            if (OutActors.Num() > 0)
            {
                var SKM_Bot = OutActors[0] as ASkeletalMeshActor;

                BotFaceMaterial = SKM_Bot?.SkeletalMeshComponent.CreateDynamicMaterialInstance(1);

                TokenSource = new CancellationTokenSource();

                ChangeMood();
            }
            
            RunParallelPerf();
        }

        private void RunParallelPerf()
        {
            var world = new IECSWorld();
            var ueTimes = new List<long>(EXECUTE_ROUND);
            var prTimes = new List<long>(EXECUTE_ROUND);
            var scheduleTimes = new List<long>(EXECUTE_ROUND);
            var scheduleCombineTimes = new List<long>(EXECUTE_ROUND);
            var scheduleParallelTimes = new List<long>(EXECUTE_ROUND);
            CreateTestEntities(world);
            
            var query = world.Query<PGDPosition, PGDRotation>();
            var queryhealth = world.Query<Health>();
            var  querymana = world.Query<Mana>();
            var queryPos = world.Query<PGDPosition>();
            var queryRot = world.Query<PGDRotation>();
            Console.WriteLine($"chunk count: {query.ArchetypeChunk}");
            // 局部计时函数
            void MeasureUe(List<long> list)
            {
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < ITERATION; i++)
                {
                    query.ExecuteUeParallel((ref PGDPosition pos, ref PGDRotation rot, IEntity entity) =>
                    {
                        pos.x += 1;
                        rot.x += 1;
                    });
                }
                sw.Stop();
                list.Add(sw.ElapsedMilliseconds);
            }
            
            void MeasurePgd(List<long> list)
            {
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < ITERATION; i++)
                {
                    // query.ParallelForEach((ref PGDPosition pos, ref PGDRotation rot, IEntity entity) =>
                    // {
                    //     pos.x += 1;
                    //     rot.x += 1;
                    // });
                    queryPos.ParallelForEach((ref PGDPosition pos, IEntity entity) =>
                    {
                        pos.x += 1;
                    });
                    queryRot.ParallelForEach((ref PGDRotation rot, IEntity entity) =>
                    {
                        rot.x += 1;
                    });
                    queryhealth.ParallelForEach((ref Health health, IEntity entity) =>
                    {
                        health.HP += 1;
                    });
                    querymana.ParallelForEach((ref Mana Component1, IEntity Entity) =>
                    {
                        Component1.MP += 1;
                    });
                }
                sw.Stop();
                list.Add(sw.ElapsedMilliseconds);
            }

            void MeasureSchedule(List<long> list)
            {
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < ITERATION; i++)
                {
                    var handle = query.ScheduleUeParallel((ref PGDPosition pos, ref PGDRotation rot, IEntity entity) =>
                    {
                        pos.x += 1;
                        rot.x += 1;
                    });
                    handle.Wait();
                    handle.Dispose();
                }
                sw.Stop();
                list.Add(sw.ElapsedMilliseconds);
            }

            void MeasureScheduleCombine(List<long> list)
            {
                var handles = new List<UETasksJobHandle>(4);
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < ITERATION; i++)
                {
                    var h1 = queryPos.ScheduleUeParallel((ref PGDPosition pos, IEntity entity) =>
                    {
                        float v = pos.x;
                        for (int k = 0; k < WORK; k++)
                        {
                            v = v * 1.001f + 0.1f;
                        }
                        pos.x = v;
                    });
                    handles.Add(h1);
                    var h2 = queryRot.ScheduleUeParallel((ref PGDRotation rot, IEntity entity) =>
                    {
                        float v = rot.x;
                        for (int k = 0; k < WORK; k++)
                        {
                            v = v * 1.001f + 0.1f;
                        }
                        rot.x = v;
                    });
                    handles.Add(h2);
                    var h3 = queryhealth.ScheduleUeParallel((ref Health health, IEntity entity) =>
                    {
                        int v = health.HP;
                        for (int k = 0; k < WORK; k++)
                        {
                            v = v * 1 + 1;
                        }
                        health.HP = v;
                    });
                    handles.Add(h3);
                    var h4 = querymana.ScheduleUeParallel<Mana>((ref Mana Component1, IEntity Entity) =>
                    {
                        int v = Component1.MP;
                        for (int k = 0; k < WORK; k++)
                        {
                            v = v * 1 + 1;
                        }
                        Component1.MP = v;
                    });
                    handles.Add(h4);
                    for (int j = 0; j < handles.Count; j++)
                    {
                        handles[j].Dispose();
                    }
                }
                sw.Stop();
                list.Add(sw.ElapsedMilliseconds);
            }

            void MeasureScheduleUeParallelBatched(List<long> list, int batchesPerChunk)
            {
                var handles = new List<UETasksJobHandle>(4);
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < ITERATION; i++)
                {
                    var h1 = queryPos.ScheduleUeParallel((ref PGDPosition pos, IEntity entity) =>
                    {
                        float v = pos.x;
                        for (int k = 0; k < WORK; k++)
                        {
                            v = v * 1.001f + 0.1f;
                        }
                        pos.x = v;
                    }, batchesPerChunk);
                    handles.Add(h1);
                    var h2 = queryRot.ScheduleUeParallel((ref PGDRotation rot, IEntity entity) =>
                    {
                        float v = rot.x;
                        for (int k = 0; k < WORK; k++)
                        {
                            v = v * 1.001f + 0.1f;
                        }
                        rot.x = v;
                    }, batchesPerChunk);
                    handles.Add(h2);
                    var h3 = queryhealth.ScheduleUeParallel((ref Health health, IEntity entity) =>
                    {
                        int v = health.HP;
                        for (int k = 0; k < WORK; k++)
                        {
                            v = v * 1 + 1;
                        }
                        health.HP = v;
                    }, batchesPerChunk);
                    handles.Add(h3);
                    var h4 = querymana.ScheduleUeParallel<Mana>((ref Mana Component1, IEntity Entity) =>
                    {
                        int v = Component1.MP;
                        for (int k = 0; k < WORK; k++)
                        {
                            v = v * 1 + 1;
                        }
                        Component1.MP = v;
                    }, batchesPerChunk);
                    handles.Add(h4);
                    for (int j = 0; j < handles.Count; j++)
                    {
                        handles[j].Dispose();
                    }
                }
                sw.Stop();
                list.Add(sw.ElapsedMilliseconds);
            }

            void MeasureScheduleParallel(List<long> list)
            {
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < ITERATION; i++)
                {
                    var h1 = queryPos.ScheduleParallel((ref PGDPosition pos, IEntity entity) =>
                    {
                        float v = pos.x;
                        for (int k = 0; k < WORK; k++)
                        {
                            v = v * 1.001f + 0.1f;
                        }
                        pos.x = v;
                    });
                    ScheduleParallelHandleStore.Add(h1);
                    var h2 = queryRot.ScheduleParallel((ref PGDRotation rot, IEntity entity) =>
                    {
                        float v = rot.x;
                        for (int k = 0; k < WORK; k++)
                        {
                            v = v * 1.001f + 0.1f;
                        }
                        rot.x = v;
                    });
                    ScheduleParallelHandleStore.Add(h2);
                    var h3 = queryhealth.ScheduleParallel((ref Health health, IEntity entity) =>
                    {
                        int v = health.HP;
                        for (int k = 0; k < WORK; k++)
                        {
                            v = v * 1 + 1;
                        }
                        health.HP = v;
                    });
                    ScheduleParallelHandleStore.Add(h3);
                    var h4 = querymana.ScheduleParallel((ref Mana Component1, IEntity Entity) =>
                    {
                        int v = Component1.MP;
                        for (int k = 0; k < WORK; k++)
                        {
                            v = v * 1 + 1;
                        }
                        Component1.MP = v;
                    });
                    ScheduleParallelHandleStore.Add(h4);
                    ScheduleParallelHandleStore.CompleteAll();
                }
                sw.Stop();
                list.Add(sw.ElapsedMilliseconds);
            }
            
            long Median(List<long> list)
            {
                return list.Sum() / list.Count;
            }
            
            // for (int round = 0; round < EXECUTE_ROUND; round++)
            // {
            //     bool ab = (round % 2 == 0); // 偶数轮：A->B，奇数轮：B->A
            //     if (!ab)
            //     {
            //         MeasureScheduleCombine(scheduleCombineTimes);
            //         // MeasurePgd(prTimes);
            //         // MeasureScheduleParallel(scheduleParallelTimes);
            //         MeasureScheduleUeParallelBatched(scheduleParallelTimes, 4);
            //     }
            //     else
            //     {
            //         MeasureScheduleUeParallelBatched(scheduleParallelTimes, 4);
            //         // MeasurePgd(prTimes);
            //         // MeasureScheduleParallel(scheduleParallelTimes);
            //         MeasureScheduleCombine(scheduleCombineTimes);
            //     }
            // }
            //
            // Console.WriteLine($"ScheduleUeParallelBatched cost: {Median(scheduleParallelTimes)} ms");
            // Console.WriteLine($"ScheduleUeParallel+Combine cost: {Median(scheduleCombineTimes)} ms");
            // Console.WriteLine("[ScheduleTest] before schedule");
            VerifyDisposeWaitsForAllTasks();
        }

        private void VerifyScheduleWrite(IECSWorld world)
        {
            var query = world.Query<PGDPosition>();
            const float BaseValue = 100.0f;
            const float DeltaValue = 7.0f;
            const float Epsilon = 0.001f;

            query.ExecuteUeParallel((ref PGDPosition pos, IEntity entity) =>
            {
                pos.x = BaseValue;
            });

            var handle = query.ScheduleUeParallel((ref PGDPosition pos, IEntity entity) =>
            {
                pos.x += DeltaValue;
            });
            handle.Dispose();

            var errorCount = 0;
            query.ExecuteUeParallel((ref PGDPosition pos, IEntity entity) =>
            {
                if (Math.Abs(pos.x - (BaseValue + DeltaValue)) > Epsilon)
                {
                    Interlocked.Increment(ref errorCount);
                }
            });

            Console.WriteLine(errorCount == 0
                ? "[Verify] ScheduleUeParallel OK"
                : $"[Verify] ScheduleUeParallel FAILED, errorCount={errorCount}");
        }
        
        private void CreateTestEntities(IECSWorld world)
        {
            for (int i = 0; i < ENTITY_COUNT; i++)
            {
                world.CreateEntity(
                    new PGDPosition(),
                    new PGDRotation()
                );
                
                world.CreateEntity(
                    new PGDPosition(),
                    new PGDRotation(),
                    new Health()
                );
                
                world.CreateEntity(
                    new PGDPosition(),
                    new PGDRotation(),
                    new Mana()
                );
                
                world.CreateEntity(
                    new PGDPosition(),
                    new PGDRotation(),
                    new Mana(),
                    new Health()
                );
                
                world.CreateEntity(
                    new PGDPosition(),
                    new PGDRotation(),
                    new Mana(),
                    new PGDScale()
                );
                
                world.CreateEntity(
                    new PGDPosition(),
                    new PGDRotation(),
                    new Mana(),
                    new PGDTransform()
                );
                
                world.CreateEntity(
                    new PGDPosition(),
                    new PGDRotation(),
                    new Mana(),
                    new Health(),
                    new PGDScale()
                );
            }
        }

        /// <summary>
        /// 验证 Dispose 是否正确等待了所有任务完成
        /// 使用时间戳方案：第一批任务写入时间戳 1000-1999，第二批任务写入 5000-5999
        /// 如果 Dispose 没有等待所有任务，会发现两个范围的时间戳同时存在
        /// </summary>
        private void VerifyDisposeWaitsForAllTasks()
        {
            Console.WriteLine("=== Dispose Verification Test Started ===");

            var world = new IECSWorld();

            // 创建测试实体
            const int TEST_ENTITY_COUNT = 10_000;
            for (int i = 0; i < TEST_ENTITY_COUNT; i++)
            {
                world.CreateEntity(new Health { HP = 0 });
            }

            var query = world.Query<Health>();

            // 第一批任务：使用时间戳 1000-1999
            Console.WriteLine("[Verify] Step 1: Scheduling batch 1 (timestamp 1000-1999)...");
            var handle1 = query.ScheduleUeParallel((ref Health health, IEntity entity) =>
            {
                // 模拟工作负载
                int v = health.HP;
                for (int k = 0; k < 100; k++)
                {
                    v = v * 1 + 1;
                }

                // 写入时间戳：1000 + (entity.Id % 1000)，范围是 1000-1999
                health.HP = 1000 + (entity.Id % 1000);
            }, batchesPerChunk: 4);

            // 立即 Dispose，不等待
            Console.WriteLine("[Verify] Step 2: Disposing batch 1...");
            handle1.Dispose();
            Console.WriteLine("[Verify] Step 2: Dispose completed");

            // 第二批任务：使用时间戳 5000-5999
            Console.WriteLine("[Verify] Step 3: Scheduling batch 2 (timestamp 5000-5999)...");
            var handle2 = query.ScheduleUeParallel((ref Health health, IEntity entity) =>
            {
                // 模拟工作负载
                int v = health.HP;
                for (int k = 0; k < 100; k++)
                {
                    v = v * 1 + 1;
                }

                // 写入时间戳：5000 + (entity.Id % 1000)，范围是 5000-5999
                health.HP = 5000 + (entity.Id % 1000);
            }, batchesPerChunk: 4);

            Console.WriteLine("[Verify] Step 4: Disposing batch 2...");
            handle2.Dispose();
            Console.WriteLine("[Verify] Step 4: Dispose completed");

            // 检查结果
            int range1Count = 0;  // 1000-1999 (第一批任务)
            int range2Count = 0;  // 5000-5999 (第二批任务)
            int range3Count = 0;  // 其他值 (数据竞争)
            int minValue = int.MaxValue;
            int maxValue = int.MinValue;

            query.ExecuteUeParallel((ref Health health, IEntity entity) =>
            {
                if (health.HP >= 1000 && health.HP < 2000)
                {
                    Interlocked.Increment(ref range1Count);
                }
                else if (health.HP >= 5000 && health.HP < 6000)
                {
                    Interlocked.Increment(ref range2Count);
                }
                else
                {
                    // 数据竞争或其他异常值
                    Interlocked.Increment(ref range3Count);
                }

                if (health.HP < minValue) minValue = health.HP;
                if (health.HP > maxValue) maxValue = health.HP;
            });

            // 输出结果
            Console.WriteLine($"[Verify] Test Entity Count: {TEST_ENTITY_COUNT}");
            Console.WriteLine($"[Verify] Batch1 (1000-1999): {range1Count}");
            Console.WriteLine($"[Verify] Batch2 (5000-5999): {range2Count}");
            Console.WriteLine($"[Verify] Unexpected values: {range3Count}");
            Console.WriteLine($"[Verify] Min value: {minValue}, Max value: {maxValue}");

            // 判断结果
            if (range3Count > 0)
            {
                Console.WriteLine("[VERIFY FAILED] ⚠️  Detected unexpected values! Race condition detected!");
                Console.WriteLine("[VERIFY FAILED] This indicates Dispose returned BEFORE all batch1 tasks completed.");
                Console.WriteLine("[VERIFY FAILED] Batch1 and Batch2 tasks were running simultaneously.");
            }
            else if (range1Count > 0 && range2Count > 0)
            {
                Console.WriteLine("[VERIFY FAILED] ⚠️  Both batch1 and batch2 values found!");
                Console.WriteLine("[VERIFY FAILED] Tasks overlapped even though Dispose was called.");
            }
            else if (range2Count == TEST_ENTITY_COUNT)
            {
                Console.WriteLine("[Verify PASSED] ✅ All entities have batch2 values.");
                Console.WriteLine("[Verify PASSED] This means: Batch1 completed before Batch2 started (expected).");
            }
            else if (range1Count == TEST_ENTITY_COUNT)
            {
                Console.WriteLine("[VERIFY WARNING] ⚠️  All entities have batch1 values only.");
                Console.WriteLine("[VERIFY WARNING] Batch2 may not have run yet, or was too fast.");
            }
            else
            {
                Console.WriteLine($"[Verify UNKNOWN] Unexpected result. Please check manually.");
            }

            Console.WriteLine("=== Dispose Verification Test Completed ===\n");
        }

        [Override]
        public override void ReceiveEndPlay(EEndPlayReason EndPlayReason)
        {
            TokenSource.Cancel();
        }

        /*
         * Change the mood (check the face material) with a flipbook every few seconds
         */
        private async void ChangeMood()
        {
            while (!TokenSource.IsCancellationRequested)
            {
                BotFaceMaterial.SetScalarParameterValue("Mood", UKismetMathLibrary.RandomIntegerInRange(0, 14));

                await Task.Delay(UKismetMathLibrary.RandomIntegerInRange(3, 6) * 1000);
            }
        }

        public UMaterialInstanceDynamic BotFaceMaterial
        {
            get
            {
                unsafe
                {
                    var __ReturnBuffer = stackalloc byte[8];

                    FPropertyImplementation.FProperty_GetObjectPropertyImplementation(GarbageCollectionHandle,
                        __BotFaceMaterial, __ReturnBuffer);

                    return *(UMaterialInstanceDynamic*)__ReturnBuffer;
                }
            }

            set
            {
                unsafe
                {
                    var __InBuffer = stackalloc byte[8];

                    *(nint*)__InBuffer = value?.GarbageCollectionHandle ?? nint.Zero;

                    FPropertyImplementation.FProperty_SetObjectPropertyImplementation(GarbageCollectionHandle,
                        __BotFaceMaterial, __InBuffer);
                }
            }
        }

        private CancellationTokenSource TokenSource;

        private static UClass StaticClassSingleton { get; set; }

        private static uint __BotFaceMaterial = 0;
    }
}
