// ReSharper disable InconsistentNaming

using System.Runtime.ExceptionServices;

namespace NzbWebDAV.Extensions;

public static class IEnumerableTaskExtensions
{
    /// <summary>
    /// Executes tasks with specified concurrency and enumerates results as they come in
    /// </summary>
    /// <param name="tasks">The tasks to execute</param>
    /// <param name="concurrency">The max concurrency</param>
    /// <param name="cancellationToken">The cancellation token</param>
    /// <typeparam name="T">The resulting type of each task</typeparam>
    /// <returns>An IAsyncEnumerable that enumerates task results as they come in</returns>
    public static IEnumerable<Task<T>> WithConcurrency<T>
    (
        this IEnumerable<Task<T>> tasks,
        int concurrency
    ) where T : IDisposable
    {
        if (concurrency < 1)
            throw new ArgumentException("concurrency must be greater than zero.");

        if (concurrency == 1)
        {
            foreach (var task in tasks) yield return task;
            yield break;
        }

        var isFirst = true;
        var runningTasks = new Queue<Task<T>>();
        try
        {
            foreach (var task in tasks)
            {
                if (isFirst)
                {
                    // help with time-to-first-byte
                    yield return task;
                    isFirst = false;
                    continue;
                }

                runningTasks.Enqueue(task);
                if (runningTasks.Count < concurrency) continue;
                yield return runningTasks.Dequeue();
            }

            while (runningTasks.Count > 0)
                yield return runningTasks.Dequeue();
        }
        finally
        {
            while (runningTasks.Count > 0)
            {
                runningTasks.Dequeue().ContinueWith(x =>
                {
                    if (x.Status == TaskStatus.RanToCompletion)
                        x.Result.Dispose();
                });
            }
        }
    }

    public static async IAsyncEnumerable<T> WithConcurrencyAsync<T>
    (
        this IEnumerable<Task<T>> tasks,
        int concurrency,
        bool drainOnFailure = false
    )
    {
        if (concurrency < 1)
            throw new ArgumentException("concurrency must be greater than zero.");

        var runningTasks = new HashSet<Task<T>>();
        var shouldDrain = false;
        try
        {
            foreach (var task in tasks)
            {
                runningTasks.Add(task);
                if (runningTasks.Count < concurrency) continue;
                var completedTask = await Task.WhenAny(runningTasks).ConfigureAwait(false);
                runningTasks.Remove(completedTask);
                var outcome = await ObserveTaskAsync(completedTask).ConfigureAwait(false);
                if (outcome.Error is not null)
                {
                    shouldDrain = drainOnFailure &&
                        !outcome.Error.SourceException.IsCancellationException();
                    outcome.Error.Throw();
                }

                yield return outcome.Result;
            }

            while (runningTasks.Count > 0)
            {
                var completedTask = await Task.WhenAny(runningTasks).ConfigureAwait(false);
                runningTasks.Remove(completedTask);
                var outcome = await ObserveTaskAsync(completedTask).ConfigureAwait(false);
                if (outcome.Error is not null)
                {
                    shouldDrain = drainOnFailure &&
                        !outcome.Error.SourceException.IsCancellationException();
                    outcome.Error.Throw();
                }

                yield return outcome.Result;
            }
        }
        finally
        {
            if (shouldDrain)
                await ObserveRemainingTasksAsync(runningTasks).ConfigureAwait(false);
        }
    }

    private static async Task<TaskOutcome<T>> ObserveTaskAsync<T>(Task<T> task)
    {
        try
        {
            return new TaskOutcome<T>(await task.ConfigureAwait(false), null);
        }
        catch (Exception e)
        {
            return new TaskOutcome<T>(default!, ExceptionDispatchInfo.Capture(e));
        }
    }

    private static async Task ObserveRemainingTasksAsync<T>(IEnumerable<Task<T>> tasks)
    {
        // These tasks are already running concurrently. Await each only to let
        // provider accounting finish and to observe every terminal exception.
        foreach (var task in tasks)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }

    private readonly record struct TaskOutcome<T>(T Result, ExceptionDispatchInfo? Error);
}
