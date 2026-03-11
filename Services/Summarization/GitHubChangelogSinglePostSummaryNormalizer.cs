using System.Text.RegularExpressions;

namespace AutoTweetRss.Services;

internal static class GitHubChangelogSinglePostSummaryNormalizer
{
    private static readonly Regex GitHubHandlePattern = new(@"(?<![\w/])@[A-Za-z0-9][A-Za-z0-9-]*", RegexOptions.Compiled);
    private static readonly Regex HashtagPattern = new(@"(?<!\w)#[A-Za-z0-9_]+", RegexOptions.Compiled);
    private static readonly Regex WhitespacePattern = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex SpaceBeforePunctuationPattern = new(@"\s+([,.;:!?])", RegexOptions.Compiled);

    public static string Normalize(string summary, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            return string.Empty;
        }

        var lines = summary
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();

        if (lines.Count == 0)
        {
            return string.Empty;
        }

        var summarySentence = lines[0].StartsWith("•", StringComparison.Ordinal)
            ? lines[0].TrimStart('•', ' ', '\t').Trim()
            : lines[0];
        summarySentence = EnsureSentence(SanitizeLine(summarySentence));
        if (string.IsNullOrWhiteSpace(summarySentence))
        {
            return string.Empty;
        }

        var bullets = new List<string>();
        for (var index = 1; index < lines.Count; index++)
        {
            var bulletText = SanitizeLine(lines[index].TrimStart('•', ' ', '\t').Trim());
            if (!LooksLikeMeaningfulBullet(bulletText))
            {
                continue;
            }

            bullets.Add($"• {bulletText}");
        }

        return FitSummary(summarySentence, bullets, maxLength);
    }

    public static string ShortenBullet(string bullet)
    {
        var clean = Regex.Replace(bullet, @"\s+", " ").Trim();
        return clean.Length <= 55
            ? clean
            : clean[..52].TrimEnd(' ', ',', ';', ':', '-', '.', '!', '?') + "...";
    }

    public static bool LooksLikeTitleEcho(string bullet, string releaseTitle)
    {
        var normalizedBullet = NormalizeForComparison(bullet);
        var normalizedTitle = NormalizeForComparison(releaseTitle);

        if (string.IsNullOrEmpty(normalizedBullet) || string.IsNullOrEmpty(normalizedTitle))
        {
            return false;
        }

        if (normalizedTitle.Contains(normalizedBullet, StringComparison.Ordinal) ||
            normalizedBullet.Contains(normalizedTitle, StringComparison.Ordinal))
        {
            return true;
        }

        var bulletWords = normalizedBullet.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var titleWords = normalizedTitle.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (bulletWords.Length == 0 || titleWords.Length == 0)
        {
            return false;
        }

        var titleWordSet = titleWords.ToHashSet(StringComparer.Ordinal);
        var overlapCount = bulletWords.Count(titleWordSet.Contains);
        return overlapCount >= Math.Max(2, bulletWords.Length - 1);
    }

    private static string FitSummary(string summarySentence, IReadOnlyList<string> bullets, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(summarySentence) || maxLength <= 0)
        {
            return string.Empty;
        }

        if (!XPostLengthHelper.FitsWithinLimit(summarySentence, maxLength))
        {
            return TruncateSentence(summarySentence, maxLength);
        }

        if (bullets.Count == 0)
        {
            return summarySentence;
        }

        var includedBullets = new List<string>();
        foreach (var bullet in bullets.Take(2))
        {
            var candidateBullets = includedBullets.Concat([bullet]).ToList();
            var candidate = $"{summarySentence}\n\n{string.Join("\n", candidateBullets)}";
            if (!XPostLengthHelper.FitsWithinLimit(candidate, maxLength))
            {
                break;
            }

            includedBullets = candidateBullets;
        }

        return includedBullets.Count == 0
            ? summarySentence
            : $"{summarySentence}\n\n{string.Join("\n", includedBullets)}";
    }

    private static string SanitizeLine(string text)
    {
        var clean = GitHubHandlePattern.Replace(text, string.Empty);
        clean = HashtagPattern.Replace(clean, string.Empty);
        clean = WhitespacePattern.Replace(clean, " ").Trim();
        clean = SpaceBeforePunctuationPattern.Replace(clean, "$1");
        return clean.Trim();
    }

    private static bool LooksLikeMeaningfulBullet(string text)
        => !string.IsNullOrWhiteSpace(text)
            && text.Any(char.IsLetterOrDigit)
            && text.Count(char.IsLetterOrDigit) >= 8;

    private static string TruncateSentence(string sentence, int maxLength)
    {
        var truncated = XPostLengthHelper.TruncateToWeightedLength(sentence, maxLength);
        if (!truncated.EndsWith("...", StringComparison.Ordinal))
        {
            return truncated;
        }

        var withoutEllipsis = truncated[..^3].TrimEnd();
        var lastSpace = withoutEllipsis.LastIndexOf(' ');
        if (lastSpace <= 0)
        {
            return truncated;
        }

        var candidate = withoutEllipsis[..lastSpace].TrimEnd(' ', ',', ';', ':') + "...";
        return XPostLengthHelper.FitsWithinLimit(candidate, maxLength)
            ? candidate
            : truncated;
    }

    private static string EnsureSentence(string text)
    {
        var clean = text.Trim();
        if (string.IsNullOrWhiteSpace(clean))
        {
            return string.Empty;
        }

        return clean[^1] is '.' or '!' or '?'
            ? clean
            : $"{clean}.";
    }

    private static string NormalizeForComparison(string value)
        => Regex.Replace(value.ToLowerInvariant(), @"[^a-z0-9]+", " ").Trim();
}
