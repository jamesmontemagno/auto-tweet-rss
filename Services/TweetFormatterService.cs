using System.Globalization;
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
    private const int UrlLength = 23; // t.co shortens all URLs to 23 chars
    private const string Hashtag = "#GitHubCopilotCLI";
    private const string SdkHashtag = "#GitHubCopilotSDK";
    
    // Truncation constants
    private const int MinTruncatedLineLength = 10; // Minimum meaningful characters to show after truncation
    
    // Emojis for different content types
    private const string ReleaseEmoji = "🚀";
    private const string FeatureEmoji = "✨";
    private const string PerformanceEmoji = "⚡";
    private const string BugFixEmoji = "🐛";
    private const string SecurityEmoji = "🔒";
    private const string DocsEmoji = "📖";
    
    // Compiled regex pattern for "...and X more" suffix matching
    [GeneratedRegex(@"\n?\.\.\.and \d+ more$")]
    private static partial Regex MoreIndicatorPattern();

    public TweetFormatterService(ILogger<TweetFormatterService> logger, ReleaseSummarizerService? releaseSummarizer = null)
    {
        _logger = logger;
        _releaseSummarizer = releaseSummarizer;
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
        var header = $"{ReleaseEmoji} Copilot CLI v{entry.Title} released!";
        var newlines = 6; // 2 between each section
        var hashtagLength = Hashtag.Length;
        
        var buffer = 6; // Small buffer to avoid edge cases
        var availableForFeatures = MaxTweetLength - header.Length - UrlLength - hashtagLength - newlines - buffer;
        
        // Get AI-generated summary with CLI-specific prompt
        var features = await _releaseSummarizer.SummarizeReleaseAsync(entry.Title, entry.Content, availableForFeatures, feedType: "cli");
        
        // Build the tweet
        var tweet = $"{header}\n\n{features}\n\n{entry.Link}\n\n{Hashtag}";
        
        // Final safety check - try to preserve "...and X more" if truncation needed
        if (tweet.Length > MaxTweetLength)
        {
            features = TruncatePreservingMoreIndicator(features, tweet.Length - MaxTweetLength);
            tweet = $"{header}\n\n{features}\n\n{entry.Link}\n\n{Hashtag}";
        }
        
        return tweet;
    }

    public string FormatTweet(ReleaseEntry entry)
    {
        // Calculate available space
        // Format: "{header}\n\n{features}\n\n{url}\n\n{hashtag}"
        var header = $"{ReleaseEmoji} Copilot CLI v{entry.Title} released!";
        var newlines = 6; // 2 between each section
        var hashtagLength = Hashtag.Length;
        
        var availableForFeatures = MaxTweetLength - header.Length - UrlLength - hashtagLength - newlines;
        
        // Extract and format features from HTML content
        var features = ExtractFeatures(entry.Content, availableForFeatures);
        
        // Build the tweet
        var tweet = $"{header}\n\n{features}\n\n{entry.Link}\n\n{Hashtag}";
        
        // Final safety check - truncate if needed (shouldn't happen with proper calculation)
        if (tweet.Length > MaxTweetLength)
        {
            // Truncate features to fit
            var overflow = tweet.Length - MaxTweetLength;
            features = features[..^(overflow + 3)] + "...";
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
        var header = $"{ReleaseEmoji} Copilot SDK {entry.Title} released!";
        var newlines = 6; // 2 between each section
        var hashtagLength = SdkHashtag.Length;
        var buffer = 6; // Small buffer to avoid edge cases
        
        var availableForSummary = MaxTweetLength - header.Length - UrlLength - hashtagLength - newlines - buffer;
        
        // Get AI-generated summary with SDK-specific prompt
        var summary = await _releaseSummarizer.SummarizeReleaseAsync(entry.Title, entry.Content, availableForSummary, feedType: "sdk");
        
        // Build the tweet
        var tweet = $"{header}\n\n{summary}\n\n{entry.Link}\n\n{SdkHashtag}";
        
        // Final safety check - try to preserve "...and X more" if truncation needed
        if (tweet.Length > MaxTweetLength)
        {
            summary = TruncatePreservingMoreIndicator(summary, tweet.Length - MaxTweetLength);
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
            $"🗓️ Weekly Copilot CLI recap ({dateRange})",
            $"🚀 Releases: {releaseCount} {releaseWord}",
            $"🛠️ Improvements: {improvementCount} {improvementWord}");

        var highlightsPrefix = "Highlights:\n";
        var url = "https://github.com/github/copilot-cli/releases";
        var newlines = 7; // 2 after header, 1 after prefix, 2 after highlights, 2 after URL
        var buffer = 4; // Small buffer to avoid edge cases
        var availableForHighlights = MaxTweetLength - header.Length - highlightsPrefix.Length - UrlLength - Hashtag.Length - newlines - buffer;
        if (availableForHighlights < 0)
        {
            availableForHighlights = 0;
        }

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

        var tweet = $"{header}\n\n{highlightsPrefix}{highlights}\n\n{url}\n\n{Hashtag}";

        if (tweet.Length > MaxTweetLength)
        {
            var overflow = tweet.Length - MaxTweetLength;
            highlights = TruncatePreservingMoreIndicator(highlights, overflow);
            tweet = $"{header}\n\n{highlightsPrefix}{highlights}\n\n{url}\n\n{Hashtag}";
        }

        return tweet;
    }

    public string FormatVSCodeChangelogTweetAsync(string summary, DateTime startDate, DateTime endDate, string url)
    {
        var startLabel = FormatShortDate(startDate);
        var endLabel = FormatShortDate(endDate);
        var dateLabel = startDate.Date == endDate.Date ? startLabel : $"{startLabel}-{endLabel}";
        var header = $"🆕 VS Code Updates ({dateLabel})";

        if (string.IsNullOrWhiteSpace(summary))
        {
            summary = "Highlights in recent updates.";
        }

        var newlines = 4; // 2 between header/summary and 2 between summary/url
        var buffer = 4; // Small buffer to avoid edge cases
        var availableForSummary = MaxTweetLength - header.Length - UrlLength - newlines - buffer;
        if (availableForSummary < 0)
        {
            availableForSummary = 0;
        }

        if (summary.Length > availableForSummary)
        {
            if (availableForSummary > MinTruncatedLineLength)
            {
                var overflow = summary.Length - availableForSummary;
                summary = TruncatePreservingMoreIndicator(summary, overflow);
            }
            else
            {
                summary = "See latest updates.";
            }
        }

        var tweet = $"{header}\n\n{summary}\n\n{url}";

        if (tweet.Length > MaxTweetLength)
        {
            var overflow = tweet.Length - MaxTweetLength;
            summary = TruncatePreservingMoreIndicator(summary, overflow);
            tweet = $"{header}\n\n{summary}\n\n{url}";
        }

        return tweet;
    }

    /// <summary>
    /// Truncates the summary while trying to preserve the "...and X more" indicator
    /// </summary>
    private static string TruncatePreservingMoreIndicator(string summary, int overflow)
    {
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
                
                if (newSummary.Length <= summary.Length - overflow)
                {
                    return newSummary;
                }
            }
            
            // If we still don't fit, truncate the last remaining line
            if (lines.Count == 1)
            {
                var targetLength = summary.Length - overflow - moreSuffix.Length - 3; // -3 for "..."
                if (targetLength > MinTruncatedLineLength)
                {
                    return lines[0][..targetLength] + "..." + moreSuffix;
                }
            }
        }
        
        // Fallback to simple truncation if no "more" pattern found
        return summary[..^(overflow + 3)] + "...";
    }

    public string FormatSdkTweet(ReleaseEntry entry)
    {
        // Calculate available space
        // Format: "{header}\n\n{summary}\n\n{url}\n\n{hashtag}"
        var header = $"{ReleaseEmoji} Copilot SDK {entry.Title} released!";
        var newlines = 6; // 2 between each section
        var hashtagLength = SdkHashtag.Length;
        
        var availableForSummary = MaxTweetLength - header.Length - UrlLength - hashtagLength - newlines;
        
        // Extract and summarize changes from SDK content
        var summary = ExtractSdkSummary(entry.Content, availableForSummary);
        
        // Build the tweet
        var tweet = $"{header}\n\n{summary}\n\n{entry.Link}\n\n{SdkHashtag}";
        
        // Final safety check - truncate if needed
        if (tweet.Length > MaxTweetLength)
        {
            // Truncate summary to fit
            var overflow = tweet.Length - MaxTweetLength;
            summary = summary[..^(overflow + 3)] + "...";
            tweet = $"{header}\n\n{summary}\n\n{entry.Link}\n\n{SdkHashtag}";
        }
        
        return tweet;
    }

    private string ExtractFeatures(string htmlContent, int maxLength, int maxItems = 3)
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
                
                foreach (var line in lines.Take(3))
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
            
            if (currentLength + featureWithNewline.Length <= maxLength)
            {
                result.Add(feature);
                currentLength += featureWithNewline.Length;
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
            if (currentLength + indicator.Length + 1 <= maxLength)
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
        
        // Extract list items from "What's Changed" section
        var listItemPattern = @"<li[^>]*>(.*?)</li>";
        var matches = Regex.Matches(decoded, listItemPattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        
        var changes = new List<string>();
        
        foreach (Match match in matches)
        {
            var text = StripHtml(match.Groups[1].Value).Trim();
            
            // Clean up the text - remove PR references and author mentions
            text = Regex.Replace(text, @"(?:by\s+@\S+\s+)?(?:in\s+)?#\d+$|by\s+@\S+\s+in\s+#\d+", "", RegexOptions.IgnoreCase).Trim();
            
            if (!string.IsNullOrWhiteSpace(text) && !text.StartsWith("Full Changelog"))
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
        var maxItems = 3; // Limit to top 3 items
        
        foreach (var change in changes.Take(maxItems))
        {
            var changeWithNewline = result.Count > 0 ? $"\n{change}" : change;
            
            if (currentLength + changeWithNewline.Length <= maxLength)
            {
                result.Add(change);
                currentLength += changeWithNewline.Length;
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
                if (currentLength + indicator.Length + 1 <= maxLength)
                {
                    result.Add(indicator);
                }
            }
        }
        
        return string.Join("\n", result);
    }
}
