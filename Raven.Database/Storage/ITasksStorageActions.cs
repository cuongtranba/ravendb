//-----------------------------------------------------------------------
// <copyright file="ITasksStorageActions.cs" company="Hibernating Rhinos LTD">
//     Copyright (c) Hibernating Rhinos LTD. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------
using System;
using Raven.Abstractions.Extensions;
using Raven.Database.Tasks;

namespace Raven.Database.Storage
{
    using System.Collections.Generic;

    public interface ITasksStorageActions
    {
        void AddTask(DatabaseTask task, DateTime addedAt);
        bool HasTasks { get; }
        long ApproximateTaskCount { get; }

        T GetMergedTask<T>(Func<IComparable, MaxTaskIdStatus> maxIdStatus, 
            Action<IComparable> updateMaxTaskId, Reference<bool> foundWork)
            where T : DatabaseTask;

        IEnumerable<TaskMetadata> GetPendingTasksForDebug();
    }
}
