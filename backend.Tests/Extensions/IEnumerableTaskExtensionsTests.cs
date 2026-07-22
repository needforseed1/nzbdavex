using NzbWebDAV.Extensions;

namespace NzbWebDAV.Tests.Extensions;

public class IEnumerableTaskExtensionsTests
{
    [Fact]
    public async Task DrainOnFailureObservesAlreadyRunningTasksBeforeReturning()
    {
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delayedFailure = Task.Run<int>(async () =>
        {
            await Task.Delay(50);
            settled.SetResult();
            throw new InvalidOperationException("late failure");
        });
        Task<int>[] tasks =
        [
            Task.FromException<int>(new InvalidOperationException("first failure")),
            delayedFailure,
        ];

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            tasks.WithConcurrencyAsync(2, drainOnFailure: true).GetAllAsync());

        Assert.Equal("first failure", error.Message);
        Assert.True(settled.Task.IsCompleted);
        Assert.True(delayedFailure.IsCompleted);
    }

    [Fact]
    public async Task CallerCancellationDoesNotWaitForUnresponsivePeerTask()
    {
        var peer = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Task<int>[] tasks =
        [
            Task.FromCanceled<int>(cancellation.Token),
            peer.Task,
        ];

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            tasks.WithConcurrencyAsync(2, drainOnFailure: true)
                .GetAllAsync().WaitAsync(TimeSpan.FromSeconds(1)));

        Assert.False(peer.Task.IsCompleted);
        peer.SetResult(0);
    }
}
