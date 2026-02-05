using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using AutoTweetRss.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace AutoTweetRss.Functions;

public class WeeklyCliRecapFunction
{
    private readonly ILogger<WeeklyCliRecapFunction> _logger;
    private readonly RssFeedService _rssFeedService;
    private readonly TwitterApiClient _twitterApiClient;
    private readonly TweetFormatterService _tweetFormatterService;
    private readonly StateTrackingService _stateTrackingService;

    private const string FeedUrl = "https://github.com/github/copilot-cli/releases.atom";
    private const string StateFileName = "cli-weekly-recap-last-date.txt";

    private static readonly Regex ListItemPattern = new(@"<li[^>]*>(.*?)</li>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex HtmlTagPattern = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex WhitespacePattern = new(@"\s+", RegexOptions.Compiled);

    public WeeklyCliRecapFunction(
        ILogger<WeeklyCliRecapFunction> logger,
        RssFeedService rssFeedService,
        TwitterApiClient twitterApiClient,
        TweetFormatterService tweetFormatterService,
        StateTrackingService stateTrackingService)
    {
        _logger = logger;
        _rssFeedService = rssFeedService;
        _twitterApiClient = twitterApiClient;
        _tweetFormatterService = tweetFormatterService;
        _stateTrackingService = stateTrackingService;
    }

    [Function("WeeklyCliRecap")]
    public async Task Run([TimerTrigger("0 0 17,18 * * 6")] TimerInfo timerInfo)
    {
        _logger.LogInformation("WeeklyCliRecap function started at: {Time}", DateTime.UtcNow);

        try
        {
            var pacificTimeZone = GetPacificTimeZone();
            var nowUtc = DateTimeOffset.UtcNow;
            var nowPacific = TimeZoneInfo.ConvertTime(nowUtc, pacificTimeZone);

            if (nowPacific.DayOfWeek != DayOfWeek.Saturday || nowPacific.Hour != 10)
            {
                _logger.LogInformation("Skipping run outside 10am Pacific window. Local time: {LocalTime}", nowPacific);
                return;
            }

            var todayKey = nowPacific.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var lastRunDate = await _stateTrackingService.GetLastProcessedIdAsync(StateFileName);

            if (string.Equals(lastRunDate, todayKey, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Weekly recap already posted for {Date}", todayKey);
                return;
            }

            var weekEndPacific = nowPacific;
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
                _logger.LogInformation("No releases found for weekly window {Start} - {End}", weekStartUtc, weekEndUtc);
                return;
            }

            var improvementCount = weeklyEntries.Sum(e => CountImprovements(e.Content));

            var tweet = await _tweetFormatterService.FormatWeeklyCliRecapTweetAsync(
                weeklyEntries,
                weekStartPacific,
                weekEndPacific,
                improvementCount,
                useAi: true);

            _logger.LogInformation("Formatted weekly recap tweet ({Length} chars):\n{Tweet}", tweet.Length, tweet);

            var success = await _twitterApiClient.PostTweetAsync(tweet);

            if (success)
            {
                await _stateTrackingService.SetLastProcessedIdAsync(todayKey, StateFileName);
                _logger.LogInformation("Successfully tweeted weekly recap for {Date}", todayKey);
            }
            else
            {
                _logger.LogWarning("Failed to tweet weekly recap for {Date}. Skipping.", todayKey);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in WeeklyCliRecap function");
        }

        _logger.LogInformation("WeeklyCliRecap function completed at: {Time}", DateTime.UtcNow);
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
