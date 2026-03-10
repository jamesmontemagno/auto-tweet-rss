using System.Globalization;
using AutoTweetRss.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace AutoTweetRss.Functions;

public class GitHubChangelogWeeklyRecapFunction
{
    private const string StateFileName = "github-changelog-weekly-recap-last-date.txt";

    private readonly ILogger<GitHubChangelogWeeklyRecapFunction> _logger;
    private readonly GitHubChangelogFeedService _feedService;
    private readonly GitHubChangelogTwitterApiClient _twitterApiClient;
    private readonly TweetFormatterService _tweetFormatterService;
    private readonly StateTrackingService _stateTrackingService;

    public GitHubChangelogWeeklyRecapFunction(
        ILogger<GitHubChangelogWeeklyRecapFunction> logger,
        GitHubChangelogFeedService feedService,
        GitHubChangelogTwitterApiClient twitterApiClient,
        TweetFormatterService tweetFormatterService,
        StateTrackingService stateTrackingService)
    {
        _logger = logger;
        _feedService = feedService;
        _twitterApiClient = twitterApiClient;
        _tweetFormatterService = tweetFormatterService;
        _stateTrackingService = stateTrackingService;
    }

    [Function("GitHubChangelogWeeklyRecap")]
    public async Task Run([TimerTrigger("0 0 17,18 * * 6")] TimerInfo timerInfo)
    {
        _logger.LogInformation("GitHubChangelogWeeklyRecap started at: {Time}", DateTime.UtcNow);

        if (!_twitterApiClient.IsConfigured)
        {
            _logger.LogWarning("GitHub changelog Twitter credentials are not configured. Skipping.");
            return;
        }

        if (!IsEnabled("ENABLE_GITHUB_CHANGELOG_WEEKLY_RECAP"))
        {
            _logger.LogInformation("GitHub changelog weekly recap is disabled.");
            return;
        }

        try
        {
            var pacificTimeZone = GetPacificTimeZone();
            var nowPacific = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, pacificTimeZone);
            if (nowPacific.DayOfWeek != DayOfWeek.Saturday || nowPacific.Hour != 10)
            {
                _logger.LogInformation("Skipping weekly recap outside 10am Pacific window. Local time: {LocalTime}", nowPacific);
                return;
            }

            var todayKey = nowPacific.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var lastRunDate = await _stateTrackingService.GetLastProcessedIdAsync(StateFileName);
            if (string.Equals(lastRunDate, todayKey, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("GitHub changelog weekly recap already posted for {Date}", todayKey);
                return;
            }

            var weekEndPacific = nowPacific;
            var weekStartPacific = weekEndPacific.AddDays(-7);
            var weekStartUtc = weekStartPacific.ToUniversalTime();
            var weekEndUtc = weekEndPacific.ToUniversalTime();

            var entries = await _feedService.GetEntriesAsync();
            var weeklyEntries = entries
                .Where(entry => entry.Updated >= weekStartUtc && entry.Updated <= weekEndUtc)
                .OrderBy(entry => entry.Updated)
                .ToList();

            if (weeklyEntries.Count == 0)
            {
                _logger.LogInformation("No GitHub changelog entries found for weekly window {Start} - {End}.", weekStartUtc, weekEndUtc);
                return;
            }

            var premiumMode = IsEnabled("X_GITHUB_CHANGELOG_PREMIUM_MODE");
            bool success;

            if (premiumMode)
            {
                var post = await _tweetFormatterService.FormatGitHubChangelogWeeklyRecapPremiumPostForXAsync(
                    weeklyEntries,
                    weekStartPacific,
                    weekEndPacific,
                    useAi: true);
                success = await _twitterApiClient.PostTweetAsync(post);
            }
            else
            {
                var thread = await _tweetFormatterService.FormatGitHubChangelogWeeklyRecapThreadForXAsync(
                    weeklyEntries,
                    weekStartPacific,
                    weekEndPacific,
                    useAi: true);
                success = await _twitterApiClient.PostTweetThreadAsync(thread);
            }

            if (success)
            {
                await _stateTrackingService.SetLastProcessedIdAsync(todayKey, StateFileName);
                _logger.LogInformation("Successfully posted GitHub changelog weekly recap for {Date}", todayKey);
            }
            else
            {
                _logger.LogWarning("Failed to post GitHub changelog weekly recap for {Date}", todayKey);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in GitHubChangelogWeeklyRecap");
        }

        _logger.LogInformation("GitHubChangelogWeeklyRecap completed at: {Time}", DateTime.UtcNow);
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

    private static bool IsEnabled(string envVar)
    {
        var value = Environment.GetEnvironmentVariable(envVar);
        return bool.TryParse(value, out var enabled) && enabled;
    }
}
