using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;
using System.Net;

namespace AutoTweetRss.Services;

/// <summary>
/// Service for generating AI-powered summaries of release notes
/// </summary>
public class ReleaseSummarizerService
{
    private const int DefaultThreadPlanTimeoutSeconds = 60;
    private readonly IChatClient _chatClient;
    private readonly ILogger<ReleaseSummarizerService> _logger;
    
    // Compiled regex patterns for better performance
    private static readonly Regex ListItemPattern = new(@"<li[^>]*>(.*?)</li>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex HtmlTagPattern = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex WhitespacePattern = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex BulletLinePattern = new(@"^\s*[-*]\s+", RegexOptions.Compiled);
    private static readonly Regex MoreIndicatorLinePattern = new(@"^\s*\.\.\.and\s+(\d+)\s+more\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex EmojiFeatureLinePattern = new(@"^\s*[\p{So}\p{Sk}]", RegexOptions.Compiled);
    private static readonly Regex PrefixedListLinePattern = new(@"^\s*(?:[-*•]\s+|[\p{So}\p{Sk}]\s+)", RegexOptions.Compiled);
    private static readonly Regex CategoryHeaderLinePattern = new(@"^\s*(?:[\p{So}\p{Sk}]\s+)?[^\r\n]+:\s*$", RegexOptions.Compiled);

    public ReleaseSummarizerService(
        ILogger<ReleaseSummarizerService> logger,
        string endpoint,
        string apiKey,
        string deploymentModel)
    {
        _logger = logger;
        _chatClient = CreateClient(endpoint, apiKey, deploymentModel);
    }

    private IChatClient CreateClient(string endpoint, string apiKey, string deploymentModel)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(apiKey);
        ArgumentNullException.ThrowIfNull(deploymentModel);

        var azureClient = new AzureOpenAIClient(
            new Uri(endpoint),
            new AzureKeyCredential(apiKey));
        
        var chatClient = azureClient.GetChatClient(deploymentModel);
        return chatClient.AsIChatClient();
    }

    /// <summary>
    /// Generates a concise, emoji-enhanced summary of release notes that fits within tweet limits
    /// </summary>
    /// <param name="releaseTitle">The release version/title</param>
    /// <param name="releaseContent">The full release notes content</param>
    /// <param name="maxLength">Maximum length of the summary in characters</param>
    /// <param name="feedType">Type of feed - "cli" or "sdk" - defaults to "sdk" for backwards compatibility</param>
    /// <param name="cancellationToken">Cancellation token for the async operation</param>
    /// <returns>A well-formatted summary with emojis highlighting top features</returns>
    public async Task<string> SummarizeReleaseAsync(string releaseTitle, string releaseContent, int maxLength, string feedType = "sdk", CancellationToken cancellationToken = default)
    {
        try
        {
            var totalItemCount = CountItemsInRelease(releaseContent, feedType);
            var systemPrompt = ReleaseSummarizerCliSdkPrompts.GetReleaseSummarySystemPrompt();
            var userPrompt = BuildUserPrompt(releaseTitle, releaseContent, maxLength, totalItemCount, feedType);
            
            var messages = new List<Microsoft.Extensions.AI.ChatMessage>
            {
                new(ChatRole.System, systemPrompt),
                new(ChatRole.User, userPrompt)
            };

            _logger.LogInformation("Requesting AI summary for {FeedType} release: {Title} ({TotalItems} items)", feedType, releaseTitle, totalItemCount);
            
            // Use GetResponseAsync from version 10.2 API
            var response = await _chatClient.GetResponseAsync(messages, cancellationToken: cancellationToken);
            var summary = response.Messages.LastOrDefault()?.Text?.Trim() ?? string.Empty;
            summary = NormalizeSummaryListFormatting(summary, feedType);
            summary = NormalizeMoreIndicator(summary, totalItemCount);
            
            _logger.LogInformation("Generated {FeedType} summary ({Length} chars): {Summary}", feedType, summary.Length, summary);
            
            return summary;
        }
        catch (Exception ex)
        {
            var errorCode = (ex as Azure.RequestFailedException)?.ErrorCode ?? "N/A";
            var statusCode = (ex as Azure.RequestFailedException)?.Status.ToString() ?? "N/A";
            _logger.LogError(ex,
                "Error generating AI summary for {FeedType} release: {Title}. MaxLength={MaxLength}, StatusCode={StatusCode}, ErrorCode={ErrorCode}, Message={ErrorMessage}",
                feedType, releaseTitle, maxLength, statusCode, errorCode, ex.Message);
            throw;
        }
    }

    private static readonly Regex H3HeadingPattern = new(@"<h3[^>]*>(.*?)</h3>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static int CountItemsInRelease(string htmlContent, string feedType = "sdk")
    {
        try
        {
            // Decode HTML entities
            var decoded = WebUtility.HtmlDecode(htmlContent);
            
            // Check if we're past the "New Contributors" section - don't count those items
            var newContributorsIndex = decoded.IndexOf("New Contributors", StringComparison.OrdinalIgnoreCase);
            var contentToCount = newContributorsIndex >= 0 ? decoded[..newContributorsIndex] : decoded;

            var count = 0;

            // New SDK format (v0.1.29+): major features are described in <h3> headings
            var h3Matches = H3HeadingPattern.Matches(contentToCount);
            foreach (Match h3Match in h3Matches)
            {
                var text = StripHtml(h3Match.Groups[1].Value).Trim();
                if (!string.IsNullOrWhiteSpace(text) &&
                    !text.StartsWith("Other changes", StringComparison.OrdinalIgnoreCase) &&
                    !text.StartsWith("New contributor", StringComparison.OrdinalIgnoreCase) &&
                    !text.StartsWith("What", StringComparison.OrdinalIgnoreCase))
                {
                    count++;
                }
            }
            
            // Extract list items from HTML
            var matches = ListItemPattern.Matches(contentToCount);
            
            foreach (Match match in matches)
            {
                var text = StripHtml(match.Groups[1].Value).Trim();
                // Skip empty items, "Full Changelog" entries, contributor mentions and "Generated by"
                if (!string.IsNullOrWhiteSpace(text) && 
                    !text.StartsWith("Full Changelog", StringComparison.OrdinalIgnoreCase) &&
                    !text.Contains("made their first contribution", StringComparison.OrdinalIgnoreCase) &&
                    !text.StartsWith("Generated by", StringComparison.OrdinalIgnoreCase))
                {
                    count++;
                }
            }

            if (count > 0)
            {
                return count;
            }

            // Fallback for plain-text bullet input (e.g., "- title: description")
            var lines = contentToCount.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                if (!BulletLinePattern.IsMatch(line))
                {
                    continue;
                }

                var text = BulletLinePattern.Replace(line, string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(text) &&
                    !text.StartsWith("Full Changelog", StringComparison.OrdinalIgnoreCase) &&
                    !text.Contains("made their first contribution", StringComparison.OrdinalIgnoreCase))
                {
                    count++;
                }
            }
            
            return count;
        }
        catch (Exception)
        {
            // If parsing fails, return 0 to avoid breaking the summary
            // We gracefully fall back to 0 without logging since this is non-critical
            return 0;
        }
    }

    private static string StripHtml(string html)
    {
        // Remove HTML tags
        var withoutTags = HtmlTagPattern.Replace(html, " ");
        // Normalize whitespace
        var normalized = WhitespacePattern.Replace(withoutTags, " ");
        return normalized.Trim();
    }

    private static readonly Regex ContributorLinePattern = new(
        @"(?:by\s+@\S+\s*(?:in\s+)?)?(?:https?://\S+/pull/\d+|#\d+)\s*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private const int MaxAiContentLength = 4000;

    /// <summary>
    /// Strips HTML, removes contributor/PR noise, truncates "New Contributors" section,
    /// and caps total length to keep prompts within model token limits.
    /// </summary>
    private static string PrepareContentForAi(string rawContent)
    {
        var decoded = WebUtility.HtmlDecode(rawContent);

        // Chop off "New Contributors" and "Full Changelog" sections
        var cutoff = decoded.IndexOf("New Contributors", StringComparison.OrdinalIgnoreCase);
        if (cutoff < 0)
            cutoff = decoded.IndexOf("Full Changelog", StringComparison.OrdinalIgnoreCase);
        if (cutoff > 0)
            decoded = decoded[..cutoff];

        // Strip HTML tags
        var cleaned = HtmlTagPattern.Replace(decoded, " ");

        // Remove contributor/PR references (e.g., "by @user in #123")
        cleaned = ContributorLinePattern.Replace(cleaned, " ");

        // Normalize whitespace and blank lines
        cleaned = WhitespacePattern.Replace(cleaned, " ").Trim();

        // Cap length to avoid exceeding model token limits
        if (cleaned.Length > MaxAiContentLength)
        {
            cleaned = cleaned[..MaxAiContentLength] + "...[truncated]";
        }

        return cleaned;
    }

    private static string NormalizeMoreIndicator(string summary, int totalItemCount)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            return summary;
        }

        var normalized = summary.Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = normalized.Split('\n', StringSplitOptions.None).ToList();

        if (lines.Count == 0)
        {
            return summary;
        }

        var lastLine = lines[^1].Trim();
        if (!MoreIndicatorLinePattern.IsMatch(lastLine))
        {
            return normalized;
        }

        // Only remove a false "...and X more" when shown features >= totalItemCount.
        // Never recalculate the number — trust the AI's judgment on consolidation.
        var linesBeforeIndicator = lines.Take(lines.Count - 1).ToList();
        var shownFeatureCount = linesBeforeIndicator.Count(line => EmojiFeatureLinePattern.IsMatch(line));

        if (shownFeatureCount == 0)
        {
            shownFeatureCount = linesBeforeIndicator.Count(line => !string.IsNullOrWhiteSpace(line));
        }

        if (shownFeatureCount >= totalItemCount || totalItemCount <= 0)
        {
            lines.RemoveAt(lines.Count - 1);
            return string.Join("\n", lines).TrimEnd();
        }

        return normalized;
    }

    private static string NormalizeSummaryListFormatting(string summary, string feedType)
    {
        if (string.IsNullOrWhiteSpace(summary) || string.Equals(feedType, "cli-paragraph", StringComparison.OrdinalIgnoreCase))
        {
            return summary;
        }

        var normalized = summary.Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = normalized.Split('\n', StringSplitOptions.None);
        var firstListLineIndex = FindFirstListLineIndex(lines);

        if (firstListLineIndex < 0)
        {
            return normalized;
        }

        for (var index = firstListLineIndex; index < lines.Length; index++)
        {
            var currentLine = lines[index];
            var trimmed = currentLine.Trim();

            if (string.IsNullOrWhiteSpace(trimmed) || MoreIndicatorLinePattern.IsMatch(trimmed) || CategoryHeaderLinePattern.IsMatch(trimmed))
            {
                continue;
            }

            lines[index] = NormalizeListLine(trimmed);
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

                    if (MoreIndicatorLinePattern.IsMatch(nextTrimmed))
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

    private static string NormalizeListLine(string trimmed)
    {
        if (trimmed.StartsWith("• ", StringComparison.Ordinal))
        {
            return trimmed;
        }

        if (trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed.StartsWith("* ", StringComparison.Ordinal))
        {
            return $"• {trimmed[2..].TrimStart()}";
        }

        if (PrefixedListLinePattern.IsMatch(trimmed))
        {
            return $"• {trimmed}";
        }

        return $"• {trimmed}";
    }

    private static string BuildUserPrompt(string releaseTitle, string releaseContent, int maxLength, int totalItemCount, string feedType)
    {
        if (feedType.StartsWith("vscode", StringComparison.OrdinalIgnoreCase))
        {
            var estimatedCharsPerItem = 40;
            var reserveForSuffix = totalItemCount > 5 ? 15 : 0;
            var maxItems = Math.Max(3, (maxLength - reserveForSuffix) / estimatedCharsPerItem);
            maxItems = Math.Min(maxItems, 10);
            if (totalItemCount > 0)
            {
                maxItems = Math.Min(maxItems, totalItemCount);
            }

            if (feedType == "vscode-week")
            {
                return ReleaseSummarizerVSCodePrompts.BuildVSCodeWeeklyUserPrompt(releaseTitle, releaseContent, maxLength, totalItemCount, maxItems);
            }
            if (feedType == "vscode-ai")
            {
                return ReleaseSummarizerVSCodePrompts.BuildVSCodeAiUserPrompt(releaseTitle, releaseContent, maxLength, totalItemCount, Math.Min(maxItems, 8));
            }
            if (feedType == "vscode-week-ai")
            {
                return ReleaseSummarizerVSCodePrompts.BuildVSCodeAiWeeklyUserPrompt(releaseTitle, releaseContent, maxLength, totalItemCount, Math.Min(maxItems, 8));
            }

            return ReleaseSummarizerVSCodePrompts.BuildVSCodeUserPrompt(releaseTitle, releaseContent, maxLength, totalItemCount, maxItems);
        }

        return ReleaseSummarizerCliSdkPrompts.BuildCliOrSdkUserPrompt(releaseTitle, releaseContent, maxLength, totalItemCount, feedType);
    }

    /// <summary>
    /// Uses AI to generate a ranked list of features for thread assembly.
    /// Returns null if AI is unavailable or the response cannot be parsed.
    /// </summary>
    public async Task<ThreadPlan?> PlanThreadAsync(
        string releaseTitle,
        string releaseContent,
        string feedType,
        int maxPostLength,
        int maxPosts = 4,
        int topHighlights = 3,
        CancellationToken cancellationToken = default)
    {
        const int maxRetries = 3;
        var totalItemCount = CountItemsInRelease(releaseContent, feedType);
        var cleaned = PrepareContentForAi(releaseContent);

                var prompt = ReleaseSummarizerCliSdkPrompts.BuildThreadPlanUserPrompt(releaseTitle, cleaned, feedType, totalItemCount);

        var messages = new List<Microsoft.Extensions.AI.ChatMessage>
        {
            new(ChatRole.System, ReleaseSummarizerCliSdkPrompts.GetThreadPlanSystemPrompt()),
            new(ChatRole.User, prompt)
        };

        var options = new ChatOptions
        {
            ResponseFormat = ChatResponseFormat.Json
        };

        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                _logger.LogInformation("Requesting AI thread plan for {FeedType} release: {Title} (attempt {Attempt}/{MaxRetries}, {TotalItems} items, prompt length {PromptLength} chars)",
                    feedType, releaseTitle, attempt, maxRetries, totalItemCount, prompt.Length);

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(GetThreadPlanTimeoutSeconds()));

                var response = await _chatClient.GetResponseAsync(messages, options, timeoutCts.Token);
                var json = StripCodeFences(response.Messages.LastOrDefault()?.Text?.Trim() ?? string.Empty);

                var plan = System.Text.Json.JsonSerializer.Deserialize<ThreadPlan>(json, new System.Text.Json.JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (plan == null || plan.Items == null || plan.Items.Count == 0)
                {
                    _logger.LogWarning("AI thread plan response was null or empty for {FeedType}: {Title} (attempt {Attempt}/{MaxRetries}). Response: {Response}",
                        feedType, releaseTitle, attempt, maxRetries, json.Length > 200 ? json[..200] : json);
                    if (attempt < maxRetries)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(attempt * 2), cancellationToken);
                        continue;
                    }
                    return null;
                }

                // Use our detected count as the authoritative total — AI's self-reported totalCount
                // can be inaccurate (e.g. overcounting prose sub-points as distinct items).
                // Only fall back to the AI's count when our detection returned nothing.
                plan.TotalCount = totalItemCount > 0 ? totalItemCount
                    : (plan.TotalCount > 0 ? plan.TotalCount : plan.Items.Count);

                _logger.LogInformation("Generated thread plan: {TotalCount} total, {ItemCount} ranked items for {FeedType}: {Title}",
                    plan.TotalCount, plan.Items.Count, feedType, releaseTitle);

                return plan;
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex,
                    "Timed out generating AI thread plan for {FeedType}: {Title} (attempt {Attempt}/{MaxRetries}). TimeoutSeconds={TimeoutSeconds}",
                    feedType, releaseTitle, attempt, maxRetries, GetThreadPlanTimeoutSeconds());
                if (attempt < maxRetries)
                {
                    await Task.Delay(TimeSpan.FromSeconds(attempt * 2), cancellationToken);
                    continue;
                }
                return null;
            }
            catch (Exception ex)
            {
                var errorCode = (ex as Azure.RequestFailedException)?.ErrorCode ?? "N/A";
                var statusCode = (ex as Azure.RequestFailedException)?.Status.ToString() ?? "N/A";
                _logger.LogError(ex,
                    "Error generating AI thread plan for {FeedType}: {Title} (attempt {Attempt}/{MaxRetries}). StatusCode={StatusCode}, ErrorCode={ErrorCode}, Message={ErrorMessage}",
                    feedType, releaseTitle, attempt, maxRetries, statusCode, errorCode, ex.Message);
                if (attempt < maxRetries)
                {
                    await Task.Delay(TimeSpan.FromSeconds(attempt * 2), cancellationToken);
                    continue;
                }
                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// Uses AI to produce a single-post Premium X plan already organized into sections.
    /// Returns null if AI is unavailable or response parsing fails.
    /// </summary>
    public async Task<PremiumPostPlan?> PlanPremiumPostAsync(
        string releaseTitle,
        string releaseContent,
        string feedType,
        int maxLength,
        CancellationToken cancellationToken = default)
    {
        const int maxRetries = 3;
        var totalItemCount = CountItemsInRelease(releaseContent, feedType);
        var cleaned = PrepareContentForAi(releaseContent);

                var prompt = ReleaseSummarizerCliSdkPrompts.BuildPremiumPostUserPrompt(releaseTitle, cleaned, feedType, maxLength);

        var messages = new List<Microsoft.Extensions.AI.ChatMessage>
        {
            new(ChatRole.System, ReleaseSummarizerCliSdkPrompts.GetPremiumPostSystemPrompt()),
            new(ChatRole.User, prompt)
        };

        var options = new ChatOptions
        {
            ResponseFormat = ChatResponseFormat.Json
        };

        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                _logger.LogInformation(
                    "Requesting AI premium plan for {FeedType} release: {Title} (attempt {Attempt}/{MaxRetries}, {TotalItems} items)",
                    feedType, releaseTitle, attempt, maxRetries, totalItemCount);

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(GetThreadPlanTimeoutSeconds()));

                var response = await _chatClient.GetResponseAsync(messages, options, timeoutCts.Token);
                var json = StripCodeFences(response.Messages.LastOrDefault()?.Text?.Trim() ?? string.Empty);

                var plan = System.Text.Json.JsonSerializer.Deserialize<PremiumPostPlan>(json, new System.Text.Json.JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (plan == null)
                {
                    _logger.LogWarning("AI premium post plan was null for {FeedType}: {Title}", feedType, releaseTitle);
                    if (attempt < maxRetries)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(attempt * 2), cancellationToken);
                        continue;
                    }
                    return null;
                }

                plan.TopFeatures ??= [];
                plan.Enhancements ??= [];
                plan.BugFixes ??= [];
                plan.Misc ??= [];

                var itemCount = plan.TopFeatures.Count + plan.Enhancements.Count + plan.BugFixes.Count + plan.Misc.Count;
                if (itemCount == 0)
                {
                    _logger.LogWarning("AI premium post plan had no categorized items for {FeedType}: {Title}", feedType, releaseTitle);
                    if (attempt < maxRetries)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(attempt * 2), cancellationToken);
                        continue;
                    }
                    return null;
                }

                // Use our detected count as the authoritative total — AI's self-reported totalCount
                // can be inaccurate (e.g. overcounting prose sub-points as distinct items).
                // Only fall back to the AI's count when our detection returned nothing.
                plan.TotalCount = totalItemCount > 0 ? totalItemCount
                    : (plan.TotalCount > 0 ? plan.TotalCount : itemCount);

                _logger.LogInformation(
                    "Generated premium plan: total={TotalCount}, top={TopCount}, enh={EnhCount}, bugs={BugCount}, misc={MiscCount} for {FeedType}: {Title}",
                    plan.TotalCount,
                    plan.TopFeatures.Count,
                    plan.Enhancements.Count,
                    plan.BugFixes.Count,
                    plan.Misc.Count,
                    feedType,
                    releaseTitle);

                return plan;
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex,
                    "Timed out generating AI premium plan for {FeedType}: {Title} (attempt {Attempt}/{MaxRetries}).",
                    feedType, releaseTitle, attempt, maxRetries);
                if (attempt < maxRetries)
                {
                    await Task.Delay(TimeSpan.FromSeconds(attempt * 2), cancellationToken);
                    continue;
                }
                return null;
            }
            catch (Exception ex)
            {
                var errorCode = (ex as Azure.RequestFailedException)?.ErrorCode ?? "N/A";
                var statusCode = (ex as Azure.RequestFailedException)?.Status.ToString() ?? "N/A";
                _logger.LogError(ex,
                    "Error generating AI premium plan for {FeedType}: {Title} (attempt {Attempt}/{MaxRetries}). StatusCode={StatusCode}, ErrorCode={ErrorCode}",
                    feedType, releaseTitle, attempt, maxRetries, statusCode, errorCode);

                if (attempt < maxRetries)
                {
                    await Task.Delay(TimeSpan.FromSeconds(attempt * 2), cancellationToken);
                    continue;
                }

                return null;
            }
        }

        return null;
    }

    private static string StripCodeFences(string json)
    {
        if (!json.StartsWith("```", StringComparison.Ordinal))
        {
            return json;
        }

        var firstNewline = json.IndexOf('\n');
        var lastFence = json.LastIndexOf("```", StringComparison.Ordinal);
        if (firstNewline >= 0 && lastFence > firstNewline)
        {
            return json[(firstNewline + 1)..lastFence].Trim();
        }

        return json;
    }

    private static int GetThreadPlanTimeoutSeconds()
    {
        var configured = Environment.GetEnvironmentVariable("AI_THREAD_PLAN_TIMEOUT_SECONDS");
        return int.TryParse(configured, out var seconds) && seconds > 0
            ? seconds
            : DefaultThreadPlanTimeoutSeconds;
    }

}
