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
    private readonly IChatClient _chatClient;
    private readonly ILogger<ReleaseSummarizerService> _logger;
    
    // Compiled regex patterns for better performance
    private static readonly Regex ListItemPattern = new(@"<li[^>]*>(.*?)</li>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex HtmlTagPattern = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex WhitespacePattern = new(@"\s+", RegexOptions.Compiled);

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
            var systemPrompt = GetSystemPrompt();
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
            
            _logger.LogInformation("Generated {FeedType} summary ({Length} chars): {Summary}", feedType, summary.Length, summary);
            
            return summary;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating AI summary for {FeedType} release: {Title}", feedType, releaseTitle);
            throw;
        }
    }

    private static string GetSystemPrompt() => @"You are an expert at analyzing software release notes and creating concise, engaging summaries for social media.

Your task is to:
1. Identify the most exciting and impactful features or changes from release notes
2. Format them in a concise way with appropriate emojis
3. Ensure the summary fits within the specified character limit
4. Use emojis strategically to enhance readability and appeal
5. NEVER include user names, contributor names, or issue numbers
6. Focus ONLY on features, fixes, and improvements - not who contributed them

Emoji guidelines:
- ✨ for new features
- ⚡ for performance improvements
- 🐛 for bug fixes
- 🔒 for security updates
- 📖 for documentation
- 🎉 for major milestones

Keep the tone exciting and developer-friendly. Focus on what matters most to users.";

    private static int CountItemsInRelease(string htmlContent, string feedType = "sdk")
    {
        try
        {
            // Decode HTML entities
            var decoded = WebUtility.HtmlDecode(htmlContent);
            
            // Check if we're past the "New Contributors" section - don't count those items
            var newContributorsIndex = decoded.IndexOf("New Contributors", StringComparison.OrdinalIgnoreCase);
            var contentToCount = newContributorsIndex >= 0 ? decoded[..newContributorsIndex] : decoded;
            
            // Extract list items from HTML
            var matches = ListItemPattern.Matches(contentToCount);
            
            var count = 0;
            foreach (Match match in matches)
            {
                var text = StripHtml(match.Groups[1].Value).Trim();
                // Skip empty items, "Full Changelog" entries, and contributor mentions
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

    private static string BuildUserPrompt(string releaseTitle, string releaseContent, int maxLength, int totalItemCount, string feedType)
    {
        // Calculate how many items we can likely fit
        // Estimate: emoji (2) + space (1) + average description (35-40 chars) + newline (1) = ~40 chars per item
        // Reserve space for "...and X more" suffix (~15 chars)
        var estimatedCharsPerItem = feedType == "cli" ? 50 : 40; // CLI items tend to be longer
        var reserveForSuffix = totalItemCount > 5 ? 15 : 0;
        var maxItems = Math.Max(3, (maxLength - reserveForSuffix) / estimatedCharsPerItem);
        // Cap at reasonable maximum to avoid token limits
        maxItems = Math.Min(maxItems, 10);
        
        if (feedType == "cli")
        {
            return BuildCliUserPrompt(releaseTitle, releaseContent, maxLength, totalItemCount, maxItems);
        }
        if (feedType == "vscode")
        {
            return BuildVSCodeUserPrompt(releaseTitle, releaseContent, maxLength, totalItemCount, maxItems);
        }
        return BuildSdkUserPrompt(releaseTitle, releaseContent, maxLength, totalItemCount, maxItems);
    }

    private static string BuildCliUserPrompt(string releaseTitle, string releaseContent, int maxLength, int totalItemCount, int targetItems) =>
        $@"Summarize the following Copilot CLI release notes for {releaseTitle}.

Release Content:
{releaseContent}

Total items in release: {totalItemCount}
Target items to show: {targetItems}

Requirements:
- Maximum length: {maxLength} characters (this is CRITICAL - count characters carefully)
- Include UP TO {targetItems} of the most important/exciting features that fit within the character limit
- Prioritize: Show as many HIGH-RELEVANCE items as possible while staying under {maxLength} characters
- NEVER include user names, contributor names, or issue/PR numbers in the summary
- Focus ONLY on what the feature does, not who contributed it
- Use emojis to make it visually appealing
- Each feature should be on its own line
- IMPORTANT: CLI feature descriptions are often long - you MUST shorten/summarize them to fit more items
- Keep each feature line concise (aim for 40-50 characters) to maximize count
- CRITICAL: If you show fewer items than the total ({totalItemCount} items), you MUST add ""...and X more"" as the FINAL line where X = items not shown
- DO NOT include any markdown formatting or headers
- DO NOT include the version number (it will be added separately)
- DO NOT truncate feature descriptions mid-sentence with ""..."" - either shorten them properly or omit them
- Output ONLY the formatted feature list, nothing else
- MAXIMIZE the number of items shown - more items is better than longer descriptions!

Example output format (when total items = 8, showing 5):
✨ Show compaction status in timeline
✨ Add Esc-Esc to undo file changes
✨ Support for GHE Cloud remote agents
⚡ Improved workspace indexing speed
🐛 Fixed file watcher memory leak
...and 3 more

Example output format (when total items = 3, showing 3):
✨ Show compaction status in timeline
✨ Add Esc-Esc to undo file changes
✨ Support for GHE Cloud remote agents";

    private static string BuildSdkUserPrompt(string releaseTitle, string releaseContent, int maxLength, int totalItemCount, int targetItems) =>
        $@"Summarize the following release notes for {releaseTitle}.

Release Content:
{releaseContent}

Total items in release: {totalItemCount}
Target items to show: {targetItems}

Requirements:
- Maximum length: {maxLength} characters (this is CRITICAL - count characters carefully)
- Include UP TO {targetItems} of the most important/exciting features that fit within the character limit
- Prioritize: Show as many HIGH-RELEVANCE items as possible while staying under {maxLength} characters
- NEVER include user names, contributor names, or issue/PR numbers in the summary
- Focus ONLY on what the feature does, not who contributed it
- Use emojis to make it visually appealing
- Each feature should be on its own line
- Keep descriptions concise (aim for 35-40 characters per line) to fit more items
- CRITICAL: If you show fewer items than the total ({totalItemCount} items), you MUST add ""...and X more"" as the FINAL line where X = items not shown
- DO NOT include any markdown formatting or headers
- DO NOT include the version number (it will be added separately)
- Output ONLY the formatted feature list, nothing else
- MAXIMIZE the number of items shown - more items is better than longer descriptions!

Example output format (when total items = 8, showing 6):
✨ New AI code completion engine
⚡ 40% faster suggestion generation
🐛 Fixed context window overflow
✨ Support for Rust language
🔒 Updated security dependencies
📖 Added API migration guide
...and 2 more

Example output format (when total items = 4, showing 4):
✨ New AI code completion engine
⚡ 40% faster suggestion generation
🐛 Fixed context window overflow
✨ Support for Rust language";

    private static string BuildVSCodeUserPrompt(string releaseTitle, string releaseContent, int maxLength, int totalItemCount, int targetItems) =>
        $@"Summarize the following VS Code Insiders release notes for {releaseTitle}.

Release Content:
{releaseContent}

Total items in release: {totalItemCount}
Target items to show: {targetItems}

Requirements:
- Start with 2-3 sentences providing a high-level summary of the day's updates
- Maximum length: {maxLength} characters (this is CRITICAL - count characters carefully)
- After the summary sentences, include UP TO {targetItems} of the most important/exciting features
- NEVER include user names, contributor names, or issue/PR numbers in the summary
- Focus ONLY on what the feature does, not who contributed it
- Use emojis to make it visually appealing
- Each feature should be on its own line
- Keep descriptions concise (aim for 40-50 characters per line)
- If you show fewer items than the total, add ""...and X more"" as the FINAL line
- DO NOT include any markdown formatting or headers
- Output ONLY the summary and formatted feature list, nothing else

Example output format:
VS Code Insiders brings exciting updates to the chat experience and terminal functionality. Performance improvements and new settings make development smoother.

✨ Improved inline chat discoverability
⚡ Better performance for long sessions
🔧 New terminal sticky scroll setting
🎨 Chat overlay hover UI enhanced
...and 3 more";
}
