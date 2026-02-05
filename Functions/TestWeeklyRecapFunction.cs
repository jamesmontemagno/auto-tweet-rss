using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using AutoTweetRss.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutoTweetRss.Functions;

public class TestWeeklyRecapFunction
{
    private readonly ILogger<TestWeeklyRecapFunction> _logger;
    private readonly RssFeedService _rssFeedService;
    private readonly TweetFormatterService _tweetFormatterService;

    private const string FeedUrl = "https://github.com/github/copilot-cli/releases.atom";

    private static readonly Regex ListItemPattern = new(@"<li[^>]*>(.*?)</li>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex HtmlTagPattern = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex WhitespacePattern = new(@"\s+", RegexOptions.Compiled);

    public TestWeeklyRecapFunction(
        ILogger<TestWeeklyRecapFunction> logger,
        RssFeedService rssFeedService,
        TweetFormatterService tweetFormatterService)
    {
        _logger = logger;
        _rssFeedService = rssFeedService;
        _tweetFormatterService = tweetFormatterService;
    }

    [Function("TestWeeklyRecap")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "test-weekly-recap")] HttpRequestData req)
    {
        _logger.LogInformation("TestWeeklyRecap function called at: {Time}", DateTime.UtcNow);

        var response = req.CreateResponse();

        try
        {
            var pacificTimeZone = GetPacificTimeZone();
            var weekEndPacific = GetWeekEndPacific(req, pacificTimeZone);
            var weekStartPacific = weekEndPacific.AddDays(-7);

            var weekStartUtc = weekStartPacific.ToUniversalTime();
            var weekEndUtc = weekEndPacific.ToUniversalTime();

            var entries = await _rssFeedService.GetNonPreReleaseEntriesAsync(FeedUrl);

            var weeklyEntries = entries
                .Where(e => e.Updated >= weekStartUtc && e.Updated <= weekEndUtc)
                .OrderBy(e => e.Updated)
                .ToList();

            if (weeklyEntries.Count == 0)
            {
                response.StatusCode = HttpStatusCode.NotFound;
                await response.WriteStringAsync("No releases found for the weekly window.");
                return response;
            }

            var improvementCount = weeklyEntries.Sum(e => CountImprovements(e.Content));

            var tweet = await _tweetFormatterService.FormatWeeklyCliRecapTweetAsync(
                weeklyEntries,
                weekStartPacific,
                weekEndPacific,
                improvementCount,
                useAi: true);

            response.StatusCode = HttpStatusCode.OK;
            response.Headers.Add("Content-Type", "text/plain; charset=utf-8");

            var output = $"Weekly window (PT): {weekStartPacific:yyyy-MM-dd} to {weekEndPacific:yyyy-MM-dd}\n";
            output += $"Releases: {weeklyEntries.Count}\n";
            output += $"Improvements: {improvementCount}\n";
            output += $"\nFormatted Tweet ({tweet.Length} chars):\n";
            output += "═══════════════════════════════════════\n";
            output += tweet;

            await response.WriteStringAsync(output);
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating weekly recap test output");
            response.StatusCode = HttpStatusCode.InternalServerError;
            await response.WriteStringAsync($"Error: {ex.Message}");
            return response;
        }
    }

    private static DateTimeOffset GetWeekEndPacific(HttpRequestData req, TimeZoneInfo pacificTimeZone)
    {
        var dateParam = GetQueryParameter(req, "date");
        if (!string.IsNullOrWhiteSpace(dateParam) &&
            DateTime.TryParseExact(dateParam, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            var localDateTime = new DateTime(date.Year, date.Month, date.Day, 10, 0, 0, DateTimeKind.Unspecified);
            return new DateTimeOffset(localDateTime, pacificTimeZone.GetUtcOffset(localDateTime));
        }

        var nowUtc = DateTimeOffset.UtcNow;
        return TimeZoneInfo.ConvertTime(nowUtc, pacificTimeZone);
    }

    private static string? GetQueryParameter(HttpRequestData req, string name)
    {
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        return query[name];
    }

    private static int CountImprovements(string htmlContent)
    {
        if (string.IsNullOrWhiteSpace(htmlContent))
        {
            return 0;
        }

        var decoded = WebUtility.HtmlDecode(htmlContent);
        var newContributorsIndex = decoded.IndexOf("New Contributors", StringComparison.OrdinalIgnoreCase);
        var contentToCount = newContributorsIndex >= 0 ? decoded[..newContributorsIndex] : decoded;

        var matches = ListItemPattern.Matches(contentToCount);
        var count = 0;

        foreach (Match match in matches)
        {
            var text = StripHtml(match.Groups[1].Value).Trim();
            if (!string.IsNullOrWhiteSpace(text) &&
                !text.StartsWith("Full Changelog", StringComparison.OrdinalIgnoreCase) &&
                !text.Contains("made their first contribution", StringComparison.OrdinalIgnoreCase))
            {
                count++;
            }
        }

        return count;
    }

    private static string StripHtml(string html)
    {
        var withoutTags = HtmlTagPattern.Replace(html, " ");
        var normalized = WhitespacePattern.Replace(withoutTags, " ");
        return normalized.Trim();
    }

    private static TimeZoneInfo GetPacificTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");
        }
    }
}
