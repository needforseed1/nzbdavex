using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Api.SabControllers.GetHistory;

internal static class HistoryCategoryClassifier
{
    internal const string StreamingMovieCategory = "streaming-movie";
    internal const string StreamingSeriesCategory = "streaming-series";

    internal static string GetDisplayCategory(string category, string? submissionSource)
    {
        if (submissionSource != SabRequestSource.StreamingSubmissionSource)
            return category;

        if (string.Equals(category, "Movies", StringComparison.OrdinalIgnoreCase))
            return StreamingMovieCategory;
        if (string.Equals(category, "TV", StringComparison.OrdinalIgnoreCase))
            return StreamingSeriesCategory;
        return category;
    }

    internal static IQueryable<HistoryItem> ApplyFilter(
        IQueryable<HistoryItem> query,
        string? category,
        bool usePhysicalCategories)
    {
        if (category == null)
            return query;
        if (usePhysicalCategories)
            return query.Where(item => item.Category == category);

        return category switch
        {
            StreamingMovieCategory => query.Where(item =>
                (item.SubmissionSource == SabRequestSource.StreamingSubmissionSource
                 && item.Category == "Movies")
                || (item.SubmissionSource != SabRequestSource.StreamingSubmissionSource
                    && item.Category == StreamingMovieCategory)),
            StreamingSeriesCategory => query.Where(item =>
                (item.SubmissionSource == SabRequestSource.StreamingSubmissionSource
                 && item.Category == "TV")
                || (item.SubmissionSource != SabRequestSource.StreamingSubmissionSource
                    && item.Category == StreamingSeriesCategory)),
            _ => query.Where(item =>
                item.Category == category
                && item.SubmissionSource != SabRequestSource.StreamingSubmissionSource),
        };
    }
}
