using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using AutoTweetRss.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AutoTweetRss.Functions;

public class CliReleaseSummaryFunction
{
    private readonly ILogger<CliReleaseSummaryFunction> _logger;
    private readonly RssFeedService _rssFeedService;
    private readonly ReleaseSummarizerService? _releaseSummarizer;

    public CliReleaseSummaryFunction(
        ILogger<CliReleaseSummaryFunction> logger,
        RssFeedService rssFeedService,
        IServiceProvider serviceProvider)
    {
        _logger = logger;
        _rssFeedService = rssFeedService;
        _releaseSummarizer = serviceProvider.GetService<ReleaseSummarizerService>();
    }

    /// <summary>
    /// Generates an AI summary paragraph for a specific Copilot CLI version.
    /// </summary>
    /// <remarks>
    /// Query parameters:
    /// - version: Required. Version to summarize (e.g., "1.7.0" or "v1.7.0")
    /// - maxLength: Optional. Max summary length in characters (defaults to 700)
    /// - format: Response format - "json" or "text" (defaults to "json")
    /// </remarks>
    [Function("CliReleaseSummary")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "cli-summary")] HttpRequestData req)
    {
        _logger.LogInformation("CliReleaseSummary called at {Time}", DateTime.UtcNow);

        var response = req.CreateResponse();

        try
        {
            var versionParam = GetQueryParameter(req, "version");
            if (string.IsNullOrWhiteSpace(versionParam))
            {
                response.StatusCode = HttpStatusCode.BadRequest;
                await response.WriteStringAsync("Missing required query parameter: version.");
                return response;
            }

            var maxLength = 700;
            var maxLengthParam = GetQueryParameter(req, "maxLength");
            if (!string.IsNullOrWhiteSpace(maxLengthParam))
            {
                if (!int.TryParse(maxLengthParam, out maxLength) || maxLength <= 0)
                {
                    response.StatusCode = HttpStatusCode.BadRequest;
                    await response.WriteStringAsync("Invalid maxLength. Provide a positive integer.");
                    return response;
                }
            }

            if (_releaseSummarizer == null)
            {
                response.StatusCode = HttpStatusCode.ServiceUnavailable;
                await response.WriteStringAsync("AI summarizer is not configured.");
                return response;
            }

            var format = GetQueryParameter(req, "format")?.ToLowerInvariant() ?? "json";

            var feedUrl = Environment.GetEnvironmentVariable("RSS_FEED_URL")
                ?? "https://github.com/github/copilot-cli/releases.atom";

            var entries = await _rssFeedService.GetNonPreReleaseEntriesAsync(feedUrl);
            if (entries.Count == 0)
            {
                response.StatusCode = HttpStatusCode.NotFound;
                await response.WriteStringAsync("No stable releases found in feed.");
                return response;
            }

            var entry = FindEntryByVersion(entries, versionParam);
            if (entry == null)
            {
                response.StatusCode = HttpStatusCode.NotFound;
                await response.WriteStringAsync($"Version not found in feed: {versionParam}.");
                return response;
            }

            var rawSummary = await _releaseSummarizer.SummarizeReleaseAsync(
                entry.Title,
                entry.Content,
                maxLength,
                feedType: "cli-paragraph");

            var summary = NormalizeParagraph(rawSummary);

            if (format == "text")
            {
                response.StatusCode = HttpStatusCode.OK;
                response.Headers.Add("Content-Type", "text/plain; charset=utf-8");
                await response.WriteStringAsync(summary);
                return response;
            }

            response.StatusCode = HttpStatusCode.OK;
            response.Headers.Add("Content-Type", "application/json");

            var result = new CliReleaseSummaryResponse
            {
                RequestedVersion = versionParam.Trim(),
                Version = entry.Title,
                Updated = entry.Updated.UtcDateTime,
                Link = entry.Link,
                Summary = summary
            };

            await response.WriteStringAsync(JsonSerializer.Serialize(result, JsonOptions));
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in CliReleaseSummary");
            response.StatusCode = HttpStatusCode.InternalServerError;
            await response.WriteStringAsync("An error occurred while processing your request.");
            return response;
        }
    }

    private static ReleaseEntry? FindEntryByVersion(IEnumerable<ReleaseEntry> entries, string versionParam)
    {
        var normalizedRequested = NormalizeVersion(versionParam);

        foreach (var entry in entries)
        {
            var normalizedTitle = NormalizeVersion(entry.Title);
            if (string.Equals(normalizedTitle, normalizedRequested, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        foreach (var entry in entries)
        {
            if (entry.Title.Contains(versionParam.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        return null;
    }

    private static string NormalizeVersion(string version)
    {
        var trimmed = version.Trim();
        if (trimmed.StartsWith('v') || trimmed.StartsWith('V'))
        {
            trimmed = trimmed[1..];
        }

        return trimmed;
    }

    private static string NormalizeParagraph(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var normalized = text.Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal);

        return Regex.Replace(normalized, "\\s+", " ").Trim();
    }

    private static string? GetQueryParameter(HttpRequestData req, string name)
    {
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        return query[name];
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
}

public class CliReleaseSummaryResponse
{
    public required string RequestedVersion { get; set; }
    public required string Version { get; set; }
    public required DateTime Updated { get; set; }
    public required string Link { get; set; }
    public required string Summary { get; set; }
}
