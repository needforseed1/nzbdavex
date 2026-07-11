using NzbWebDAV.Extensions;

namespace NzbWebDAV.Tests.Extensions;

public class ProgressExtensionsTests
{
    [Fact]
    public void EmptyPercentageWorkloadReportsComplete()
    {
        var reported = -1;
        var target = new CaptureProgress(value => reported = value);

        using var context = new ImmediateSynchronizationContextScope();
        IProgress<int> percentage = target.ToPercentage(0);
        percentage.Report(0);

        Assert.Equal(100, reported);
    }

    [Fact]
    public void EmptyMultiProgressWorkloadReportsComplete()
    {
        var reported = -1;
        var target = new CaptureProgress(value => reported = value);

        using var context = new ImmediateSynchronizationContextScope();
        IProgress<int> subProgress = target.ToMultiProgress(0).SubProgress;
        subProgress.Report(0);

        Assert.Equal(100, reported);
    }

    [Fact]
    public void ProgressTransformsReportInline()
    {
        var reported = -1;
        var target = new CaptureProgress(value => reported = value);
        var transformed = target.Offset(50).Scale(50, 100).ToPercentage(10);

        transformed.Report(4);

        Assert.Equal(70, reported);
    }

    private sealed class CaptureProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }

    private sealed class ImmediateSynchronizationContextScope : SynchronizationContext, IDisposable
    {
        private readonly SynchronizationContext? _previous = Current;

        public ImmediateSynchronizationContextScope() => SetSynchronizationContext(this);

        public override void Post(SendOrPostCallback callback, object? state) => callback(state);

        public void Dispose() => SetSynchronizationContext(_previous);
    }
}
