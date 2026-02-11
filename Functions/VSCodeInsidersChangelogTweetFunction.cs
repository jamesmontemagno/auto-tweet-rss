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

    /// <summary>
    /// Polls every 30 minutes to check if today's VS Code Insiders release notes have been published.
    /// When new notes for today are found and haven't been tweeted yet, posts a summary tweet.
    /// </summary>
    [Function("VSCodeInsidersChangelogTweet")]
    public async Task Run([TimerTrigger("0 */30 * * * *")] TimerInfo timerInfo)
    {
        _logger.LogInformation("VSCodeInsidersChangelogTweet function started at: {Time}", DateTime.UtcNow);

        if (!_twitterApiClient.IsConfigured)
        {
            _logger.LogWarning("VS Code Twitter credentials not configured. Skipping.");
            return;
        }

        try
        {
            // Use Pacific Time to determine "today" since VS Code dates align with PT
            var pacificTimeZone = GetPacificTimeZone();
            var nowPacific = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, pacificTimeZone);
            var today = nowPacific.Date;
            var todayString = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            // Check if we already tweeted today's notes
            var lastState = await _stateTrackingService.GetLastProcessedIdAsync(StateFileName);
            if (string.Equals(lastState, todayString, StringComparison.Ordinal))
            {
                _logger.LogInformation("Already tweeted VS Code changelog for today ({Date}). Skipping.", todayString);
                return;
            }

            // Check if today's release notes exist in the markdown
            var notes = await _releaseNotesService.GetReleaseNotesForDateAsync(today);
            if (notes == null || notes.Features.Count == 0)
            {
                _logger.LogInformation("No VS Code Insiders release notes found for today ({Date}).", todayString);
                return;
            }

            _logger.LogInformation("Found {Count} features for today ({Date}). Preparing tweet.",
                notes.Features.Count, todayString);

            var cacheFormat = $"daily-tweet-{today:yyyyMMdd}";
            var summary = await _releaseNotesService.GenerateSummaryAsync(
                notes,
                maxLength: 220,
                format: cacheFormat,
                forceRefresh: false,
                aiOnly: false,
                isThisWeek: false);

            var tweet = _tweetFormatterService.FormatVSCodeChangelogTweet(summary, today, today, notes.WebsiteUrl);

            _logger.LogInformation("Formatted VS Code changelog tweet ({Length} chars):\n{Tweet}", tweet.Length, tweet);

            var success = await _twitterApiClient.PostTweetAsync(tweet);
            if (success)
            {
                await _stateTrackingService.SetLastProcessedIdAsync(todayString, StateFileName);
                _logger.LogInformation("Successfully tweeted VS Code changelog for {Date}", todayString);
            }
            else
            {
                _logger.LogWarning("Failed to tweet VS Code changelog for {Date}", todayString);
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
