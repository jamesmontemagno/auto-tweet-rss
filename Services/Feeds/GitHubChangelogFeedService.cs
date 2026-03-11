using System.ServiceModel.Syndication;
using System.Text.RegularExpressions;
using System.Xml;
using Microsoft.Extensions.Logging;

namespace AutoTweetRss.Services;

public partial class GitHubChangelogFeedService
{
    private const string FeedUrl = "https://github.blog/changelog/feed/";
    private const string ContentNamespace = "http://purl.org/rss/1.0/modules/content/";

    private readonly ILogger<GitHubChangelogFeedService> _logger;
    private readonly HttpClient _httpClient;

    [GeneratedRegex(@"<p>\s*The post .*?appeared first on.*?</p>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex WordPressFooterPattern();

    [GeneratedRegex(@"<video[^>]*\bsrc=""([^""]+)""", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex VideoPattern();

    [GeneratedRegex(@"<source[^>]*\btype=""video/[^""]+""[^>]*\bsrc=""([^""]+)""", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex VideoSourcePattern();

    [GeneratedRegex(@"<img[^>]*\bsrc=""([^""]+)""", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ImagePattern();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.Compiled)]
    private static partial Regex HtmlTagPattern();

    [GeneratedRegex(@"\s+", RegexOptions.Compiled)]
    private static partial Regex WhitespacePattern();

    public GitHubChangelogFeedService(
        ILogger<GitHubChangelogFeedService> logger,
        IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _httpClient = httpClientFactory.CreateClient();
    }

    public async Task<IReadOnlyList<GitHubChangelogEntry>> GetEntriesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Fetching GitHub changelog feed");

            using var stream = await _httpClient.GetStreamAsync(FeedUrl, cancellationToken);
            using var reader = XmlReader.Create(stream);
            var feed = SyndicationFeed.Load(reader);

            if (feed == null)
            {
                _logger.LogWarning("GitHub changelog feed returned no items");
                return [];
            }

            var entries = feed.Items
                .Select(CreateEntry)
                .Where(entry => entry != null)
                .Cast<GitHubChangelogEntry>()
                .OrderByDescending(entry => entry.Updated)
                .ToList();

            _logger.LogInformation("Parsed {Count} GitHub changelog entries", entries.Count);
            return entries;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching GitHub changelog feed");
            return [];
        }
    }

    public async Task<string> FindCopilotDescriptionForUrlAsync(string url, CancellationToken cancellationToken)
    {
        var normalizedTarget = NormalizeUrlForCompare(url);
        if (string.IsNullOrEmpty(normalizedTarget))
        {
            return string.Empty;
        }

        var entries = await GetEntriesAsync(cancellationToken);
        var match = entries
            .Where(entry => string.Equals(NormalizeUrlForCompare(entry.Link), normalizedTarget, StringComparison.OrdinalIgnoreCase))
            .Where(entry => entry.Labels.Any(label => label.Contains("copilot", StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(entry => entry.Updated)
            .FirstOrDefault();

        return match?.SummaryText ?? string.Empty;
    }

    private GitHubChangelogEntry? CreateEntry(SyndicationItem item)
    {
        var link = item.Links.FirstOrDefault()?.Uri?.ToString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(link))
        {
            return null;
        }

        var contentHtml = GetEncodedContent(item);
        var summaryHtml = item.Summary?.Text ?? string.Empty;
        var normalizedContent = RemoveWordPressFooter(contentHtml);
        var normalizedSummary = RemoveWordPressFooter(summaryHtml);
        var media = ExtractMedia(normalizedContent);
        var updated = item.LastUpdatedTime != default ? item.LastUpdatedTime : item.PublishDate;
        var labels = item.Categories
            .Select(category => category.Name ?? category.Label ?? string.Empty)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new GitHubChangelogEntry
        {
            Id = item.Id ?? link,
            Title = item.Title?.Text?.Trim() ?? string.Empty,
            Link = link,
            SummaryHtml = normalizedSummary,
            SummaryText = StripHtml(normalizedSummary),
            ContentHtml = normalizedContent,
            ContentText = StripHtml(normalizedContent),
            Labels = labels,
            Updated = updated,
            ChangelogType = item.Categories
                .FirstOrDefault(category => string.Equals(category.Scheme, "changelog-type", StringComparison.OrdinalIgnoreCase))
                ?.Name ?? string.Empty,
            Media = media
        };
    }

    private static string GetEncodedContent(SyndicationItem item)
    {
        try
        {
            var content = item.ElementExtensions.ReadElementExtensions<string>("encoded", ContentNamespace);
            if (content.Count > 0)
            {
                return content[0] ?? string.Empty;
            }
        }
        catch
        {
        }

        return item.Summary?.Text ?? string.Empty;
    }

    private static List<GitHubChangelogMediaItem> ExtractMedia(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return [];
        }

        var media = new List<GitHubChangelogMediaItem>();

        foreach (Match match in VideoSourcePattern().Matches(html))
        {
            var url = match.Groups[1].Value;
            if (Uri.TryCreate(url, UriKind.Absolute, out _))
            {
                media.Add(new GitHubChangelogMediaItem(url, GitHubChangelogMediaType.Video));
            }
        }

        foreach (Match match in VideoPattern().Matches(html))
        {
            var url = match.Groups[1].Value;
            if (Uri.TryCreate(url, UriKind.Absolute, out _))
            {
                media.Add(new GitHubChangelogMediaItem(url, GitHubChangelogMediaType.Video));
            }
        }

        foreach (Match match in ImagePattern().Matches(html))
        {
            var url = match.Groups[1].Value;
            if (Uri.TryCreate(url, UriKind.Absolute, out _) &&
                !url.Contains("favicon", StringComparison.OrdinalIgnoreCase) &&
                !url.Contains("emoji", StringComparison.OrdinalIgnoreCase))
            {
                media.Add(new GitHubChangelogMediaItem(url, GitHubChangelogMediaType.Image));
            }
        }

        return media
            .GroupBy(item => item.Url, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    private static string RemoveWordPressFooter(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        return WordPressFooterPattern().Replace(html, string.Empty).Trim();
    }

    private static string StripHtml(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        var withoutTags = HtmlTagPattern().Replace(System.Net.WebUtility.HtmlDecode(html), " ");
        return WhitespacePattern().Replace(withoutTags, " ").Trim();
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
}

public sealed class GitHubChangelogEntry
{
    public required string Id { get; set; }
    public required string Title { get; set; }
    public required string Link { get; set; }
    public required string SummaryHtml { get; set; }
    public required string SummaryText { get; set; }
    public required string ContentHtml { get; set; }
    public required string ContentText { get; set; }
    public required IReadOnlyList<string> Labels { get; set; }
    public required IReadOnlyList<GitHubChangelogMediaItem> Media { get; set; }
    public required string ChangelogType { get; set; }
    public DateTimeOffset Updated { get; set; }
}

public sealed record GitHubChangelogMediaItem(string Url, GitHubChangelogMediaType MediaType);

public enum GitHubChangelogMediaType
{
    Image,
    Video
}
