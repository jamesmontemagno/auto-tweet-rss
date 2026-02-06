using System.Globalization;
using AutoTweetRss.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace AutoTweetRss.Functions;

public class VSCodeInsidersChangelogTweetFunction
{
    private readonly ILogger<VSCodeInsidersChangelogTweetFunction> _logger;
    private readonly VSCodeReleaseNotesService _releaseNotesService;
    private readonly TweetFormatterService _tweetFormatterService;
    private readonly VSCodeTwitterApiClient _twitterApiClient;
    private readonly StateTrackingService _stateTrackingService;

    private const string StateFileName = "vscode-insiders-changelog-last-date.txt";

    public VSCodeInsidersChangelogTweetFunction(
        ILogger<VSCodeInsidersChangelogTweetFunction> logger,
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

    [Function("VSCodeInsidersChangelogTweet")]
    public async Task Run([TimerTrigger("0 0 22,23 * * *")] TimerInfo timerInfo)
    {
        _logger.LogInformation("VSCodeInsidersChangelogTweet function started at: {Time}", DateTime.UtcNow);

        try
        {
            var pacificTimeZone = GetPacificTimeZone();
            var nowUtc = DateTimeOffset.UtcNow;
            var nowPacific = TimeZoneInfo.ConvertTime(nowUtc, pacificTimeZone);

            if (nowPacific.Hour != 15)
            {
                _logger.LogInformation("Skipping run outside 3pm Pacific window. Local time: {LocalTime}", nowPacific);
                return;
            }

            var today = nowPacific.Date;
            var lastState = await _stateTrackingService.GetLastProcessedIdAsync(StateFileName);
            DateTime startDate;

            if (string.IsNullOrWhiteSpace(lastState))
            {
                _logger.LogWarning("No VS Code changelog state found. Using today only: {Date}", today);
                startDate = today;
            }
            else if (!DateTime.TryParseExact(lastState, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var lastDate))
            {
                _logger.LogWarning("Invalid VS Code changelog state '{State}'. Using today only: {Date}", lastState, today);
                startDate = today;
            }
            else
            {
                startDate = lastDate.AddDays(1);
            }

            if (startDate > today)
            {
                _logger.LogInformation("No new days to process. Last state: {LastState}", lastState ?? "<none>");
                return;
            }

            _logger.LogInformation("Fetching VS Code updates for {StartDate} to {EndDate}",
                startDate.ToString("yyyy-MM-dd"), today.ToString("yyyy-MM-dd"));

            var notes = await _releaseNotesService.GetReleaseNotesForDateRangeAsync(startDate, today);
            if (notes == null || notes.Features.Count == 0)
            {
                _logger.LogInformation("No VS Code updates found for {StartDate} to {EndDate}",
                    startDate.ToString("yyyy-MM-dd"), today.ToString("yyyy-MM-dd"));
                return;
            }

            var cacheFormat = $"daily-tweet-{startDate:yyyyMMdd}-{today:yyyyMMdd}";
            var summary = await _releaseNotesService.GenerateSummaryAsync(
                notes,
                maxLength: 220,
                format: cacheFormat,
                forceRefresh: false,
                aiOnly: false,
                isThisWeek: false);

            var url = notes.VersionUrl ?? "https://code.visualstudio.com/updates";
            var tweet = _tweetFormatterService.FormatVSCodeChangelogTweetAsync(summary, startDate, today, url);

            _logger.LogInformation("Formatted VS Code changelog tweet ({Length} chars):\n{Tweet}", tweet.Length, tweet);

            var success = await _twitterApiClient.PostTweetAsync(tweet);
            if (success)
            {
                await _stateTrackingService.SetLastProcessedIdAsync(today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), StateFileName);
                _logger.LogInformation("Successfully tweeted VS Code changelog for {Date}", today.ToString("yyyy-MM-dd"));
            }
            else
            {
                _logger.LogWarning("Failed to tweet VS Code changelog for {Date}", today.ToString("yyyy-MM-dd"));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in VSCodeInsidersChangelogTweet function");
        }

        _logger.LogInformation("VSCodeInsidersChangelogTweet function completed at: {Time}", DateTime.UtcNow);
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
