using NzbWebDAV.Config;
using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Services;

public class NzbFetchCoalescerTests
{
    [Fact]
    public async Task RejectedAdmissionDoesNotPublishSharedFailure()
    {
        var subject = new NzbFetchCoalescer(new ConfigManager());
        var url = $"https://example.invalid/{Guid.NewGuid():N}.nzb";

        var rejected = await subject.GetOrFetchAsync(
            url,
            _ => Task.FromResult(false),
            _ => Task.FromResult<byte[]?>([1]),
            CancellationToken.None);
        var fetched = await subject.GetOrFetchAsync(
            url,
            _ => Task.FromResult<byte[]?>([2]),
            CancellationToken.None);

        Assert.Null(rejected);
        Assert.Equal([2], fetched);
    }

    [Fact]
    public async Task ExistingSharedFetchSkipsSecondAdmission()
    {
        var subject = new NzbFetchCoalescer(new ConfigManager());
        var url = $"https://example.invalid/{Guid.NewGuid():N}.nzb";
        var releaseFetch = new TaskCompletionSource<byte[]?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var first = subject.GetOrFetchAsync(url, _ => releaseFetch.Task, CancellationToken.None);
        var admissionCalled = false;

        var second = subject.GetOrFetchAsync(
            url,
            _ =>
            {
                admissionCalled = true;
                return Task.FromResult(false);
            },
            _ => Task.FromResult<byte[]?>([9]),
            CancellationToken.None);
        releaseFetch.SetResult([3]);

        Assert.Equal([3], await first);
        Assert.Equal([3], await second);
        Assert.False(admissionCalled);
    }
}
