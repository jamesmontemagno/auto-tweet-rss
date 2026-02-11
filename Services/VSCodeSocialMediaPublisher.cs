using Microsoft.Extensions.Logging;

namespace AutoTweetRss.Services;

/// <summary>
/// Publishes content to all configured social media platforms for the VS Code account.
/// Each platform is attempted independently — a failure on one does not block others.
/// </summary>
public class VSCodeSocialMediaPublisher
{
    private readonly ILogger<VSCodeSocialMediaPublisher> _logger;
    private readonly ISocialMediaClient[] _clients;

    public VSCodeSocialMediaPublisher(
        ILogger<VSCodeSocialMediaPublisher> logger,
        VSCodeTwitterApiClient twitterClient,
        BlueskyApiClient blueskyClient)
    {
        _logger = logger;
        _clients = [twitterClient, blueskyClient];
    }

    /// <summary>
    /// Whether at least one platform has valid credentials configured.
    /// </summary>
    public bool IsConfigured => _clients.Any(c => c.IsConfigured);

    /// <summary>
    /// Posts text to all configured platforms independently.
    /// </summary>
    /// <returns>True if at least one platform posted successfully.</returns>
    public async Task<bool> PostToAllAsync(string text)
    {
        var anySuccess = false;
        var anyConfigured = false;

        foreach (var client in _clients)
        {
            if (!client.IsConfigured)
            {
                _logger.LogInformation("{Platform} is not configured. Skipping.", client.PlatformName);
                continue;
            }

            anyConfigured = true;

            try
            {
                var success = await client.PostAsync(text);
                if (success)
                {
                    _logger.LogInformation("Successfully posted to {Platform}.", client.PlatformName);
                    anySuccess = true;
                }
                else
                {
                    _logger.LogWarning("Failed to post to {Platform}.", client.PlatformName);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error posting to {Platform}.", client.PlatformName);
            }
        }

        if (!anyConfigured)
        {
            _logger.LogWarning("No social media platforms are configured.");
        }

        return anySuccess;
    }
}
