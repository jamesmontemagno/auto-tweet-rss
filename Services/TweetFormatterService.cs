using System.Text.RegularExpressions;
using System.Web;
using Microsoft.Extensions.Logging;

namespace AutoTweetRss.Services;

public class TweetFormatterService
{
    private readonly ILogger<TweetFormatterService> _logger;
    private readonly ReleaseSummarizerService? _releaseSummarizer;

    // Twitter limits
    private const int MaxTweetLength = 280;
    private const int UrlLength = 23; // t.co shortens all URLs to 23 chars
    private const string Hashtag = "#GitHubCopilotCLI";
    private const string SdkHashtag = "#GitHubCopilotSDK";
    
    // Emojis for different content types
    private const string ReleaseEmoji = "🚀";
    private const string FeatureEmoji = "✨";
    private const string PerformanceEmoji = "⚡";
    private const string BugFixEmoji = "🐛";
    private const string SecurityEmoji = "🔒";
    private const string DocsEmoji = "📖";

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
        
        var availableForFeatures = MaxTweetLength - header.Length - UrlLength - hashtagLength - newlines;
        
        // Get AI-generated summary
        var features = await _releaseSummarizer.SummarizeReleaseAsync(entry.Title, entry.Content, availableForFeatures);
        
        // Build the tweet
        var tweet = $"{header}\n\n{features}\n\n{entry.Link}\n\n{Hashtag}";
        
        // Final safety check - truncate if needed
        if (tweet.Length > MaxTweetLength)
        {
            var overflow = tweet.Length - MaxTweetLength;
            features = features[..^(overflow + 3)] + "...";
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
        
        var availableForSummary = MaxTweetLength - header.Length - UrlLength - hashtagLength - newlines;
        
        // Get AI-generated summary
        var summary = await _releaseSummarizer.SummarizeReleaseAsync(entry.Title, entry.Content, availableForSummary);
        
        // Build the tweet
        var tweet = $"{header}\n\n{summary}\n\n{entry.Link}\n\n{SdkHashtag}";
        
        // Final safety check - truncate if needed
        if (tweet.Length > MaxTweetLength)
        {
            var overflow = tweet.Length - MaxTweetLength;
            summary = summary[..^(overflow + 3)] + "...";
            tweet = $"{header}\n\n{summary}\n\n{entry.Link}\n\n{SdkHashtag}";
        }
        
        return tweet;
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

    private string ExtractFeatures(string htmlContent, int maxLength)
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
        
        foreach (var feature in features.Take(3)) // Max 3 features
        {
            var featureWithNewline = result.Count > 0 ? $"\n{feature}" : feature;
            
            if (currentLength + featureWithNewline.Length <= maxLength)
            {
                result.Add(feature);
                currentLength += featureWithNewline.Length;
            }
            else
            {
                // Try to fit a truncated version
                var available = maxLength - currentLength - (result.Count > 0 ? 1 : 0) - 3; // -3 for "..."
                if (available > 20) // Only add if we can show at least 20 chars
                {
                    result.Add(feature[..available] + "...");
                }
                break;
            }
        }
        
        return string.Join("\n", result);
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
