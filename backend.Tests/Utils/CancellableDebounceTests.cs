using System.Collections.Concurrent;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Tests.Utils;

public sealed class CancellableDebounceTests
{
    [Fact]
    public async Task CancelPendingPreventsScanProgressFromOverwritingTerminalState()
    {
        var events = new ConcurrentQueue<string>();
        using var debounce = DebounceUtil.CreateCancellableDebounce(TimeSpan.FromMilliseconds(50));
        var count = 0;
        for (var i = 1; i <= 3809; i++)
        {
            count = i;
            debounce.Invoke(() => events.Enqueue($"Scanning... Found {count}..."));
        }

        debounce.CancelPending();
        events.Enqueue("Done. Identified 816 unlinked files.");
        await Task.Delay(100);

        Assert.Equal("Done. Identified 816 unlinked files.", events.Last());
    }
}
