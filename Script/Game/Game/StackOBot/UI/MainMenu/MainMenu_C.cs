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
        private const int ITERATION = 256;
        private const int WRAM_UP_TIMES = 50;
        private const int EXECUTE_ROUND = 10;
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
            CreateTestEntities(world);
            
            var query = world.Query<PGDPosition, PGDRotation>();
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
                    query.ParallelForEach((ref PGDPosition pos, ref PGDRotation rot, IEntity entity) =>
                    {
                        pos.x += 1;
                        rot.x += 1;
                    });
                }
                sw.Stop();
                list.Add(sw.ElapsedMilliseconds);
            }
            
            long Median(List<long> list)
            {
                // list.Sort();
                // int mid = list.Count / 2;
                // return list.Count % 2 == 1 ? list[mid] : (list[mid - 1] + list[mid]) / 2;
                return list.Sum() / list.Count;
            }
            
            for (int round = 0; round < EXECUTE_ROUND; round++)
            {
                bool ab = (round % 2 == 0); // 偶数轮：A->B，奇数轮：B->A
                if (!ab)
                {
                    MeasureUe(ueTimes);
                    MeasurePgd(prTimes);
                }
                else
                {
                    MeasurePgd(prTimes);
                    MeasureUe(ueTimes);
                }
            }
            
            Console.WriteLine($"ExecuteUeParallel cost: {Median(ueTimes)} ms");
            Console.WriteLine($"ParallelForEach cost: {Median(prTimes)} ms");
            Console.WriteLine("[ScheduleTest] before schedule");
            var handle = query.ScheduleUeParallel((ref PGDPosition pos, ref PGDRotation rot, IEntity entity) =>
            {
                // 模拟耗时
                for (int i = 0; i < 100000; i++)
                {
                    pos.x += 1;
                }
            });
            Console.WriteLine("[ScheduleTest] after schedule (should be immediate)");

            Console.WriteLine($"[ScheduleTest] IsCompleted: {handle.IsCompleted}");
            handle.Wait();
            Console.WriteLine("[ScheduleTest] after Wait (should be later)");
            handle.Dispose();
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
