using System.Globalization;
using AutoTweetRss.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace AutoTweetRss.Functions;

public class VSCodeWeeklyRecapFunction
{
    private readonly ILogger<VSCodeWeeklyRecapFunction> _logger;
    private readonly VSCodeReleaseNotesService _releaseNotesService;
    private readonly TweetFormatterService _tweetFormatterService;
    private readonly VSCodeTwitterApiClient _twitterApiClient;
    private readonly StateTrackingService _stateTrackingService;

    private const string StateFileName = "vscode-weekly-recap-last-date.txt";

    public VSCodeWeeklyRecapFunction(
        ILogger<VSCodeWeeklyRecapFunction> logger,
        VSCodeReleaseNotesService releaseNotesService,
        TweetFormatterService tweetFormatterService,
        VSCodeTwitterApiClient twitterApiClient,
        StateTrackingService stateTrackingService)
    {
        _logger = logger;
        _releaseNotesService = releaseNotesService;
        _tweetFormatterService = tweetFormatterService;
        _twitterApiClient = twitterApiClient;
        _stateTrackingService = stateTrackingService;
    }

    [Function("VSCodeWeeklyRecap")]
    public async Task Run([TimerTrigger("0 0 18,19 * * 6")] TimerInfo timerInfo)
    {
        _logger.LogInformation("VSCodeWeeklyRecap function started at: {Time}", DateTime.UtcNow);

        if (!_twitterApiClient.IsConfigured)
        {
            _logger.LogWarning("VS Code Twitter credentials not configured. Skipping.");
            return;
        }

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

            var weekEndDate = nowPacific.Date;
            var todayKey = weekEndDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var lastRunDate = await _stateTrackingService.GetLastProcessedIdAsync(StateFileName);

            if (string.Equals(lastRunDate, todayKey, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Weekly recap already posted for {Date}", todayKey);
                return;
            }

            var weekStartDate = weekEndDate.AddDays(-6);

            _logger.LogInformation("Fetching VS Code updates for {StartDate} to {EndDate}",
                weekStartDate.ToString("yyyy-MM-dd"), weekEndDate.ToString("yyyy-MM-dd"));

            var notes = await _releaseNotesService.GetReleaseNotesForDateRangeAsync(weekStartDate, weekEndDate);
            if (notes == null || notes.Features.Count == 0)
            {
                _logger.LogInformation("No VS Code updates found for {StartDate} to {EndDate}",
                    weekStartDate.ToString("yyyy-MM-dd"), weekEndDate.ToString("yyyy-MM-dd"));
                return;
            }

            var cacheFormat = $"weekly-tweet-{weekStartDate:yyyyMMdd}-{weekEndDate:yyyyMMdd}";
            var summary = await _releaseNotesService.GenerateSummaryAsync(
                notes,
                maxLength: 220,
                format: cacheFormat,
                forceRefresh: false,
                aiOnly: false,
                isThisWeek: true);

            var url = "https://aka.ms/vscode/updates/insiders";
            var tweet = _tweetFormatterService.FormatVSCodeChangelogTweet(summary, weekStartDate, weekEndDate, url);

            _logger.LogInformation("Formatted VS Code weekly recap tweet ({Length} chars):\n{Tweet}", tweet.Length, tweet);

            var success = await _twitterApiClient.PostTweetAsync(tweet);
            if (success)
            {
                await _stateTrackingService.SetLastProcessedIdAsync(todayKey, StateFileName);
                _logger.LogInformation("Successfully tweeted VS Code weekly recap for {Date}", todayKey);
            }
            else
            {
                _logger.LogWarning("Failed to tweet VS Code weekly recap for {Date}", todayKey);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in VSCodeWeeklyRecap function");
        }

        _logger.LogInformation("VSCodeWeeklyRecap function completed at: {Time}", DateTime.UtcNow);
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
