using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using Microsoft.Extensions.Logging;

namespace AutoTweetRss.Services;

public partial class TweetFormatterService
{
    private readonly ILogger<TweetFormatterService> _logger;
    private readonly ReleaseSummarizerService? _releaseSummarizer;

    // Twitter limits
    private const int MaxTweetLength = 280;
    private const int MaxPremiumTweetLength = 25000;
    private const int MaxBlueskyLength = 300;
    private const int UrlLength = 23; // t.co shortens all URLs to 23 chars
    private const string Hashtag = "#GitHubCopilotCLI";
    private const string SdkHashtag = "#GitHubCopilotSDK";
    private const string VSCodeHashtag = "#vscode";
    
    // Truncation constants
    private const int MinTruncatedLineLength = 10; // Minimum meaningful characters to show after truncation
    
    // Emojis for different content types
    private const string ReleaseEmojiCLI = "🚀✨";
    private const string ReleaseEmojiSDK = "🎉🔨";
    private const string FeatureEmoji = "✨";
    private const string PerformanceEmoji = "⚡";
    private const string BugFixEmoji = "🐛";
    private const string SecurityEmoji = "🔒";
    private const string DocsEmoji = "📖";
    
    // Compiled regex pattern for "...and X more" suffix matching
    [GeneratedRegex(@"\n?\.\.\.and \d+ more$")]
    private static partial Regex MoreIndicatorPattern();

    [GeneratedRegex(@"^\s*(?:[\p{So}\p{Sk}]\s+)?[^\r\n]+:\s*$")]
    private static partial Regex SectionHeaderPattern();

    public TweetFormatterService(ILogger<TweetFormatterService> logger, ReleaseSummarizerService? releaseSummarizer = null)
    {
        _logger = logger;
        _releaseSummarizer = releaseSummarizer;
    }

    private static int GetTextLength(string text, bool useXWeightedLength)
        => useXWeightedLength ? XPostLengthHelper.GetWeightedLength(text) : text.Length;

    private static bool FitsWithinLimit(string text, int limit, bool useXWeightedLength)
        => useXWeightedLength ? XPostLengthHelper.FitsWithinLimit(text, limit) : text.Length <= limit;

    private static string TruncateToLimit(string text, int limit, bool useXWeightedLength)
    {
        if (limit <= 0)
        {
            return string.Empty;
        }

        if (FitsWithinLimit(text, limit, useXWeightedLength))
        {
            return text;
        }

        return useXWeightedLength
            ? XPostLengthHelper.TruncateToWeightedLength(text, limit)
            : text[..Math.Max(0, limit - 3)] + "...";
    }

    public async Task<string> FormatTweetAsync(ReleaseEntry entry, bool useAi = false)
    {
        // Determine if we should use AI
        var shouldUseAi = useAi || ShouldUseAiFromEnvironment();
        
        // Try to use AI summarization if available and enabled
        if (shouldUseAi && _releaseSummarizer != null)
        {
            try
            {
                return await FormatTweetWithAiAsync(entry);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to generate AI summary, falling back to manual extraction");
            }
        }

        // Fall back to manual extraction
        return FormatTweet(entry);
    }

    private bool ShouldUseAiFromEnvironment()
    {
        var enableAi = Environment.GetEnvironmentVariable("ENABLE_AI_SUMMARIES");
        return !string.IsNullOrEmpty(enableAi) && bool.Parse(enableAi);
    }

    private async Task<string> FormatTweetWithAiAsync(ReleaseEntry entry)
    {
        if (_releaseSummarizer == null)
        {
            throw new InvalidOperationException("ReleaseSummarizer is not configured");
        }

        // Calculate available space for AI summary
        var header = $"{ReleaseEmojiCLI} Copilot CLI v{entry.Title} released!";
        var buffer = 6; // Small buffer to avoid edge cases
        var reservedLength = GetTextLength($"{header}\n\n", useXWeightedLength: true)
            + UrlLength
            + GetTextLength($"\n\n{Hashtag}", useXWeightedLength: true)
            + buffer;
        var availableForFeatures = Math.Max(0, MaxTweetLength - reservedLength);
        
        // Get AI-generated summary with CLI-specific prompt
        var features = await _releaseSummarizer.SummarizeReleaseAsync(entry.Title, entry.Content, availableForFeatures, feedType: "cli");
        features = NormalizeGeneratedListText(features);
        
        // Build the tweet
        var tweet = $"{header}\n\n{features}\n\n{entry.Link}\n\n{Hashtag}";
        
        // Final safety check - try to preserve "...and X more" if truncation needed
        if (!FitsWithinLimit(tweet, MaxTweetLength, useXWeightedLength: true))
        {
            features = TruncatePreservingMoreIndicatorToLimit(features, availableForFeatures, useXWeightedLength: true);
            tweet = $"{header}\n\n{features}\n\n{entry.Link}\n\n{Hashtag}";
        }
        
        return tweet;
    }

    public string FormatTweet(ReleaseEntry entry)
    {
        // Calculate available space
        // Format: "{header}\n\n{features}\n\n{url}\n\n{hashtag}"
        var header = $"{ReleaseEmojiCLI} Copilot CLI v{entry.Title} released!";
        var reservedLength = GetTextLength($"{header}\n\n", useXWeightedLength: true)
            + UrlLength
            + GetTextLength($"\n\n{Hashtag}", useXWeightedLength: true);
        var availableForFeatures = Math.Max(0, MaxTweetLength - reservedLength);
        
        // Extract and format features from HTML content
        var features = ExtractFeatures(entry.Content, availableForFeatures);
        features = NormalizeGeneratedListText(features);
        
        // Build the tweet
        var tweet = $"{header}\n\n{features}\n\n{entry.Link}\n\n{Hashtag}";
        
        // Final safety check - truncate if needed (shouldn't happen with proper calculation)
        if (!FitsWithinLimit(tweet, MaxTweetLength, useXWeightedLength: true))
        {
            features = TruncateToLimit(features, availableForFeatures, useXWeightedLength: true);
            tweet = $"{header}\n\n{features}\n\n{entry.Link}\n\n{Hashtag}";
        }
        
        return tweet;
    }

    public async Task<string> FormatSdkTweetAsync(ReleaseEntry entry, bool useAi = false)
    {
        // Determine if we should use AI
        var shouldUseAi = useAi || ShouldUseAiFromEnvironment();
        
        // Try to use AI summarization if available and enabled
        if (shouldUseAi && _releaseSummarizer != null)
        {
            try
            {
                return await FormatSdkTweetWithAiAsync(entry);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to generate AI summary for SDK release, falling back to manual extraction");
            }
        }

        // Fall back to manual extraction
        return FormatSdkTweet(entry);
    }

    private async Task<string> FormatSdkTweetWithAiAsync(ReleaseEntry entry)
    {
        if (_releaseSummarizer == null)
        {
            throw new InvalidOperationException("ReleaseSummarizer is not configured");
        }

        // Calculate available space for AI summary
        var header = $"{ReleaseEmojiSDK} Copilot SDK {entry.Title} released!";
        var buffer = 6; // Small buffer to avoid edge cases
        var reservedLength = GetTextLength($"{header}\n\n", useXWeightedLength: true)
            + UrlLength
            + GetTextLength($"\n\n{SdkHashtag}", useXWeightedLength: true)
            + buffer;
        var availableForSummary = Math.Max(0, MaxTweetLength - reservedLength);
        
        // Get AI-generated summary with SDK-specific prompt
        var summary = await _releaseSummarizer.SummarizeReleaseAsync(entry.Title, entry.Content, availableForSummary, feedType: "sdk");
        summary = NormalizeGeneratedListText(summary);
        
        // Build the tweet
        var tweet = $"{header}\n\n{summary}\n\n{entry.Link}\n\n{SdkHashtag}";
        
        // Final safety check - try to preserve "...and X more" if truncation needed
        if (!FitsWithinLimit(tweet, MaxTweetLength, useXWeightedLength: true))
        {
            summary = TruncatePreservingMoreIndicatorToLimit(summary, availableForSummary, useXWeightedLength: true);
            tweet = $"{header}\n\n{summary}\n\n{entry.Link}\n\n{SdkHashtag}";
        }
        
        return tweet;
    }

    public async Task<string> FormatWeeklyCliRecapTweetAsync(
        IReadOnlyList<ReleaseEntry> entries,
        DateTimeOffset weekStartPacific,
        DateTimeOffset weekEndPacific,
        int improvementCount,
        bool useAi = false)
    {
        if (entries == null || entries.Count == 0)
        {
            throw new ArgumentException("At least one release entry is required", nameof(entries));
        }

        var releaseCount = entries.Count;
        var dateRange = FormatDateRange(weekStartPacific, weekEndPacific);
        var releaseWord = releaseCount == 1 ? "release" : "releases";
        var improvementWord = improvementCount == 1 ? "improvement" : "improvements";
        var header = string.Join("\n",
            $"🗓️ Weekly recap ({dateRange})",
            $"🚀 Releases: {releaseCount} {releaseWord}",
            $"🛠️ Improvements: {improvementCount} {improvementWord}");

        var highlightsPrefix = "Highlights:\n";
        var url = "https://github.com/github/copilot-cli/releases";
        var buffer = 4; // Small buffer to avoid edge cases
        var reservedLength = GetTextLength($"{header}\n\n{highlightsPrefix}", useXWeightedLength: true)
            + UrlLength
            + GetTextLength($"\n\n{Hashtag}", useXWeightedLength: true)
            + buffer;
        var availableForHighlights = Math.Max(0, MaxTweetLength - reservedLength);

        string highlights;
        var shouldUseAi = useAi || ShouldUseAiFromEnvironment();
        if (shouldUseAi && _releaseSummarizer != null)
        {
            try
            {
                var combinedContent = string.Join("\n", entries.Select(e => RemoveStaffFlagItems(e.Content)));
                highlights = await _releaseSummarizer.SummarizeReleaseAsync(
                    "Copilot CLI weekly recap",
                    combinedContent,
                    availableForHighlights,
                    feedType: "cli-weekly");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to generate AI weekly recap summary, falling back to manual extraction");
                highlights = string.Empty;
            }
        }
        else
        {
            highlights = string.Empty;
        }

        if (string.IsNullOrWhiteSpace(highlights))
        {
            var combinedContent = string.Join("\n", entries.Select(e => RemoveStaffFlagItems(e.Content)));
            highlights = ExtractFeatures(combinedContent, availableForHighlights, maxItems: 7);
        }

        if (string.IsNullOrWhiteSpace(highlights))
        {
            highlights = "✨ Highlights in this week's updates";
        }

        highlights = NormalizeGeneratedListText(highlights);

        var tweet = $"{header}\n\n{highlightsPrefix}{highlights}\n\n{url}\n\n{Hashtag}";

        if (!FitsWithinLimit(tweet, MaxTweetLength, useXWeightedLength: true))
        {
            highlights = TruncatePreservingMoreIndicatorToLimit(highlights, availableForHighlights, useXWeightedLength: true);
            tweet = $"{header}\n\n{highlightsPrefix}{highlights}\n\n{url}\n\n{Hashtag}";
        }

        return tweet;
    }

    public string FormatVSCodeChangelogTweet(string summary, DateTime startDate, DateTime endDate, string url)
    {
        return FormatVSCodeChangelogPost(summary, startDate, endDate, url, MaxTweetLength, UrlLength, useXWeightedLength: true);
    }

    public string FormatVSCodeChangelogTweetForX(string summary, DateTime startDate, DateTime endDate, string url)
    {
        return FormatVSCodeChangelogPost(summary, startDate, endDate, url, MaxTweetLength, UrlLength, useXWeightedLength: true);
    }

    public string FormatVSCodeChangelogTweetForBluesky(string summary, DateTime startDate, DateTime endDate, string url)
    {
        return FormatVSCodeChangelogPost(summary, startDate, endDate, url, MaxBlueskyLength, url.Length, useXWeightedLength: false);
    }

    public async Task<string> FormatVSCodeWeeklyRecapForXAsync(
        int featureCount,
        DateTimeOffset weekStartPacific,
        DateTimeOffset weekEndPacific,
        string url,
        Func<int, Task<string>> generateSummary)
    {
        return await FormatVSCodeWeeklyRecapPostAsync(featureCount, weekStartPacific, weekEndPacific, url, MaxTweetLength, UrlLength, generateSummary, useXWeightedLength: true);
    }

    public async Task<string> FormatVSCodeWeeklyRecapForBlueskyAsync(
        int featureCount,
        DateTimeOffset weekStartPacific,
        DateTimeOffset weekEndPacific,
        string url,
        Func<int, Task<string>> generateSummary)
    {
        return await FormatVSCodeWeeklyRecapPostAsync(featureCount, weekStartPacific, weekEndPacific, url, MaxBlueskyLength, url.Length, generateSummary, useXWeightedLength: false);
    }

    private async Task<string> FormatVSCodeWeeklyRecapPostAsync(
        int featureCount,
        DateTimeOffset weekStartPacific,
        DateTimeOffset weekEndPacific,
        string url,
        int maxPostLength,
        int effectiveUrlLength,
        Func<int, Task<string>> generateSummary,
        bool useXWeightedLength)
    {
        var dateRange = FormatDateRange(weekStartPacific, weekEndPacific);
        var featureWord = featureCount == 1 ? "feature" : "features";
        var header = string.Join("\n",
            $"🗓️ Weekly recap ({dateRange})",
            $"✨ {featureCount} new {featureWord}");

        var highlightsPrefix = "Highlights:\n";
        var buffer = 4;
        var reservedLength = GetTextLength($"{header}\n\n{highlightsPrefix}", useXWeightedLength)
            + effectiveUrlLength
            + GetTextLength($"\n\n{VSCodeHashtag}", useXWeightedLength)
            + buffer;
        var availableForHighlights = Math.Max(0, maxPostLength - reservedLength);

        var highlights = await generateSummary(availableForHighlights);

        if (string.IsNullOrWhiteSpace(highlights))
        {
            highlights = "✨ See the latest Insiders updates";
        }

        highlights = NormalizeGeneratedListText(highlights);

        var post = $"{header}\n\n{highlightsPrefix}{highlights}\n\n{url}\n\n{VSCodeHashtag}";

        if (!FitsWithinLimit(post, maxPostLength, useXWeightedLength))
        {
            highlights = TruncatePreservingMoreIndicatorToLimit(highlights, availableForHighlights, useXWeightedLength);
            post = $"{header}\n\n{highlightsPrefix}{highlights}\n\n{url}\n\n{VSCodeHashtag}";
        }

        return post;
    }

    private string FormatVSCodeChangelogPost(string summary, DateTime startDate, DateTime endDate, string url, int maxPostLength, int effectiveUrlLength, bool useXWeightedLength)
    {
        var dateLabel = startDate.Date == endDate.Date
            ? FormatShortDate(endDate)
            : $"{FormatShortDate(startDate)}-{FormatShortDate(endDate)}";
        var header = $"🚀 Insiders Update - {dateLabel}";

        if (string.IsNullOrWhiteSpace(summary))
        {
            summary = "See latest updates.";
        }

        summary = NormalizeGeneratedListText(summary);

        var buffer = 4;
        var reservedLength = GetTextLength($"{header}\n\n", useXWeightedLength)
            + effectiveUrlLength
            + GetTextLength($"\n\n{VSCodeHashtag}", useXWeightedLength)
            + buffer;
        var availableForSummary = Math.Max(0, maxPostLength - reservedLength);

        if (!FitsWithinLimit(summary, availableForSummary, useXWeightedLength))
        {
            if (availableForSummary > MinTruncatedLineLength)
            {
                summary = TruncatePreservingMoreIndicatorToLimit(summary, availableForSummary, useXWeightedLength);
            }
            else
            {
                summary = "See latest updates.";
            }
        }

        var tweet = $"{header}\n\n{summary}\n\n{url}\n\n{VSCodeHashtag}";

        if (!FitsWithinLimit(tweet, maxPostLength, useXWeightedLength))
        {
            summary = TruncatePreservingMoreIndicatorToLimit(summary, availableForSummary, useXWeightedLength);
            tweet = $"{header}\n\n{summary}\n\n{url}\n\n{VSCodeHashtag}";
        }

        return tweet;
    }

    /// <summary>
    /// Truncates the summary while trying to preserve the "...and X more" indicator
    /// </summary>
    private static string TruncatePreservingMoreIndicatorToLimit(string summary, int targetLength, bool useXWeightedLength)
    {
        if (targetLength <= 0)
        {
            return string.Empty;
        }

        if (FitsWithinLimit(summary, targetLength, useXWeightedLength))
        {
            return summary;
        }

        // Check if the summary ends with "...and X more" pattern
        var match = MoreIndicatorPattern().Match(summary);
        
        if (match.Success)
        {
            // Extract the "...and X more" suffix
            var moreSuffix = match.Value;
            var contentWithoutSuffix = summary[..match.Index];
            
            // Find the last complete line in the content
            var lines = contentWithoutSuffix.Split('\n').ToList();
            
            // Try to remove lines from the end until we fit
            while (lines.Count > 1)
            {
                lines.RemoveAt(lines.Count - 1);
                var trimmedContent = string.Join("\n", lines);
                var newSummary = trimmedContent + moreSuffix;
                
                if (FitsWithinLimit(newSummary, targetLength, useXWeightedLength))
                {
                    return newSummary;
                }
            }
            
            // If we still don't fit, truncate the last remaining line
            if (lines.Count == 1)
            {
                var availableLength = targetLength - GetTextLength(moreSuffix, useXWeightedLength) - 3;
                if (availableLength > MinTruncatedLineLength)
                {
                    return TruncateToLimit(lines[0], availableLength, useXWeightedLength) + moreSuffix;
                }
            }
        }
        
        // Fallback to simple truncation if no "more" pattern found
        return TruncateToLimit(summary, targetLength, useXWeightedLength);
    }

    private static string NormalizeGeneratedListText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = normalized.Split('\n', StringSplitOptions.None);
        var firstListLineIndex = FindFirstListLineIndex(lines);

        if (firstListLineIndex < 0)
        {
            return normalized;
        }

        for (var index = firstListLineIndex; index < lines.Length; index++)
        {
            var trimmed = lines[index].Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || IsMoreIndicatorLine(trimmed) || SectionHeaderPattern().IsMatch(trimmed))
            {
                continue;
            }

            lines[index] = NormalizeListItem(trimmed);
        }

        return string.Join("\n", lines);
    }

    private static int FindFirstListLineIndex(string[] lines)
    {
        var sawContent = false;

        for (var index = 0; index < lines.Length; index++)
        {
            var trimmed = lines[index].Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                if (!sawContent)
                {
                    continue;
                }

                for (var nextIndex = index + 1; nextIndex < lines.Length; nextIndex++)
                {
                    var nextTrimmed = lines[nextIndex].Trim();
                    if (string.IsNullOrWhiteSpace(nextTrimmed))
                    {
                        continue;
                    }

                    if (IsMoreIndicatorLine(nextTrimmed))
                    {
                        return -1;
                    }

                    return nextIndex;
                }

                return -1;
            }

            sawContent = true;
        }

        return Array.FindIndex(lines, line => !string.IsNullOrWhiteSpace(line));
    }

    private static bool IsMoreIndicatorLine(string line)
        => line.StartsWith("...and ", StringComparison.OrdinalIgnoreCase)
            && line.EndsWith(" more", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeListItem(string item)
    {
        var trimmed = item.Trim();
        if (string.IsNullOrWhiteSpace(trimmed) || IsMoreIndicatorLine(trimmed))
        {
            return trimmed;
        }

        if (trimmed.StartsWith("• ", StringComparison.Ordinal))
        {
            return trimmed;
        }

        if (trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed.StartsWith("* ", StringComparison.Ordinal))
        {
            return $"• {trimmed[2..].TrimStart()}";
        }

        return $"• {trimmed}";
    }

    public string FormatSdkTweet(ReleaseEntry entry)
    {
        // Calculate available space
        // Format: "{header}\n\n{summary}\n\n{url}\n\n{hashtag}"
        var header = $"{ReleaseEmojiSDK} Copilot SDK {entry.Title} released!";
        var reservedLength = GetTextLength($"{header}\n\n", useXWeightedLength: true)
            + UrlLength
            + GetTextLength($"\n\n{SdkHashtag}", useXWeightedLength: true);
        var availableForSummary = Math.Max(0, MaxTweetLength - reservedLength);
        
        // Extract and summarize changes from SDK content
        var summary = ExtractSdkSummary(entry.Content, availableForSummary);
        summary = NormalizeGeneratedListText(summary);
        
        // Build the tweet
        var tweet = $"{header}\n\n{summary}\n\n{entry.Link}\n\n{SdkHashtag}";
        
        // Final safety check - truncate if needed
        if (!FitsWithinLimit(tweet, MaxTweetLength, useXWeightedLength: true))
        {
            summary = TruncateToLimit(summary, availableForSummary, useXWeightedLength: true);
            tweet = $"{header}\n\n{summary}\n\n{entry.Link}\n\n{SdkHashtag}";
        }
        
        return tweet;
    }

    private string ExtractFeatures(string htmlContent, int maxLength, int maxItems = int.MaxValue)
    {
        // Decode HTML entities
        var decoded = HttpUtility.HtmlDecode(htmlContent);
        
        // Extract list items from HTML
        var listItemPattern = @"<li[^>]*>(.*?)</li>";
        var matches = Regex.Matches(decoded, listItemPattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        
        var features = new List<string>();
        
        foreach (Match match in matches)
        {
            var text = StripHtml(match.Groups[1].Value).Trim();
            if (!string.IsNullOrWhiteSpace(text))
            {
                var emoji = GetEmojiForFeature(text);
                features.Add($"{emoji} {text}");
            }
        }
        
        // If no list items found, try to extract from plain text
        if (features.Count == 0)
        {
            var plainText = StripHtml(decoded).Trim();
            if (!string.IsNullOrWhiteSpace(plainText))
            {
                // Split by newlines or sentences
                var lines = plainText.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(l => l.Trim())
                    .Where(l => !string.IsNullOrWhiteSpace(l) && !IsDateLine(l))
                    .ToList();
                
                foreach (var line in lines.Take(maxItems))
                {
                    var emoji = GetEmojiForFeature(line);
                    features.Add($"{emoji} {line}");
                }
            }
        }
        
        // Build features string, respecting max length
        var result = new List<string>();
        var currentLength = 0;
        foreach (var feature in features.Take(maxItems))
        {
            var featureWithNewline = result.Count > 0 ? $"\n{feature}" : feature;
            
            if (currentLength + GetTextLength(featureWithNewline, useXWeightedLength: true) <= maxLength)
            {
                result.Add(feature);
                currentLength += GetTextLength(featureWithNewline, useXWeightedLength: true);
            }
            else
            {
                // If there are more features to show, don't truncate - just stop here
                // and let the "...and X more" indicator handle it
                if (features.Count > result.Count)
                {
                    // There are more features, so just break and let the indicator show
                    break;
                }
                
                // This is the last feature, try to fit a truncated version
                var available = maxLength - currentLength - (result.Count > 0 ? 1 : 0) - 3; // -3 for "..."
                if (available > 20) // Only add if we can show at least 20 chars
                {
                    result.Add(feature[..available] + "...");
                }
                break;
            }
        }
        
        // If we have more features not shown, add indicator
        if (features.Count > result.Count && currentLength + 10 < maxLength)
        {
            var remaining = features.Count - result.Count;
            var indicator = $"...and {remaining} more";
            if (currentLength + GetTextLength($"\n{indicator}", useXWeightedLength: true) <= maxLength)
            {
                result.Add(indicator);
            }
        }
        
        return string.Join("\n", result);
    }

    private static string FormatDateRange(DateTimeOffset start, DateTimeOffset end)
    {
        var startText = start.ToString("MMM d");
        var endText = end.ToString("MMM d");
        return $"{startText}-{endText}";
    }

    private static string FormatShortDate(DateTime date)
    {
        return date.ToString("MMM d", CultureInfo.InvariantCulture);
    }

    private static string RemoveStaffFlagItems(string htmlContent)
    {
        if (string.IsNullOrWhiteSpace(htmlContent))
        {
            return string.Empty;
        }

        var withoutStaffFlags = Regex.Replace(
            htmlContent,
            @"<li[^>]*>[^<]*(?:(?!</li>).)*?staff flag(?:(?!</li>).)*?</li>",
            string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        return withoutStaffFlags;
    }

    private static string GetEmojiForFeature(string text)
    {
        var lowerText = text.ToLowerInvariant();
        
        if (lowerText.Contains("fix") || lowerText.Contains("bug"))
            return BugFixEmoji;
        if (lowerText.Contains("security") || lowerText.Contains("vulnerab"))
            return SecurityEmoji;
        if (lowerText.Contains("performance") || lowerText.Contains("fast") || lowerText.Contains("speed") || lowerText.Contains("optim"))
            return PerformanceEmoji;
        if (lowerText.Contains("doc") || lowerText.Contains("readme"))
            return DocsEmoji;
        
        return FeatureEmoji; // Default to feature emoji
    }

    private static string StripHtml(string html)
    {
        // Remove HTML tags
        var withoutTags = Regex.Replace(html, @"<[^>]+>", " ");
        // Normalize whitespace
        var normalized = Regex.Replace(withoutTags, @"\s+", " ");
        return normalized.Trim();
    }

    private static bool IsDateLine(string line)
    {
        // Check if line is just a date like "2026-01-20"
        return Regex.IsMatch(line.Trim(), @"^\d{4}-\d{2}-\d{2}$");
    }

    private string ExtractSdkSummary(string htmlContent, int maxLength)
    {
        // Decode HTML entities
        var decoded = HttpUtility.HtmlDecode(htmlContent);
        
        var changes = new List<string>();

        // New format (v0.1.29+): major features are in <h3> headings
        // e.g. <h3>Feature: multi-client tool and permission broadcasts</h3>
        var h3Pattern = @"<h3[^>]*>(.*?)</h3>";
        var h3Matches = Regex.Matches(decoded, h3Pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        foreach (Match h3Match in h3Matches)
        {
            var text = StripHtml(h3Match.Groups[1].Value).Trim();
            // Skip generic section headers
            if (string.IsNullOrWhiteSpace(text) ||
                text.StartsWith("Other changes", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("New contributor", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("What", StringComparison.OrdinalIgnoreCase))
                continue;
            var emoji = GetEmojiForFeature(text);
            changes.Add($"{emoji} {text}");
        }

        // Extract list items (works for both old "What's Changed" flat list and
        // new "Other changes" sub-list)
        var listItemPattern = @"<li[^>]*>(.*?)</li>";
        var matches = Regex.Matches(decoded, listItemPattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        
        foreach (Match match in matches)
        {
            var text = StripHtml(match.Groups[1].Value).Trim();
            
            // Clean up the text - remove PR references and author mentions
            text = Regex.Replace(text, @"(?:by\s+@\S+\s+)?(?:in\s+)?#\d+$|by\s+@\S+\s+in\s+#\d+", "", RegexOptions.IgnoreCase).Trim();
            
            // Strip new-format prefixes (feature:, improvement:, bugfix:) — keep the rest
            text = Regex.Replace(text, @"^(?:feature|improvement|bugfix|chore|docs):\s*", "", RegexOptions.IgnoreCase).Trim();

            if (!string.IsNullOrWhiteSpace(text) &&
                !text.StartsWith("Full Changelog", StringComparison.OrdinalIgnoreCase) &&
                !text.Contains("made their first contribution", StringComparison.OrdinalIgnoreCase) &&
                !text.StartsWith("Generated by", StringComparison.OrdinalIgnoreCase))
            {
                var emoji = GetEmojiForFeature(text);
                changes.Add($"{emoji} {text}");
            }
        }
        
        // If we have too many changes, summarize
        if (changes.Count == 0)
        {
            return "New updates and improvements";
        }
        
        // Build summary string, respecting max length
        var result = new List<string>();
        var currentLength = 0;
        var maxItems = Math.Clamp((maxLength - 12) / 45, 1, 12);
        
        foreach (var change in changes.Take(maxItems))
        {
            var changeWithNewline = result.Count > 0 ? $"\n{change}" : change;
            
            if (currentLength + GetTextLength(changeWithNewline, useXWeightedLength: true) <= maxLength)
            {
                result.Add(change);
                currentLength += GetTextLength(changeWithNewline, useXWeightedLength: true);
            }
            else
            {
                // Try to fit a truncated version
                var available = maxLength - currentLength - (result.Count > 0 ? 1 : 0) - 3; // -3 for "..."
                if (available > 20) // Only add if we can show at least 20 chars
                {
                    result.Add(change[..available] + "...");
                }
                break;
            }
        }
        
        // If we have more changes not shown, add indicator
        if (changes.Count > result.Count && currentLength + 10 < maxLength)
        {
            var remaining = changes.Count - result.Count;
            if (remaining > 0)
            {
                var indicator = $"...and {remaining} more";
                if (currentLength + GetTextLength($"\n{indicator}", useXWeightedLength: true) <= maxLength)
                {
                    result.Add(indicator);
                }
            }
        }
        
        return string.Join("\n", result);
    }

    // -----------------------------------------------------------------------
    // Thread formatting — per-stream methods
    // -----------------------------------------------------------------------

    /// <summary>Reads THREAD_MAX_POSTS from environment (default 4, minimum 2).</summary>
    private static int GetMaxThreadPosts()
    {
        var value = Environment.GetEnvironmentVariable("THREAD_MAX_POSTS");
        return int.TryParse(value, out var n) && n >= 2 ? n : 6;
    }

    /// <summary>Reads THREAD_TOP_HIGHLIGHTS from environment (default 3, minimum 1).</summary>
    private static int GetTopHighlightsCount()
    {
        var value = Environment.GetEnvironmentVariable("THREAD_TOP_HIGHLIGHTS");
        return int.TryParse(value, out var n) && n >= 1 ? n : 3;
    }

    /// <summary>Formats a Copilot CLI release as a thread for X/Twitter.</summary>
    public Task<IReadOnlyList<string>> FormatCliThreadForXAsync(ReleaseEntry entry, bool useAi = false)
        => FormatReleaseThreadAsync(entry, useAi, isCli: true, MaxTweetLength, Hashtag, useXWeightedLength: true);

    /// <summary>Formats a Copilot CLI release as a thread for Bluesky.</summary>
    public Task<IReadOnlyList<string>> FormatCliThreadForBlueskyAsync(ReleaseEntry entry, bool useAi = false)
        => FormatReleaseThreadAsync(entry, useAi, isCli: true, MaxBlueskyLength, Hashtag, useXWeightedLength: false);

    /// <summary>Formats a Copilot SDK release as a thread for X/Twitter.</summary>
    public Task<IReadOnlyList<string>> FormatSdkThreadForXAsync(ReleaseEntry entry, bool useAi = false)
        => FormatReleaseThreadAsync(entry, useAi, isCli: false, MaxTweetLength, SdkHashtag, useXWeightedLength: true);

    /// <summary>Formats a Copilot SDK release as a thread for Bluesky.</summary>
    public Task<IReadOnlyList<string>> FormatSdkThreadForBlueskyAsync(ReleaseEntry entry, bool useAi = false)
        => FormatReleaseThreadAsync(entry, useAi, isCli: false, MaxBlueskyLength, SdkHashtag, useXWeightedLength: false);

    /// <summary>Formats a Copilot CLI release as a single Premium X mega-post.</summary>
    public Task<string> FormatCliPremiumPostForXAsync(ReleaseEntry entry, bool useAi = false)
        => FormatReleasePremiumPostForXAsync(entry, useAi, isCli: true, Hashtag);

    /// <summary>Formats a Copilot SDK release as a single Premium X mega-post.</summary>
    public Task<string> FormatSdkPremiumPostForXAsync(ReleaseEntry entry, bool useAi = false)
        => FormatReleasePremiumPostForXAsync(entry, useAi, isCli: false, SdkHashtag);

    private async Task<IReadOnlyList<string>> FormatReleaseThreadAsync(
        ReleaseEntry entry, bool useAi, bool isCli, int maxPostLength, string hashtag, bool useXWeightedLength)
    {
        var shouldUseAi = useAi || ShouldUseAiFromEnvironment();
        ThreadPlan? plan = null;

        if (shouldUseAi && _releaseSummarizer != null)
        {
            try
            {
                var feedType = isCli ? "cli" : "sdk";
                plan = await _releaseSummarizer.PlanThreadAsync(
                    entry.Title, entry.Content, feedType, maxPostLength,
                    GetMaxThreadPosts(), GetTopHighlightsCount());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to generate AI thread plan, using fallback");
            }
        }

        var header = isCli
            ? $"{ReleaseEmojiCLI} Copilot CLI v{entry.Title} released!"
            : $"{ReleaseEmojiSDK} Copilot SDK {entry.Title} released!";

        return BuildReleaseThread(entry, plan, header, maxPostLength, hashtag, useXWeightedLength);
    }

    private async Task<string> FormatReleasePremiumPostForXAsync(
        ReleaseEntry entry,
        bool useAi,
        bool isCli,
        string hashtag)
    {
        var shouldUseAi = useAi || ShouldUseAiFromEnvironment();
        PremiumPostPlan? premiumPlan = null;
        ThreadPlan? threadPlan = null;

        if (shouldUseAi && _releaseSummarizer != null)
        {
            try
            {
                var feedType = isCli ? "cli" : "sdk";
                premiumPlan = await _releaseSummarizer.PlanPremiumPostAsync(
                    entry.Title,
                    entry.Content,
                    feedType,
                    MaxPremiumTweetLength);

                if (premiumPlan == null)
                {
                    threadPlan = await _releaseSummarizer.PlanThreadAsync(
                        entry.Title,
                        entry.Content,
                        feedType,
                        MaxPremiumTweetLength,
                        maxPosts: 1,
                        topHighlights: Math.Max(GetTopHighlightsCount(), 5));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to generate AI premium post plan, using fallback");
            }
        }

        var header = isCli
            ? $"{ReleaseEmojiCLI} Copilot CLI v{entry.Title} released!"
            : $"{ReleaseEmojiSDK} Copilot SDK {entry.Title} released!";

        if (premiumPlan != null)
        {
            return BuildPremiumMegaPost(
                header,
                premiumPlan.TotalCount,
                premiumPlan.TopFeatures.Select(NormalizeListItem).ToList(),
                premiumPlan.Enhancements.Select(NormalizeListItem).ToList(),
                premiumPlan.BugFixes.Select(NormalizeListItem).ToList(),
                premiumPlan.Misc.Select(NormalizeListItem).ToList(),
                entry.Link,
                hashtag,
                MaxPremiumTweetLength);
        }

        var rankedItems = (threadPlan?.Items?.Count > 0 ? threadPlan.Items : ExtractFeatureList(entry.Content))
            .Select(NormalizeListItem)
            .ToList();
        var totalCount = threadPlan?.TotalCount > 0 ? threadPlan.TotalCount : rankedItems.Count;

        return BuildPremiumMegaPost(header, totalCount, rankedItems, entry.Link, hashtag, MaxPremiumTweetLength);
    }

    private IReadOnlyList<string> BuildReleaseThread(
        ReleaseEntry entry, ThreadPlan? plan, string header, int maxPostLength, string hashtag, bool useXWeightedLength)
    {
        List<string> allItems;
        int totalCount;

        if (plan != null && plan.Items.Count > 0)
        {
            allItems = plan.Items;
            totalCount = plan.TotalCount;
        }
        else
        {
            // Deterministic fallback: extract features from HTML
            allItems = ExtractFeatureList(entry.Content);
            totalCount = allItems.Count;
        }

        allItems = allItems.Select(NormalizeListItem).ToList();
        var topN = GetTopHighlightsCount();
        var highlights = allItems.Take(topN).ToList();
        var remaining = allItems.Skip(topN).ToList();
        var followUpGroups = PackItemsIntoPosts(remaining, maxPostLength, useXWeightedLength);

        return AssembleThread(header, highlights, followUpGroups, totalCount, entry.Link, hashtag, maxPostLength, useXWeightedLength);
    }

    /// <summary>Formats a CLI weekly recap as a thread for X/Twitter.</summary>
    public Task<IReadOnlyList<string>> FormatWeeklyCliRecapThreadAsync(
        IReadOnlyList<ReleaseEntry> entries,
        DateTimeOffset weekStartPacific,
        DateTimeOffset weekEndPacific,
        int improvementCount,
        bool useAi = false)
        => FormatWeeklyCliRecapThreadInternalAsync(entries, weekStartPacific, weekEndPacific, improvementCount, useAi, MaxTweetLength, useXWeightedLength: true);

    private async Task<IReadOnlyList<string>> FormatWeeklyCliRecapThreadInternalAsync(
        IReadOnlyList<ReleaseEntry> entries,
        DateTimeOffset weekStartPacific,
        DateTimeOffset weekEndPacific,
        int improvementCount,
        bool useAi,
        int maxPostLength,
        bool useXWeightedLength)
    {
        var releaseCount = entries.Count;
        var dateRange = FormatDateRange(weekStartPacific, weekEndPacific);
        var releaseWord = releaseCount == 1 ? "release" : "releases";
        var improvementWord = improvementCount == 1 ? "improvement" : "improvements";
        var header = string.Join("\n",
            $"🗓️ Weekly recap ({dateRange})",
            $"🚀 {releaseCount} {releaseWord}",
            $"🛠️ {improvementCount} {improvementWord}");

        var shouldUseAi = useAi || ShouldUseAiFromEnvironment();
        ThreadPlan? plan = null;

        if (shouldUseAi && _releaseSummarizer != null)
        {
            try
            {
                var combinedContent = string.Join("\n", entries.Select(e => RemoveStaffFlagItems(e.Content)));
                plan = await _releaseSummarizer.PlanThreadAsync(
                    "Copilot CLI weekly recap", combinedContent, "cli-weekly",
                    maxPostLength, GetMaxThreadPosts(), GetTopHighlightsCount());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to generate AI thread plan for weekly recap, using fallback");
            }
        }

        List<string> allItems;
        int totalCount;

        if (plan != null && plan.Items.Count > 0)
        {
            allItems = plan.Items;
            totalCount = plan.TotalCount;
        }
        else
        {
            var combinedContent = string.Join("\n", entries.Select(e => RemoveStaffFlagItems(e.Content)));
            allItems = ExtractFeatureList(combinedContent);
            totalCount = allItems.Count;
        }

        allItems = allItems.Select(NormalizeListItem).ToList();
        var topN = GetTopHighlightsCount();
        var highlights = allItems.Take(topN).ToList();
        var remaining = allItems.Skip(topN).ToList();
        var followUpGroups = PackItemsIntoPosts(remaining, maxPostLength, useXWeightedLength);

        var url = "https://github.com/github/copilot-cli/releases";
        return AssembleThread(header, highlights, followUpGroups, totalCount, url, Hashtag, maxPostLength, useXWeightedLength);
    }

    /// <summary>Formats a CLI weekly recap as a single Premium X mega-post.</summary>
    public async Task<string> FormatWeeklyCliRecapPremiumPostForXAsync(
        IReadOnlyList<ReleaseEntry> entries,
        DateTimeOffset weekStartPacific,
        DateTimeOffset weekEndPacific,
        int improvementCount,
        bool useAi = false)
    {
        var releaseCount = entries.Count;
        var dateRange = FormatDateRange(weekStartPacific, weekEndPacific);
        var releaseWord = releaseCount == 1 ? "release" : "releases";
        var improvementWord = improvementCount == 1 ? "improvement" : "improvements";
        var header = string.Join("\n",
            $"🗓️ Weekly recap ({dateRange})",
            $"🚀 {releaseCount} {releaseWord}",
            $"🛠️ {improvementCount} {improvementWord}");

        var shouldUseAi = useAi || ShouldUseAiFromEnvironment();
        PremiumPostPlan? premiumPlan = null;
        ThreadPlan? threadPlan = null;

        if (shouldUseAi && _releaseSummarizer != null)
        {
            try
            {
                var combinedContent = string.Join("\n", entries.Select(e => RemoveStaffFlagItems(e.Content)));
                premiumPlan = await _releaseSummarizer.PlanPremiumPostAsync(
                    "Copilot CLI weekly recap", combinedContent, "cli-weekly", MaxPremiumTweetLength);

                if (premiumPlan == null)
                {
                    threadPlan = await _releaseSummarizer.PlanThreadAsync(
                        "Copilot CLI weekly recap", combinedContent, "cli-weekly",
                        MaxPremiumTweetLength, maxPosts: 1, topHighlights: Math.Max(GetTopHighlightsCount(), 5));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to generate AI premium post plan for CLI weekly recap, using fallback");
            }
        }

        const string url = "https://github.com/github/copilot-cli/releases";

        if (premiumPlan != null)
        {
            return BuildPremiumMegaPost(
                header,
                premiumPlan.TotalCount,
                premiumPlan.TopFeatures.Select(NormalizeListItem).ToList(),
                premiumPlan.Enhancements.Select(NormalizeListItem).ToList(),
                premiumPlan.BugFixes.Select(NormalizeListItem).ToList(),
                premiumPlan.Misc.Select(NormalizeListItem).ToList(),
                url,
                Hashtag,
                MaxPremiumTweetLength);
        }
        var combinedForFallback = string.Join("\n", entries.Select(e => RemoveStaffFlagItems(e.Content)));
        var rankedItems = (threadPlan?.Items?.Count > 0 ? threadPlan.Items : ExtractFeatureList(combinedForFallback))
            .Select(NormalizeListItem)
            .ToList();
        var totalCount = threadPlan?.TotalCount > 0 ? threadPlan.TotalCount : rankedItems.Count;

        return BuildPremiumMegaPost(header, totalCount, rankedItems, url, Hashtag, MaxPremiumTweetLength);
    }

    /// <summary>Formats a VS Code Insiders daily changelog as a thread for X/Twitter.</summary>
    public IReadOnlyList<string> FormatVSCodeChangelogThreadForX(
        string fullSummary, int featureCount, DateTime startDate, DateTime endDate, string url)
        => FormatVSCodeChangelogThread(fullSummary, featureCount, startDate, endDate, url, MaxTweetLength, useXWeightedLength: true);

    /// <summary>Formats a VS Code Insiders daily changelog as a thread for Bluesky.</summary>
    public IReadOnlyList<string> FormatVSCodeChangelogThreadForBluesky(
        string fullSummary, int featureCount, DateTime startDate, DateTime endDate, string url)
        => FormatVSCodeChangelogThread(fullSummary, featureCount, startDate, endDate, url, MaxBlueskyLength, useXWeightedLength: false);

    /// <summary>Formats a VS Code changelog as a single Premium X mega-post.</summary>
    public string FormatVSCodeChangelogPremiumPostForX(
        IReadOnlyList<VSCodeFeature> features,
        int featureCount,
        DateTime startDate,
        DateTime endDate,
        string url)
    {
        var dateLabel = startDate.Date == endDate.Date
            ? FormatShortDate(endDate)
            : $"{FormatShortDate(startDate)}-{FormatShortDate(endDate)}";

        var header = $"🚀 Insiders Update - {dateLabel}";

        var topFeatures = new List<string>();
        var enhancements = new List<string>();
        var bugFixes = new List<string>();
        var misc = new List<string>();

        foreach (var feature in features)
        {
            var text = string.IsNullOrWhiteSpace(feature.Description)
                ? feature.Title
                : $"{feature.Title}: {feature.Description}";
            text = text.Trim();
            if (!FitsWithinLimit(text, 230, useXWeightedLength: true))
            {
                text = TruncateToLimit(text, 230, useXWeightedLength: true);
            }

            var line = $"{GetEmojiForFeature(text)} {text}";
            if (bugFixes.Count < 15 && ClassifyFeatureBucket(line) == FeatureBucket.BugFix)
            {
                bugFixes.Add(line);
            }
            else if (enhancements.Count < 20 && ClassifyFeatureBucket(line) == FeatureBucket.Enhancement)
            {
                enhancements.Add(line);
            }
            else if (topFeatures.Count < 20)
            {
                topFeatures.Add(line);
            }
            else
            {
                misc.Add(line);
            }
        }

        return BuildPremiumMegaPost(
            header,
            featureCount,
            topFeatures,
            enhancements,
            bugFixes,
            misc,
            url,
            VSCodeHashtag,
            MaxPremiumTweetLength);
    }

    private IReadOnlyList<string> FormatVSCodeChangelogThread(
        string fullSummary, int featureCount, DateTime startDate, DateTime endDate, string url, int maxPostLength, bool useXWeightedLength)
    {
        var dateLabel = startDate.Date == endDate.Date
            ? FormatShortDate(endDate)
            : $"{FormatShortDate(startDate)}-{FormatShortDate(endDate)}";
        var featureWord = featureCount == 1 ? "feature" : "features";
        var header = $"🚀 Insiders Update - {dateLabel}\n{featureCount} new {featureWord}";

        return BuildThreadFromSummaryLines(header, fullSummary, featureCount, url, VSCodeHashtag, maxPostLength, useXWeightedLength);
    }

    /// <summary>Formats a VS Code Insiders weekly recap as a single Premium X mega-post.</summary>
    public string FormatVSCodeWeeklyRecapPremiumPostForX(
        IReadOnlyList<VSCodeFeature> features,
        int featureCount,
        DateTimeOffset weekStartPacific,
        DateTimeOffset weekEndPacific,
        string url)
    {
        var dateRange = FormatDateRange(weekStartPacific, weekEndPacific);
        var featureWord = featureCount == 1 ? "feature" : "features";
        var header = $"🗓️ Weekly recap ({dateRange})\n✨ {featureCount} new {featureWord}";

        var topFeatures = new List<string>();
        var enhancements = new List<string>();
        var bugFixes = new List<string>();
        var misc = new List<string>();

        foreach (var feature in features)
        {
            var text = string.IsNullOrWhiteSpace(feature.Description)
                ? feature.Title
                : $"{feature.Title}: {feature.Description}";
            text = text.Trim();
            if (!FitsWithinLimit(text, 230, useXWeightedLength: true))
            {
                text = TruncateToLimit(text, 230, useXWeightedLength: true);
            }

            var line = $"{GetEmojiForFeature(text)} {text}";
            if (bugFixes.Count < 15 && ClassifyFeatureBucket(line) == FeatureBucket.BugFix)
            {
                bugFixes.Add(line);
            }
            else if (enhancements.Count < 20 && ClassifyFeatureBucket(line) == FeatureBucket.Enhancement)
            {
                enhancements.Add(line);
            }
            else if (topFeatures.Count < 20)
            {
                topFeatures.Add(line);
            }
            else
            {
                misc.Add(line);
            }
        }

        return BuildPremiumMegaPost(
            header,
            featureCount,
            topFeatures,
            enhancements,
            bugFixes,
            misc,
            url,
            VSCodeHashtag,
            MaxPremiumTweetLength);
    }

    /// <summary>Formats a VS Code Insiders weekly recap as a thread for X/Twitter.</summary>
    public async Task<IReadOnlyList<string>> FormatVSCodeWeeklyRecapThreadForXAsync(
        int featureCount,
        DateTimeOffset weekStartPacific,
        DateTimeOffset weekEndPacific,
        string url,
        Func<int, Task<string>> generateSummary)
        => await FormatVSCodeWeeklyRecapThreadAsync(featureCount, weekStartPacific, weekEndPacific, url, generateSummary, MaxTweetLength, useXWeightedLength: true);

    /// <summary>Formats a VS Code Insiders weekly recap as a thread for Bluesky.</summary>
    public async Task<IReadOnlyList<string>> FormatVSCodeWeeklyRecapThreadForBlueskyAsync(
        int featureCount,
        DateTimeOffset weekStartPacific,
        DateTimeOffset weekEndPacific,
        string url,
        Func<int, Task<string>> generateSummary)
        => await FormatVSCodeWeeklyRecapThreadAsync(featureCount, weekStartPacific, weekEndPacific, url, generateSummary, MaxBlueskyLength, useXWeightedLength: false);

    private async Task<IReadOnlyList<string>> FormatVSCodeWeeklyRecapThreadAsync(
        int featureCount,
        DateTimeOffset weekStartPacific,
        DateTimeOffset weekEndPacific,
        string url,
        Func<int, Task<string>> generateSummary,
        int maxPostLength,
        bool useXWeightedLength)
    {
        var dateRange = FormatDateRange(weekStartPacific, weekEndPacific);
        var featureWord = featureCount == 1 ? "feature" : "features";
        var header = $"🗓️ Weekly recap ({dateRange})\n✨ {featureCount} new {featureWord}";

        // Use a generous length to capture all highlights for thread splitting
        const int ThreadSummaryLength = 800;
        var fullSummary = await generateSummary(ThreadSummaryLength);

        if (string.IsNullOrWhiteSpace(fullSummary))
        {
            fullSummary = "✨ See the latest Insiders updates";
        }

        fullSummary = NormalizeGeneratedListText(fullSummary);

        return BuildThreadFromSummaryLines(header, fullSummary, featureCount, url, VSCodeHashtag, maxPostLength, useXWeightedLength);
    }

    // -----------------------------------------------------------------------
    // Thread assembly helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Splits a summary string (newline-separated feature lines) into a thread,
    /// using the first N lines as first-post highlights and grouping the rest.
    /// </summary>
    private IReadOnlyList<string> BuildThreadFromSummaryLines(
        string header, string summary, int totalCount, string link, string hashtag, int maxPostLength, bool useXWeightedLength)
    {
        summary = NormalizeGeneratedListText(summary);
        var topN = GetTopHighlightsCount();
        var maxLines = totalCount > 0 ? totalCount : int.MaxValue;

        // Split summary into individual lines, filtering out intro/commentary lines and
        // ensuring we never create more thread items than the actual detected feature count.
        var lines = summary
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => !string.IsNullOrWhiteSpace(l) &&
                        !l.StartsWith("...and ", StringComparison.OrdinalIgnoreCase) &&
                        !IsThreadIntroLine(l))
            .Take(maxLines)
            .ToList();

        var highlights = (IReadOnlyList<string>)lines.Take(topN).ToList();
        var followUpGroups = GroupFeatureLines(lines.Skip(topN).ToList(), 4);

        return AssembleThread(header, highlights, followUpGroups, totalCount, link, hashtag, maxPostLength, useXWeightedLength);
    }

    private static bool IsThreadIntroLine(string line)
    {
        if (line.EndsWith(":", StringComparison.Ordinal))
        {
            return true;
        }

        return line.StartsWith("summary", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("overview", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("highlights", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("this week", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("this release", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("in this update", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("vs code insiders", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Assembles a thread from its constituent parts.
    /// Structure: [first post] [follow-up posts...] [last post with link]
    /// </summary>
    private static IReadOnlyList<string> AssembleThread(
        string header,
        IReadOnlyList<string> highlights,
        IReadOnlyList<string> followUpGroups,
        int totalCount,
        string link,
        string hashtag,
        int maxPostLength,
        bool useXWeightedLength)
    {
        var singlePost = TryBuildSinglePost(header, highlights, followUpGroups, link, hashtag, maxPostLength, useXWeightedLength);
        if (singlePost != null)
        {
            return [singlePost];
        }

        var posts = new List<string>();

        // --- First post ---
        var highlightBlock = highlights.Count > 0 ? string.Join("\n", highlights) : string.Empty;
        var leadIn = "🧵 See thread below 👇";
        string firstPost;

        if (string.IsNullOrEmpty(highlightBlock))
        {
            firstPost = $"{header}\n\n{leadIn}";
        }
        else
        {
            firstPost = $"{header}\n\n{highlightBlock}\n\n{leadIn}";
        }

        // Trim to platform limit if needed
        if (!FitsWithinLimit(firstPost, maxPostLength, useXWeightedLength))
        {
            var suffix = "\n\n" + leadIn;
            var available = maxPostLength - GetTextLength(suffix, useXWeightedLength);
            firstPost = (available > 0 ? TruncateToLimit(firstPost, available, useXWeightedLength) : header) + suffix;
        }
        posts.Add(firstPost);

        // --- Follow-up posts ---
        foreach (var group in followUpGroups)
        {
            var post = FitsWithinLimit(group, maxPostLength, useXWeightedLength)
                ? group
                : TruncateToLimit(group, maxPostLength, useXWeightedLength);
            posts.Add(post);
        }

        // --- Last post: link + hashtag ---
        var lastPostContent = $"{link}\n\n{hashtag}";

        // Try to merge the last post into the previous follow-up post if there's room
        if (posts.Count >= 2)
        {
            var prevIndex = posts.Count - 1;
            var merged = $"{posts[prevIndex]}\n\n{lastPostContent}";
            if (FitsWithinLimit(merged, maxPostLength, useXWeightedLength))
            {
                posts[prevIndex] = merged;
            }
            else
            {
                posts.Add(lastPostContent);
            }
        }
        else
        {
            posts.Add(lastPostContent);
        }

        // --- Add thread position indicators (🧵 1/N) ---
        for (var i = 0; i < posts.Count; i++)
        {
            var indicator = $"🧵 {i + 1}/{posts.Count}";
            var postWithIndicator = $"{posts[i]}\n\n{indicator}";
            if (FitsWithinLimit(postWithIndicator, maxPostLength, useXWeightedLength))
            {
                posts[i] = postWithIndicator;
            }
        }

        return posts;
    }

    private static string? TryBuildSinglePost(
        string header,
        IReadOnlyList<string> highlights,
        IReadOnlyList<string> followUpGroups,
        string link,
        string hashtag,
        int maxPostLength,
        bool useXWeightedLength)
    {
        var bodyLines = highlights
            .Concat(followUpGroups
                .SelectMany(group => group
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => line.Trim())
                    .Where(line => !string.IsNullOrWhiteSpace(line))))
            .ToList();

        var sections = new List<string>();
        var normalizedHeader = RemoveTrailingThreadMarker(header);
        if (!string.IsNullOrWhiteSpace(normalizedHeader))
        {
            sections.Add(normalizedHeader);
        }

        if (bodyLines.Count > 0)
        {
            sections.Add(string.Join("\n", bodyLines));
        }

        if (!string.IsNullOrWhiteSpace(link))
        {
            sections.Add(link);
        }

        if (!string.IsNullOrWhiteSpace(hashtag))
        {
            sections.Add(hashtag);
        }

        var candidate = string.Join("\n\n", sections.Where(section => !string.IsNullOrWhiteSpace(section)));
        return !string.IsNullOrWhiteSpace(candidate) && FitsWithinLimit(candidate, maxPostLength, useXWeightedLength)
            ? candidate
            : null;
    }

    private static string RemoveTrailingThreadMarker(string text)
    {
        var lines = text
            .Split('\n', StringSplitOptions.None)
            .Select(RemoveTrailingThreadMarkerFromLine);

        return string.Join("\n", lines).Trim();
    }

    private static string RemoveTrailingThreadMarkerFromLine(string line)
    {
        const string threadMarker = "🧵";
        var trimmedLine = line.TrimEnd();
        if (!trimmedLine.EndsWith(threadMarker, StringComparison.Ordinal))
        {
            return line;
        }

        var markerIndex = trimmedLine.LastIndexOf(threadMarker, StringComparison.Ordinal);
        return markerIndex >= 0
            ? trimmedLine[..markerIndex].TrimEnd()
            : trimmedLine;
    }

    /// <summary>Extracts a plain list of emoji-prefixed feature strings from HTML release content.</summary>
    private static List<string> ExtractFeatureList(string htmlContent)
    {
        var decoded = HttpUtility.HtmlDecode(htmlContent);
        var features = new List<string>();

        // New SDK format (v0.1.29+): major features are in <h3> headings
        var h3Pattern = @"<h3[^>]*>(.*?)</h3>";
        var h3Matches = Regex.Matches(decoded, h3Pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        foreach (Match h3Match in h3Matches)
        {
            var text = StripHtml(h3Match.Groups[1].Value).Trim();
            if (string.IsNullOrWhiteSpace(text) ||
                text.StartsWith("Other changes", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("New contributor", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("What", StringComparison.OrdinalIgnoreCase))
                continue;
            features.Add(NormalizeListItem($"{GetEmojiForFeature(text)} {text}"));
        }

        const string listItemPattern = @"<li[^>]*>(.*?)</li>";
        var matches = Regex.Matches(decoded, listItemPattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);

        foreach (Match match in matches)
        {
            var text = StripHtml(match.Groups[1].Value).Trim();
            // Remove PR/author references
            text = Regex.Replace(text, @"(?:by\s+@\S+\s+)?(?:in\s+)?#\d+$|by\s+@\S+\s+in\s+#\d+",
                string.Empty, RegexOptions.IgnoreCase).Trim();
            // Strip new-format prefixes (feature:, improvement:, bugfix:)
            text = Regex.Replace(text, @"^(?:feature|improvement|bugfix|chore|docs):\s*", "", RegexOptions.IgnoreCase).Trim();

            if (!string.IsNullOrWhiteSpace(text)
                && !text.StartsWith("Full Changelog", StringComparison.OrdinalIgnoreCase)
                && !text.Contains("made their first contribution", StringComparison.OrdinalIgnoreCase)
                && !text.StartsWith("Generated by", StringComparison.OrdinalIgnoreCase))
            {
                features.Add(NormalizeListItem($"{GetEmojiForFeature(text)} {text}"));
            }
        }

        if (features.Count == 0)
        {
            var plainText = StripHtml(decoded).Trim();
            if (!string.IsNullOrWhiteSpace(plainText))
            {
                features.AddRange(
                    plainText.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(l => l.Trim())
                        .Where(l => !string.IsNullOrWhiteSpace(l) && !IsDateLine(l))
                        .Select(l => NormalizeListItem($"{GetEmojiForFeature(l)} {l}")));
            }
        }

        return features;
    }

    /// <summary>Groups a flat list of lines into chunks of <paramref name="linesPerGroup"/> each.</summary>
    private static IReadOnlyList<string> GroupFeatureLines(IList<string> lines, int linesPerGroup)
    {
        if (lines.Count == 0) return [];

        var groups = new List<string>();
        for (var i = 0; i < lines.Count; i += linesPerGroup)
        {
            groups.Add(string.Join("\n", lines.Skip(i).Take(linesPerGroup)));
        }
        return groups;
    }

    /// <summary>
    /// Packs items into posts greedily, filling each post as close to
    /// maxPostLength as possible before starting a new one.
    /// Reserves space for thread indicator (~12 chars) appended later.
    /// </summary>
    private static IReadOnlyList<string> PackItemsIntoPosts(IList<string> items, int maxPostLength, bool useXWeightedLength)
    {
        if (items.Count == 0) return [];

        const int threadIndicatorReserve = 14; // "\n\n🧵 XX/XX"
        var effectiveMax = maxPostLength - threadIndicatorReserve;
        var posts = new List<string>();
        var currentLines = new List<string>();
        var currentLength = 0;

        foreach (var item in items)
        {
            // +1 for the newline separator between lines
            var candidateText = currentLines.Count > 0 ? $"\n{item}" : item;
            var addedLength = GetTextLength(candidateText, useXWeightedLength);

            if (currentLength + addedLength > effectiveMax && currentLines.Count > 0)
            {
                // Current post is full, start a new one
                posts.Add(string.Join("\n", currentLines));
                currentLines = [];
                currentLength = 0;
                addedLength = GetTextLength(item, useXWeightedLength);
            }

            currentLines.Add(item);
            currentLength += addedLength;
        }

        if (currentLines.Count > 0)
        {
            posts.Add(string.Join("\n", currentLines));
        }

        return posts;
    }

    private string BuildPremiumMegaPost(
        string header,
        int totalCount,
        IReadOnlyList<string> rankedItems,
        string link,
        string hashtag,
        int maxLength)
    {
        var topCount = Math.Clamp(GetTopHighlightsCount() * 2, 5, 10);
        var topFeatures = rankedItems.Take(topCount).ToList();
        var remaining = rankedItems.Skip(topCount).ToList();

        var enhancements = new List<string>();
        var bugFixes = new List<string>();
        var misc = new List<string>();

        foreach (var item in remaining)
        {
            var bucket = ClassifyFeatureBucket(item);
            if (bucket == FeatureBucket.BugFix)
            {
                bugFixes.Add(item);
            }
            else if (bucket == FeatureBucket.Enhancement)
            {
                enhancements.Add(item);
            }
            else
            {
                misc.Add(item);
            }
        }

        var featureLabel = totalCount == 1 ? "feature & enhancement" : "features & enhancements";
        var sb = new StringBuilder();
        sb.AppendLine(header);
        sb.AppendLine();
        sb.AppendLine($"{totalCount} {featureLabel} in this release");

        AppendSection(sb, "Top features", topFeatures, maxLength);
        AppendSection(sb, "Enhancements", enhancements, maxLength);
        AppendSection(sb, "Bug fixes", bugFixes, maxLength);
        AppendSection(sb, "Misc", misc, maxLength);

        var footer = $"\n{link}\n\n{hashtag}";
        if (GetTextLength(sb.ToString(), useXWeightedLength: true) + GetTextLength(footer, useXWeightedLength: true) <= maxLength)
        {
            sb.Append(footer);
        }
        else
        {
            var budget = maxLength - GetTextLength(sb.ToString(), useXWeightedLength: true) - GetTextLength($"\n\n{hashtag}", useXWeightedLength: true);
            if (budget > 15)
            {
                var shortenedLink = FitsWithinLimit(link, budget, useXWeightedLength: true)
                    ? link
                    : TruncateToLimit(link, budget, useXWeightedLength: true);
                sb.Append("\n").Append(shortenedLink).Append("\n\n").Append(hashtag);
            }
            else
            {
                sb.Append("\n\n").Append(hashtag);
            }
        }

        var post = sb.ToString().Trim();
        if (!FitsWithinLimit(post, maxLength, useXWeightedLength: true))
        {
            post = TruncateToLimit(post, maxLength, useXWeightedLength: true);
        }

        return post;
    }

    private string BuildPremiumMegaPost(
        string header,
        int totalCount,
        IReadOnlyList<string> topFeatures,
        IReadOnlyList<string> enhancements,
        IReadOnlyList<string> bugFixes,
        IReadOnlyList<string> misc,
        string link,
        string hashtag,
        int maxLength)
    {
        var featureLabel = totalCount == 1 ? "feature & enhancement" : "features & enhancements";
        var sb = new StringBuilder();
        sb.AppendLine(header);
        sb.AppendLine();
        sb.AppendLine($"{totalCount} {featureLabel} in this release");

        AppendSection(sb, "Top features", topFeatures, maxLength);
        AppendSection(sb, "Enhancements", enhancements, maxLength);
        AppendSection(sb, "Bug fixes", bugFixes, maxLength);
        AppendSection(sb, "Misc", misc, maxLength);

        var footer = $"\n{link}\n\n{hashtag}";
        if (GetTextLength(sb.ToString(), useXWeightedLength: true) + GetTextLength(footer, useXWeightedLength: true) <= maxLength)
        {
            sb.Append(footer);
        }
        else
        {
            var budget = maxLength - GetTextLength(sb.ToString(), useXWeightedLength: true) - GetTextLength($"\n\n{hashtag}", useXWeightedLength: true);
            if (budget > 15)
            {
                var shortenedLink = FitsWithinLimit(link, budget, useXWeightedLength: true)
                    ? link
                    : TruncateToLimit(link, budget, useXWeightedLength: true);
                sb.Append("\n").Append(shortenedLink).Append("\n\n").Append(hashtag);
            }
            else
            {
                sb.Append("\n\n").Append(hashtag);
            }
        }

        var post = sb.ToString().Trim();
        if (!FitsWithinLimit(post, maxLength, useXWeightedLength: true))
        {
            post = TruncateToLimit(post, maxLength, useXWeightedLength: true);
        }

        return post;
    }

    private static void AppendSection(StringBuilder sb, string title, IReadOnlyList<string> items, int maxLength)
    {
        if (items.Count == 0 || GetTextLength(sb.ToString(), useXWeightedLength: true) >= maxLength - 32)
        {
            return;
        }

        sb.AppendLine();
        sb.AppendLine($"{title}:");

        foreach (var item in items)
        {
            var line = NormalizeListItem(item);
            if (GetTextLength(sb.ToString(), useXWeightedLength: true) + GetTextLength(line + "\n", useXWeightedLength: true) > maxLength)
            {
                sb.AppendLine("• ...and more");
                return;
            }

            sb.AppendLine(line);
        }
    }

    private enum FeatureBucket
    {
        TopFeature,
        Enhancement,
        BugFix,
        Misc
    }

    private static FeatureBucket ClassifyFeatureBucket(string text)
    {
        var value = text.ToLowerInvariant();

        if (value.Contains("🐛") || value.Contains("fix") || value.Contains("bug") || value.Contains("regression") || value.Contains("error"))
        {
            return FeatureBucket.BugFix;
        }

        if (value.Contains("⚡") || value.Contains("improv") || value.Contains("enhanc") || value.Contains("performance") || value.Contains("faster") || value.Contains("optimiz"))
        {
            return FeatureBucket.Enhancement;
        }

        if (value.Contains("misc") || value.Contains("docs") || value.Contains("readme") || value.Contains("housekeeping") || value.Contains("chore"))
        {
            return FeatureBucket.Misc;
        }

        return FeatureBucket.TopFeature;
    }
}
