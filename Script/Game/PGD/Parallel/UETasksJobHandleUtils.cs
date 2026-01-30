using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Script.Library;

namespace PGD.Parallel;

public static class UETasksJobHandleUtils
{
    public static UETasksJobHandle Combine(params UETasksJobHandle[] handles)
    {
        if (handles == null || handles.Length == 0)
        {
            return UETasksJobHandle.Completed;
        }

        var ids = new long[handles.Length];
        var idCount = 0;
        var gcHandles = new List<GCHandle>(handles.Length);

        for (int i = 0; i < handles.Length; i++)
        {
            var handle = handles[i];
            if (handle == null || handle.HandleId == 0)
            {
                continue;
            }

            ids[idCount++] = handle.HandleId;
            handle.DetachForCombine(gcHandles);
        }

        if (idCount == 0)
        {
            return UETasksJobHandle.Completed;
        }

        var newHandleId = FTasksQueryImplementation.FTasksQuery_CombineHandlesImplementation(ids);

        if (newHandleId == 0)
        {
            for (int i = 0; i < gcHandles.Count; i++)
            {
                if (gcHandles[i].IsAllocated)
                {
                    gcHandles[i].Free();
                }
            }
            return UETasksJobHandle.Completed;
        }

        return UETasksJobHandle.CreateCombined(newHandleId, gcHandles.ToArray());
    }
}
