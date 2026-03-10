using Microsoft.Extensions.Logging;

namespace AutoTweetRss.Services;

/// <summary>
/// Twitter API client for the GitHub Changelog account.
/// Uses TWITTER_GITHUB_CHANGELOG_* credentials via a separate OAuth1Helper instance.
/// </summary>
public class GitHubChangelogTwitterApiClient : TwitterApiClient
{
    public GitHubChangelogTwitterApiClient(
        ILogger<GitHubChangelogTwitterApiClient> logger,
        IHttpClientFactory httpClientFactory,
        OAuth1Helper oauth1Helper)
        : base((ILogger)logger, httpClientFactory, oauth1Helper)
    {
    }
}
