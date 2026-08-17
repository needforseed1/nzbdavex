using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace NzbWebDAV.Utils;

public static class FilenameMatcher
{
    private static readonly Regex BoundaryRegex = new(
        @"\b(\d{4}|S\d{1,2}(E\d{1,3})?|\d{3,4}p)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex NonAlnumRegex = new(
        @"[^a-z0-9]+",
        RegexOptions.Compiled);

    private static readonly Dictionary<char, string> LatinFolding = new()
    {
        ['ø'] = "o", ['œ'] = "oe", ['æ'] = "ae", ['ł'] = "l", ['đ'] = "d",
        ['ð'] = "d", ['þ'] = "th", ['ß'] = "ss", ['ı'] = "i", ['ŋ'] = "n",
    };

    private static string Fold(string s)
    {
        var decomposed = s.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;
            if (LatinFolding.TryGetValue(c, out var rep))
                sb.Append(rep);
            else
                sb.Append(c);
        }
        return sb.ToString();
    }

    public static string[] HeadTokens(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return [];
        var lower = Fold(s);
        var m = BoundaryRegex.Match(lower);
        while (m.Success && m.Index == 0) m = m.NextMatch();
        var head = m.Success ? lower[..m.Index] : lower;
        return NonAlnumRegex.Replace(head, " ")
                            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    public static bool TokensEqual(string[] a, string[] b)
    {
        if (a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++)
            if (a[i] != b[i]) return false;
        return true;
    }

    public static bool Matches(string? query, string? candidate)
    {
        var q = HeadTokens(query);
        if (q.Length == 0) return true;
        return TokensEqual(q, HeadTokens(candidate));
    }

    private static readonly Regex SeasonEpisodeRegex = new(
        @"\bs(\d{1,2})[. _-]?e(\d{1,4})(?:(?:-|e)e?(\d{1,4}))?\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SeasonDashEpisodeRegex = new(
        @"\bs(\d{1,2})\s*-\s*(?:e(?:p)?[. _-]?)?(\d{1,4})(?:v\d+)?(?:\s*[-+~]\s*(?:e(?:p)?[. _-]?)?(\d{1,4})(?:v\d+)?)?(?=$|[\s.\[(])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex NamedSeasonDashEpisodeRegex = new(
        @"\b(?:(\d{1,2})(?:st|nd|rd|th)\s+season|(?:season|series)[. _]*(\d{1,2}))\s*-\s*(?:e(?:p)?[. _-]?)?(\d{1,4})(?:v\d+)?(?:\s*[-+~]\s*(?:e(?:p)?[. _-]?)?(\d{1,4})(?:v\d+)?)?(?=$|[\s.\[(])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AltEpisodeRegex = new(
        @"(?<![a-z0-9])(\d{1,2})x(\d{1,4})(?:[-x](\d{1,4}))?(?![a-z0-9])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SeasonWordRegex = new(
        @"\b(?:season|series)[. _]*(\d{1,2})\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SeasonTokenRegex = new(
        @"\bs(\d{1,2})(?![. _-]?e?\d)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AbsoluteDashEpisodeRegex = new(
        @"(?:\s+-\s*|(?<=[a-z])-\s*)(?:e(?:p)?[. _-]?)?(\d{1,4})(?:v\d+)?(?:\s*[-+~]\s*(?:e(?:p)?[. _-]?)?(\d{1,4})(?:v\d+)?)?(?=$|[\s.\[(])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ExplicitAbsoluteEpisodeRegex = new(
        @"(?<![a-z0-9])ep(?:isode)?[. _-]?(\d{1,4})(?:v\d+)?(?:\s*[-+~]\s*(?:ep(?:isode)?[. _-]?)?(\d{1,4})(?:v\d+)?)?(?=$|[\s.\[(])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex YearThenEpisodeRegex = new(
        @"\((?:19|20)\d{2}\)\s+(\d{1,4})(?:v\d+)?(?=\s*(?:\(|\[|$))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AbsoluteBatchRegex = new(
        @"\((\d{1,4})\s*-\s*(\d{1,4})(?=[+),\s])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public readonly record struct EpisodeTag(int? Season, int? Episode, int? EpisodeEnd);

    public static EpisodeTag? ParseEpisode(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;

        var m = SeasonEpisodeRegex.Match(title);
        if (m.Success)
            return new EpisodeTag(
                int.Parse(m.Groups[1].Value),
                int.Parse(m.Groups[2].Value),
                m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : null);

        m = SeasonDashEpisodeRegex.Match(title);
        if (m.Success)
            return new EpisodeTag(
                int.Parse(m.Groups[1].Value),
                int.Parse(m.Groups[2].Value),
                m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : null);

        m = NamedSeasonDashEpisodeRegex.Match(title);
        if (m.Success)
            return new EpisodeTag(
                int.Parse((m.Groups[1].Success ? m.Groups[1] : m.Groups[2]).Value),
                int.Parse(m.Groups[3].Value),
                m.Groups[4].Success ? int.Parse(m.Groups[4].Value) : null);

        m = AltEpisodeRegex.Match(title);
        if (m.Success)
            return new EpisodeTag(
                int.Parse(m.Groups[1].Value),
                int.Parse(m.Groups[2].Value),
                m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : null);

        var absolute = ParseLastAbsoluteMatch(AbsoluteDashEpisodeRegex, title)
                       ?? ParseLastAbsoluteMatch(ExplicitAbsoluteEpisodeRegex, title)
                       ?? ParseLastAbsoluteMatch(YearThenEpisodeRegex, title)
                       ?? ParseLastAbsoluteMatch(AbsoluteBatchRegex, title);
        if (absolute is not null) return absolute;

        m = SeasonWordRegex.Match(title);
        if (m.Success)
            return new EpisodeTag(int.Parse(m.Groups[1].Value), null, null);

        m = SeasonTokenRegex.Match(title);
        if (m.Success)
            return new EpisodeTag(int.Parse(m.Groups[1].Value), null, null);

        return null;
    }

    private static EpisodeTag? ParseLastAbsoluteMatch(Regex regex, string title)
    {
        var matches = regex.Matches(title);
        for (var i = matches.Count - 1; i >= 0; i--)
        {
            var match = matches[i];
            if (IsInsideSquareBrackets(title, match.Index)) continue;
            var start = int.Parse(match.Groups[1].Value);
            var end = match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : (int?)null;
            if (IsLikelyYear(start) || end is { } rangeEnd && IsLikelyYear(rangeEnd)) continue;
            if (end is { } invalidEnd && invalidEnd < start) continue;
            return new EpisodeTag(null, start, end);
        }
        return null;
    }

    private static bool IsLikelyYear(int value) => value is >= 1900 and <= 2099;

    private static bool IsInsideSquareBrackets(string value, int index)
    {
        var lastOpen = value.LastIndexOf('[', index);
        return lastOpen >= 0 && lastOpen > value.LastIndexOf(']', index);
    }

    public static bool EpisodeCompatible(string? title, int? season, int? episode)
    {
        if (ParseEpisode(title) is not { } tag) return true;
        if (season is { } s && tag.Season is { } taggedSeason && taggedSeason != s) return false;
        if (episode is { } e && tag.Episode is { } taggedEpisode)
        {
            var end = tag.EpisodeEnd ?? taggedEpisode;
            if (e < taggedEpisode || e > end) return false;
        }
        return true;
    }

    private static readonly HashSet<string> LeadingArticles =
        new(StringComparer.Ordinal) { "the", "a", "an" };

    private static readonly HashSet<string> TrailingQualifiers =
        new(StringComparer.Ordinal) { "us", "uk", "au", "ca", "nz", "ie", "za" };

    private static string[] StripLeadingArticle(string[] tokens) =>
        tokens.Length > 1 && LeadingArticles.Contains(tokens[0]) ? tokens[1..] : tokens;

    public static string NormalizeTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "";
        var tokens = NonAlnumRegex.Replace(Fold(title), " ")
                                  .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', StripLeadingArticle(tokens));
    }

    public static bool TitleMatches(IReadOnlyCollection<string> expectedNormalized, string? releaseTitle)
    {
        if (expectedNormalized.Count == 0) return true;
        var head = StripLeadingArticle(HeadTokens(releaseTitle));
        if (head.Length == 0) return false;

        if (expectedNormalized.Contains(string.Join(' ', head))) return true;

        if (head.Length >= 2)
        {
            var last = head[^1];
            if (TrailingQualifiers.Contains(last) || (last.Length == 4 && last.All(char.IsDigit)))
                return expectedNormalized.Contains(string.Join(' ', head[..^1]));
        }
        return false;
    }
}
