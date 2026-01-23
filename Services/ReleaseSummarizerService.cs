using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AutoTweetRss.Services;

/// <summary>
/// Service for generating AI-powered summaries of release notes
/// </summary>
public class ReleaseSummarizerService
{
    private readonly IChatClient _chatClient;
    private readonly ILogger<ReleaseSummarizerService> _logger;

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
            var systemPrompt = GetSystemPrompt();
            var userPrompt = BuildUserPrompt(releaseTitle, releaseContent, maxLength);
            
            var messages = new List<Microsoft.Extensions.AI.ChatMessage>
            {
                new(ChatRole.System, systemPrompt),
                new(ChatRole.User, userPrompt)
            };

            _logger.LogInformation("Requesting AI summary for release: {Title}", releaseTitle);
            
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

    private static string BuildUserPrompt(string releaseTitle, string releaseContent, int maxLength) =>
        $@"Summarize the following release notes for {releaseTitle}.

Release Content:
{releaseContent}

Requirements:
- Maximum length: {maxLength} characters
- Include 2-3 of the most important/exciting features
- Use emojis to make it visually appealing
- Each feature should be on its own line
- Be concise and impactful
- DO NOT include any markdown formatting or headers
- DO NOT include the version number (it will be added separately)
- Output ONLY the formatted feature list, nothing else

Example output format:
✨ New feature that does something cool
⚡ Performance improvement that makes things faster
🐛 Fixed critical bug affecting users";
}
