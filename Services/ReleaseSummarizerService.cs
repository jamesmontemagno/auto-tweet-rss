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
        var isCliWeekly = string.Equals(feedType, "cli-weekly", StringComparison.OrdinalIgnoreCase);
        var estimatedCharsPerItem = feedType.StartsWith("cli", StringComparison.OrdinalIgnoreCase) ? 50 : 40; // CLI items tend to be longer
        if (isCliWeekly)
        {
            // Weekly recaps should favor more, shorter highlights
            estimatedCharsPerItem = 38;
        }
        var reserveForSuffix = totalItemCount > 5 ? 15 : 0;
        var maxItems = Math.Max(3, (maxLength - reserveForSuffix) / estimatedCharsPerItem);
        // Cap at reasonable maximum to avoid token limits
        maxItems = Math.Min(maxItems, isCliWeekly ? 12 : 10);
        
        if (feedType == "cli-weekly")
        {
            return BuildCliWeeklyUserPrompt(releaseTitle, releaseContent, maxLength, totalItemCount, maxItems);
        }
        if (feedType == "cli")
        {
            return BuildCliUserPrompt(releaseTitle, releaseContent, maxLength, totalItemCount, maxItems);
        }
        if (feedType == "vscode")
        {
            return BuildVSCodeUserPrompt(releaseTitle, releaseContent, maxLength, totalItemCount, maxItems);
        }
        if (feedType == "vscode-ai")
        {
            return BuildVSCodeAiUserPrompt(releaseTitle, releaseContent, maxLength, totalItemCount, Math.Min(maxItems, 8));
        }
        if (feedType == "vscode-week-ai")
        {
            return BuildVSCodeAiWeeklyUserPrompt(releaseTitle, releaseContent, maxLength, totalItemCount, Math.Min(maxItems, 8));
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

    private static string BuildCliWeeklyUserPrompt(string releaseTitle, string releaseContent, int maxLength, int totalItemCount, int targetItems) =>
        $@"Create a weekly recap summary of the following Copilot CLI release notes for {releaseTitle}.

Release Content:
{releaseContent}

Total items in release window: {totalItemCount}
Target items to show: {targetItems}

Requirements:
- Maximum length: {maxLength} characters (this is CRITICAL - count characters carefully)
- Include UP TO {targetItems} of the most important/high-impact changes across the week
- Deduplicate similar items across releases; focus on themes and top features
- EXCLUDE any items that mention ""staff flag"" or internal labels
- NEVER include user names, contributor names, or issue/PR numbers in the summary
- DO NOT include version numbers or dates
- Focus ONLY on what the features do, not who contributed them
- Use emojis to make it visually appealing
- Each highlight should be on its own line
- Keep each highlight line ultra concise (aim for 20-35 characters) to maximize count
- Prefer short noun-phrase highlights over sentences
- CRITICAL: If you show fewer items than the total ({totalItemCount} items), you MUST add ""...and X more"" as the FINAL line where X = items not shown
- DO NOT include any markdown formatting or headers
- Output ONLY the formatted highlight list, nothing else

Example output format (when total items = 9, showing 5):
✨ Smarter repo context for agents
✨ New interactive setup flow
⚡ Faster indexing for large workspaces
🐛 Fixed auth refresh edge cases
🔒 Hardened token storage behavior
...and 4 more

Example output format (when total items = 3, showing 3):
✨ Smarter repo context for agents
⚡ Faster indexing for large workspaces
🐛 Fixed auth refresh edge cases";

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

    private static string BuildVSCodeUserPrompt(string releaseTitle, string releaseContent, int maxLength, int totalItemCount, int targetItems)
    {
        // Check if this is a rich/full summary (longer maxLength)
        var isRichSummary = maxLength > 1000;
        
        if (isRichSummary)
        {
            return $@"Create a comprehensive, detailed summary of the following VS Code Insiders release for {releaseTitle}.

Release Content:
{releaseContent}

Total items in release: {totalItemCount}

Requirements:
- This is a FULL RELEASE SUMMARY - be comprehensive and detailed
- Start with 3-5 sentences providing a high-level overview of the major themes and improvements
- Maximum length: {maxLength} characters (you have plenty of space - use it wisely)
- Group features by category (e.g., Chat, Terminal, Editor, Extensions, etc.)
- Include as many important features as possible, organized by their categories
- NEVER include user names, contributor names, or issue/PR numbers
- Use emojis strategically to make it visually appealing and scannable
- Use clear category headers to organize the features
- Keep individual feature descriptions informative but concise (50-80 chars each)
- If there are more items than you can include, add ""...and X more"" at the end
- DO NOT include markdown headers with #
- Make it exciting and highlight the most impactful changes

Example output format:
VS Code Insiders delivers a major update with significant improvements across chat, terminal, and editor experiences. This release focuses on performance, discoverability, and enhanced workflows. New AI-powered features and refined UI elements make development more efficient.

🤖 Chat & AI:
✨ Improved inline chat discoverability with new UI
✨ Chat overlay hover interactions enhanced
⚡ Better performance for long chat sessions
✨ New @workspace context improvements

⌨️ Terminal:
✨ Sticky scroll setting for better navigation
⚡ Faster rendering for large outputs
🐛 Fixed Unicode character display issues

✏️ Editor:
✨ New IntelliSense improvements for TypeScript
✨ Multi-cursor enhancements
🎨 Refined syntax highlighting for JSX
⚡ Improved file watcher performance

🔧 Extensions & Settings:
✨ New extension marketplace filters
🔒 Enhanced security for extension installations
📖 Better extension documentation display

...and 15 more updates across debugging, source control, and themes.";
        }
        
        return $@"Summarize the following VS Code Insiders release notes for {releaseTitle}.

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

    private static string BuildVSCodeAiUserPrompt(string releaseTitle, string releaseContent, int maxLength, int totalItemCount, int targetItems) =>
        $@"Summarize ONLY the AI-related updates from the following VS Code Insiders release for {releaseTitle}.

Release Content:
{releaseContent}

Total items in release: {totalItemCount}
Target items to show: {targetItems}

Requirements:
- Maximum length: {maxLength} characters (this is CRITICAL - count characters carefully)
- Include ONLY AI-related features (chat, copilots, inline suggestions, AI tools, model support, prompt features)
- Start with 1-2 sentences summarizing AI themes for the release
- After the summary sentences, include UP TO {targetItems} of the most important AI features
- If there are no AI-related updates, respond with: No notable AI updates in this release.
- NEVER include user names, contributor names, or issue/PR numbers in the summary
- Use emojis to make it visually appealing
- Each feature should be on its own line
- Keep descriptions concise (aim for 40-50 characters per line)
- If you show fewer items than the total AI items, add ...and X more as the FINAL line
- DO NOT include any markdown formatting or headers
- Output ONLY the summary and formatted feature list, nothing else

Example output format:
VS Code Insiders expands AI workflows with stronger chat actions and richer inline suggestions.

✨ New inline chat actions for refactors
✨ Smarter @workspace context retrieval
⚡ Faster AI response streaming
...and 2 more";

    private static string BuildVSCodeAiWeeklyUserPrompt(string releaseTitle, string releaseContent, int maxLength, int totalItemCount, int targetItems) =>
        $@"Create a weekly recap that ONLY highlights AI-related updates from the following VS Code Insiders release notes for {releaseTitle}.

Release Content:
{releaseContent}

Total items in release window: {totalItemCount}
Target items to show: {targetItems}

Requirements:
- Maximum length: {maxLength} characters (this is CRITICAL - count characters carefully)
- Include ONLY AI-related features (chat, copilots, inline suggestions, AI tools, model support, prompt features)
- Start with 1-2 sentences summarizing AI themes across the week
- After the summary sentences, include UP TO {targetItems} of the most important AI features
- If there are no AI-related updates, respond with: No notable AI updates this week.
- NEVER include user names, contributor names, or issue/PR numbers in the summary
- Use emojis to make it visually appealing
- Each feature should be on its own line
- Keep descriptions concise (aim for 35-45 characters per line)
- If you show fewer items than the total AI items, add ...and X more as the FINAL line
- DO NOT include any markdown formatting or headers
- Output ONLY the summary and formatted feature list, nothing else

Example output format:
This week brings stronger AI chat workflows and better inline suggestion control.

✨ New chat actions for docs edits
✨ Smarter prompt variables in chat
⚡ Faster AI responses in large repos
...and 3 more";
}
