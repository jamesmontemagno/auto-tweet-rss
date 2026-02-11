using Microsoft.Extensions.Logging;

namespace AutoTweetRss.Services;

/// <summary>
/// Twitter API client for the VS Code updates account.
/// Uses TWITTER_VSCODE_* credentials via a separate OAuth1Helper instance.
/// </summary>
public class VSCodeTwitterApiClient : TwitterApiClient, ISocialMediaClient
{
    public string PlatformName => "Twitter";

    public VSCodeTwitterApiClient(
        ILogger<VSCodeTwitterApiClient> logger,
        IHttpClientFactory httpClientFactory,
        OAuth1Helper oauth1Helper)
        : base((ILogger)logger, httpClientFactory, oauth1Helper)
    {
    }

    public Task<bool> PostAsync(string text) => PostTweetAsync(text);
}

