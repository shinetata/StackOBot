using System.Collections.Generic;

namespace PGD.Parallel;

public static class ScheduleParallelHandleStore
{
    private static readonly List<JobHandle> Handles = new(8);

    public static void Reset()
    {
        Handles.Clear();
    }

    public static void Add(JobHandle handle)
    {
        Handles.Add(handle);
    }

    public static void CompleteAll()
    {
        for (int i = 0; i < Handles.Count; i++)
        {
            Handles[i].Complete();
        }
        Handles.Clear();
    }

    public static int Count => Handles.Count;
}