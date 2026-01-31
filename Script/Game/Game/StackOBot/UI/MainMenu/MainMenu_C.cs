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
        private const int ENTITY_COUNT = 80_000;
        private const int ITERATION = 16;
        private const int WRAM_UP_TIMES = 50;
        private const int EXECUTE_ROUND = 20;
        private const int WORK = 512;
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
            
            for (int round = 0; round < EXECUTE_ROUND; round++)
            {
                bool ab = (round % 2 == 0); // 偶数轮：A->B，奇数轮：B->A
                if (!ab)
                {
                    MeasureScheduleCombine(scheduleCombineTimes);
                    // MeasurePgd(prTimes);
                    MeasureScheduleParallel(scheduleParallelTimes);
                }
                else
                {
                    // MeasurePgd(prTimes);
                    MeasureScheduleParallel(scheduleParallelTimes);
                    MeasureScheduleCombine(scheduleCombineTimes);
                }
            }
            
            Console.WriteLine($"scheduleParallel cost: {Median(scheduleParallelTimes)} ms");
            Console.WriteLine($"ScheduleUeParallel+Combine cost: {Median(scheduleCombineTimes)} ms");
            Console.WriteLine("[ScheduleTest] before schedule");
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
