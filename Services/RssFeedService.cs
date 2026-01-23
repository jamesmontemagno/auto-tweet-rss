using System.ServiceModel.Syndication;
using System.Text.RegularExpressions;
using System.Xml;
using Microsoft.Extensions.Logging;

namespace AutoTweetRss.Services;

public class RssFeedService
{
    private readonly ILogger<RssFeedService> _logger;
    private readonly HttpClient _httpClient;

    public RssFeedService(ILogger<RssFeedService> logger, IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _httpClient = httpClientFactory.CreateClient();
    }

    public async Task<List<ReleaseEntry>> GetNonPreReleaseEntriesAsync(string feedUrl, bool isSdkFeed = false)
    {
        var entries = new List<ReleaseEntry>();

        try
        {
            _logger.LogInformation("Fetching RSS feed from {FeedUrl}", feedUrl);

            using var stream = await _httpClient.GetStreamAsync(feedUrl);
            using var reader = XmlReader.Create(stream);
            var feed = SyndicationFeed.Load(reader);

            foreach (var item in feed.Items)
            {
                var title = item.Title?.Text ?? string.Empty;
                var content = (item.Content as TextSyndicationContent)?.Text ?? string.Empty;
                var link = item.Links.FirstOrDefault()?.Uri?.ToString() ?? string.Empty;
                var id = item.Id ?? string.Empty;
                var updated = item.LastUpdatedTime;

                // Filter out pre-releases and submodule releases for SDK
                if (isSdkFeed)
                {
                    // Skip Go submodule releases like "go/v0.1.16"
                    if (title.StartsWith("go/", StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogDebug("Skipping Go submodule release: {Title}", title);
                        continue;
                    }
                    
                    // Skip preview releases like "v0.1.16-preview.0"
                    if (IsPreRelease(title, content) || title.Contains("-preview", StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogDebug("Skipping pre-release: {Title}", title);
                        continue;
                    }
                }
                else
                {
                    // Original CLI filtering
                    if (IsPreRelease(title, content))
                    {
                        _logger.LogDebug("Skipping pre-release: {Title}", title);
                        continue;
                    }
                }

                entries.Add(new ReleaseEntry
                {
                    Id = id,
                    Title = title,
                    Content = content,
                    Link = link,
                    Updated = updated
                });

                _logger.LogDebug("Found stable release: {Title}", title);
            }

            _logger.LogInformation("Found {Count} non-pre-release entries", entries.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching RSS feed from {FeedUrl}", feedUrl);
        }

        return entries;
    }

    private static bool IsPreRelease(string title, string content)
    {
        // Check if title has pre-release suffix like "-0", "-1", etc.
        if (Regex.IsMatch(title, @"-\d+$"))
        {
            return true;
        }

        // Check if content contains "Pre-release"
        if (content.Contains("Pre-release", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }
}

public class ReleaseEntry
{
    public required string Id { get; set; }
    public required string Title { get; set; }
    public required string Content { get; set; }
    public required string Link { get; set; }
    public DateTimeOffset Updated { get; set; }
}
