using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Script.Library;

namespace PGD.Parallel;

public static class UETasksQueryExtensions
{
    public static void ExecuteUeParallel<T1>(this IQuery<T1> query, ForEachEntity<T1> lambda)
        where T1 : struct
    {
        if (lambda == null) throw new ArgumentNullException(nameof(lambda));

        var (chunks, count) = CollectChunks(query);
        if (count == 0) return;

        var runner = new QueryRunner1<T1>(query.World, chunks, count, lambda);

        if (count == 1)
        {
            runner.ExecuteTask(0);
            return;
        }

        var handle = GCHandle.Alloc(runner);
        try
        {
            FTasksQueryImplementation.FTasksQuery_ExecuteBatchImplementation(
                (nint)GCHandle.ToIntPtr(handle),
                count,
                wait: true);
        }
        finally
        {
            handle.Free();
        }
    }

    public static UETasksJobHandle ScheduleUeParallel<T1>(this IQuery<T1> query, ForEachEntity<T1> lambda)
        where T1 : struct
    {
        if (lambda == null) throw new ArgumentNullException(nameof(lambda));

        var (chunks, count) = CollectChunks(query);
        if (count == 0) return UETasksJobHandle.Completed;

        var runner = new QueryRunner1<T1>(query.World, chunks, count, lambda);

        if (count == 1)
        {
            runner.ExecuteTask(0);
            return UETasksJobHandle.Completed;
        }

        var handle = GCHandle.Alloc(runner);
        var handleId = FTasksQueryImplementation.FTasksQuery_ScheduleBatchImplementation(
            (nint)GCHandle.ToIntPtr(handle),
            count);

        if (handleId == 0)
        {
            handle.Free();
            return UETasksJobHandle.Completed;
        }

        return new UETasksJobHandle(handleId, handle);
    }

    public static void ExecuteUeParallel<T1, T2>(this IQuery<T1, T2> query, ForEachEntity<T1, T2> lambda)
        where T1 : struct
        where T2 : struct
    {
        if (lambda == null) throw new ArgumentNullException(nameof(lambda));

        var (chunks, count) = CollectChunks(query);
        if (count == 0) return;

        var runner = new QueryRunner2<T1, T2>(query.World, chunks, count, lambda);

        if (count == 1)
        {
            runner.ExecuteTask(0);
            return;
        }

        var handle = GCHandle.Alloc(runner);
        try
        {
            FTasksQueryImplementation.FTasksQuery_ExecuteBatchImplementation(
                (nint)GCHandle.ToIntPtr(handle),
                count,
                wait: true);
        }
        finally
        {
            handle.Free();
        }
    }

    public static UETasksJobHandle ScheduleUeParallel<T1, T2>(this IQuery<T1, T2> query, ForEachEntity<T1, T2> lambda)
        where T1 : struct
        where T2 : struct
    {
        if (lambda == null) throw new ArgumentNullException(nameof(lambda));

        var (chunks, count) = CollectChunks(query);
        if (count == 0) return UETasksJobHandle.Completed;

        var runner = new QueryRunner2<T1, T2>(query.World, chunks, count, lambda);

        if (count == 1)
        {
            runner.ExecuteTask(0);
            return UETasksJobHandle.Completed;
        }

        var handle = GCHandle.Alloc(runner);
        var handleId = FTasksQueryImplementation.FTasksQuery_ScheduleBatchImplementation(
            (nint)GCHandle.ToIntPtr(handle),
            count);

        if (handleId == 0)
        {
            handle.Free();
            return UETasksJobHandle.Completed;
        }

        return new UETasksJobHandle(handleId, handle);
    }

    private static (ArchetypeChunk<T1>[] chunks, int count) CollectChunks<T1>(this IQuery<T1> query)
        where T1 : struct
    {
        var chunkCount = query.ChunkCount;
        var list = new List<ArchetypeChunk<T1>>(chunkCount > 0 ? chunkCount : 0);

        foreach (var chunk in query.ArchetypeChunk)
        {
            list.Add(chunk);
        }

        var chunks = list.ToArray();
        return (chunks, chunks.Length);
    }

    private static (ArchetypeChunk<T1, T2>[] chunks, int count) CollectChunks<T1, T2>(this IQuery<T1, T2> query)
        where T1 : struct
        where T2 : struct
    {
        var chunkCount = query.ChunkCount;
        var list = new List<ArchetypeChunk<T1, T2>>(chunkCount > 0 ? chunkCount : 0);

        foreach (var chunk in query.ArchetypeChunk)
        {
            list.Add(chunk);
        }

        var chunks = list.ToArray();
        return (chunks, chunks.Length);
    }

    private sealed class QueryRunner1<T1> : IUETasksQueryRunner
        where T1 : struct
    {
        private readonly IECSWorld world;
        private readonly ArchetypeChunk<T1>[] chunks;
        private readonly int chunkCount;
        private readonly ForEachEntity<T1> action;

        public QueryRunner1(IECSWorld world,
            ArchetypeChunk<T1>[] chunks,
            int chunkCount,
            ForEachEntity<T1> action)
        {
            this.world = world;
            this.chunks = chunks;
            this.chunkCount = chunkCount;
            this.action = action;
        }

        public void ExecuteTask(int taskIndex)
        {
            if (taskIndex < 0 || taskIndex >= chunkCount) return;

            var chunk = chunks[taskIndex];
            RunChunk(chunk, 0, chunk.Length);
        }

        private void RunChunk(in ArchetypeChunk<T1> chunk, int start, int count)
        {
            var comps = chunk.Components1;
            var ids = chunk.Ids;
            var end = start + count;

            for (int n = start; n < end; n++)
            {
                action(ref comps[n], world.GetEntityById(ids[n]));
            }
        }
    }

    private sealed class QueryRunner2<T1, T2> : IUETasksQueryRunner
        where T1 : struct
        where T2 : struct
    {
        private readonly IECSWorld world;
        private readonly ArchetypeChunk<T1, T2>[] chunks;
        private readonly int chunkCount;
        private readonly ForEachEntity<T1, T2> action;

        public QueryRunner2(IECSWorld world,
            ArchetypeChunk<T1, T2>[] chunks,
            int chunkCount,
            ForEachEntity<T1, T2> action)
        {
            this.world = world;
            this.chunks = chunks;
            this.chunkCount = chunkCount;
            this.action = action;
        }

        public void ExecuteTask(int taskIndex)
        {
            if (taskIndex < 0 || taskIndex >= chunkCount) return;

            var chunk = chunks[taskIndex];
            RunChunk(chunk, 0, chunk.Length);
        }

        private void RunChunk(in ArchetypeChunk<T1, T2> chunk, int start, int count)
        {
            var c1 = chunk.Components1;
            var c2 = chunk.Components2;
            var ids = chunk.Ids;
            var end = start + count;

            for (int n = start; n < end; n++)
            {
                action(ref c1[n], ref c2[n], world.GetEntityById(ids[n]));
            }
        }
    }
}
