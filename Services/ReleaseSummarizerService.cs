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
    /// <param name="cancellationToken">Cancellation token for the async operation</param>
    /// <returns>A well-formatted summary with emojis highlighting top features</returns>
    public async Task<string> SummarizeReleaseAsync(string releaseTitle, string releaseContent, int maxLength, CancellationToken cancellationToken = default)
    {
        try
        {
            var totalItemCount = CountItemsInRelease(releaseContent);
            var systemPrompt = GetSystemPrompt();
            var userPrompt = BuildUserPrompt(releaseTitle, releaseContent, maxLength, totalItemCount);
            
            var messages = new List<Microsoft.Extensions.AI.ChatMessage>
            {
                new(ChatRole.System, systemPrompt),
                new(ChatRole.User, userPrompt)
            };

            _logger.LogInformation("Requesting AI summary for release: {Title} ({TotalItems} items)", releaseTitle, totalItemCount);
            
            // Use GetResponseAsync from version 10.2 API
            var response = await _chatClient.GetResponseAsync(messages, cancellationToken: cancellationToken);
            var summary = response.Messages.LastOrDefault()?.Text?.Trim() ?? string.Empty;
            
            _logger.LogInformation("Generated summary ({Length} chars): {Summary}", summary.Length, summary);
            
            return summary;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating AI summary for release: {Title}", releaseTitle);
            throw;
        }
    }

    private static string GetSystemPrompt() => @"You are an expert at analyzing software release notes and creating concise, engaging summaries for social media.

Your task is to:
1. Identify the most exciting and impactful features or changes from release notes
2. Format them in a concise way with appropriate emojis
3. Ensure the summary fits within the specified character limit
4. Use emojis strategically to enhance readability and appeal

Emoji guidelines:
- ✨ for new features
- ⚡ for performance improvements
- 🐛 for bug fixes
- 🔒 for security updates
- 📖 for documentation
- 🎉 for major milestones

Keep the tone exciting and developer-friendly. Focus on what matters most to users.";

    private static int CountItemsInRelease(string htmlContent)
    {
        try
        {
            // Decode HTML entities
            var decoded = WebUtility.HtmlDecode(htmlContent);
            
            // Extract list items from HTML
            var matches = ListItemPattern.Matches(decoded);
            
            var count = 0;
            foreach (Match match in matches)
            {
                var text = StripHtml(match.Groups[1].Value).Trim();
                // Skip empty items and "Full Changelog" entries
                if (!string.IsNullOrWhiteSpace(text) && !text.StartsWith("Full Changelog", StringComparison.OrdinalIgnoreCase))
                {
                    count++;
                }
            }
            
            return count;
        }
        catch (Exception ex)
        {
            // If parsing fails, return 0 to avoid breaking the summary
            // Exception is not logged here as it's a non-critical operation
            // and we gracefully fall back to 0
            _ = ex; // Explicitly acknowledge exception for code analysis
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

    private static string BuildUserPrompt(string releaseTitle, string releaseContent, int maxLength, int totalItemCount) =>
        $@"Summarize the following release notes for {releaseTitle}.

Release Content:
{releaseContent}

Total items in release: {totalItemCount}

Requirements:
- Maximum length: {maxLength} characters
- Include 2-3 of the most important/exciting features
- Use emojis to make it visually appealing
- Each feature should be on its own line
- Be concise and impactful
- If you only show a subset of items (fewer than {totalItemCount}), add a line at the end: ""...and X more"" where X is the remaining count
- DO NOT include any markdown formatting or headers
- DO NOT include the version number (it will be added separately)
- Output ONLY the formatted feature list, nothing else

Example output format (when showing 3 out of 5 items):
✨ New feature that does something cool
⚡ Performance improvement that makes things faster
🐛 Fixed critical bug affecting users
...and 2 more";
}
