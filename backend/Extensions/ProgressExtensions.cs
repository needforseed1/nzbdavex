namespace NzbWebDAV.Extensions;

public static class ProgressExtensions
{
    public static IProgress<int> ToPercentage(this IProgress<int>? progress, int total)
    {
        return new InlineProgress<int>(x => progress?.Report(total > 0 ? x * 100 / total : 100));
    }

    public static IProgress<int> Scale(this IProgress<int>? progress, int numerator, int denominator)
    {
        return new InlineProgress<int>(x => progress?.Report(x * numerator / denominator));
    }

    public static IProgress<int> Offset(this IProgress<int>? progress, int offset)
    {
        return new InlineProgress<int>(x => progress?.Report(x + offset));
    }

    public static MultiProgress ToMultiProgress(this IProgress<int>? progress, int total)
    {
        return new MultiProgress(progress, total);
    }

    public class MultiProgress(IProgress<int>? progress, int total)
    {
        private int _numerator;
        private readonly int _denominator = 100 * total;
        private readonly Lock _lock = new();

        public IProgress<int> SubProgress
        {
            get
            {
                var previous = 0;
                return new InlineProgress<int>(x =>
                {
                    int? current;
                    lock (_lock)
                    {
                        _numerator -= previous;
                        _numerator += x;
                        current = _numerator;
                    }

                    previous = x;
                    progress?.Report(_denominator > 0 ? current!.Value * 100 / _denominator : 100);
                });
            }
        }
    }
}

public sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
{
    public void Report(T value) => callback(value);
}
