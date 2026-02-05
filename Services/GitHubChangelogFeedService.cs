using System.ServiceModel.Syndication;
using System.Xml;
using Microsoft.Extensions.Logging;

namespace AutoTweetRss.Services;

public class GitHubChangelogFeedService
{
    private const string FeedUrl = "https://github.blog/changelog/feed/";
    private readonly ILogger<GitHubChangelogFeedService> _logger;
    private readonly HttpClient _httpClient;

    public GitHubChangelogFeedService(
        ILogger<GitHubChangelogFeedService> logger,
        IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _httpClient = httpClientFactory.CreateClient();
    }

    public async Task<string> FindCopilotDescriptionForUrlAsync(string url, CancellationToken cancellationToken)
    {
        var normalizedTarget = NormalizeUrlForCompare(url);
        if (string.IsNullOrEmpty(normalizedTarget))
        {
            return string.Empty;
        }

        try
        {
            _logger.LogInformation("Fetching GitHub changelog feed");

            using var stream = await _httpClient.GetStreamAsync(FeedUrl, cancellationToken);
            using var reader = XmlReader.Create(stream);
            var feed = SyndicationFeed.Load(reader);

            if (feed == null)
            {
                _logger.LogWarning("GitHub changelog feed returned no items");
                return string.Empty;
            }

            FeedMatch? newestMatch = null;

            foreach (var item in feed.Items)
            {
                var link = item.Links.FirstOrDefault()?.Uri?.ToString() ?? string.Empty;
                if (string.IsNullOrEmpty(link))
                {
                    continue;
                }

                var normalizedLink = NormalizeUrlForCompare(link);
                if (!string.Equals(normalizedLink, normalizedTarget, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!HasCopilotLabel(item))
                {
                    continue;
                }

                var updated = item.LastUpdatedTime != default ? item.LastUpdatedTime : item.PublishDate;
                var description = item.Summary?.Text
                    ?? (item.Content as TextSyndicationContent)?.Text
                    ?? string.Empty;

                if (newestMatch == null || updated > newestMatch.Updated)
                {
                    newestMatch = new FeedMatch(updated, description);
                }
            }

            return newestMatch?.Description ?? string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching GitHub changelog feed");
            return string.Empty;
        }
    }

    private static bool HasCopilotLabel(SyndicationItem item)
    {
        foreach (var category in item.Categories)
        {
            var name = category.Name ?? string.Empty;
            var label = category.Label ?? string.Empty;

            if (name.Contains("copilot", StringComparison.OrdinalIgnoreCase) ||
                label.Contains("copilot", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalizeUrlForCompare(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return string.Empty;
        }

        var builder = new UriBuilder(uri)
        {
            Query = string.Empty,
            Fragment = string.Empty
        };

        var normalizedPath = builder.Path.TrimEnd('/');
        if (string.IsNullOrEmpty(normalizedPath))
        {
            normalizedPath = "/";
        }

        builder.Path = normalizedPath;

        var scheme = builder.Scheme.ToLowerInvariant();
        var host = builder.Host.ToLowerInvariant();
        var isDefaultPort = (scheme == "https" && builder.Port == 443) ||
                            (scheme == "http" && builder.Port == 80);
        var portPart = isDefaultPort ? string.Empty : $":{builder.Port}";

        return $"{scheme}://{host}{portPart}{builder.Path}";
    }

    private sealed record FeedMatch(DateTimeOffset Updated, string Description);
}
