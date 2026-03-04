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
    private readonly VSCodeSocialMediaPublisher _publisher;
    private readonly StateTrackingService _stateTrackingService;

    private const string StateFileName = "vscode-insiders-changelog-last-date.txt";

    public VSCodeInsidersChangelogTweetFunction(
        ILogger<VSCodeInsidersChangelogTweetFunction> logger,
        VSCodeReleaseNotesService releaseNotesService,
        TweetFormatterService tweetFormatterService,
        VSCodeSocialMediaPublisher publisher,
        StateTrackingService stateTrackingService)
    {
        _logger = logger;
        _releaseNotesService = releaseNotesService;
        _tweetFormatterService = tweetFormatterService;
        _publisher = publisher;
        _stateTrackingService = stateTrackingService;
    }

    /// <summary>
    /// Polls every 30 minutes to check if VS Code Insiders release notes have new entries after the last posted release-note date.
    /// Fetches only strictly newer dates so each release-note day is posted once.
    /// </summary>
    [Function("VSCodeInsidersChangelogTweet")]
    public async Task Run([TimerTrigger("0 */30 * * * *")] TimerInfo timerInfo)
    {
        _logger.LogInformation("VSCodeInsidersChangelogTweet function started at: {Time}", DateTime.UtcNow);

        if (!_publisher.IsConfigured)
        {
            _logger.LogWarning("No social media platforms configured for VS Code. Skipping.");
            return;
        }

        try
        {
            // Use Pacific Time to determine "today" since VS Code dates align with PT
            var pacificTimeZone = GetPacificTimeZone();
            var nowPacific = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, pacificTimeZone);
            var today = nowPacific.Date;
            var todayString = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            var lastState = await _stateTrackingService.GetLastProcessedIdAsync(StateFileName);
            var startDate = today;
            DateTime? lastReleaseDate = null;

            if (!string.IsNullOrWhiteSpace(lastState))
            {
                var stateParts = lastState.Split('|', 2, StringSplitOptions.TrimEntries);
                var stateDate = stateParts[0];

                if (DateTime.TryParseExact(
                    stateDate,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var parsedLastDate))
                {
                    lastReleaseDate = parsedLastDate.Date;
                    if (lastReleaseDate.Value >= today)
                    {
                        _logger.LogInformation(
                            "Latest posted release-note date is {Date}; no newer dates available yet. Skipping.",
                            lastReleaseDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                        return;
                    }

                    startDate = lastReleaseDate.Value.AddDays(1);

                    _logger.LogInformation(
                        "Loaded previous changelog state: last posted release-note date {Date}. Checking newer dates starting {StartDate}.",
                        lastReleaseDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        startDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                }
                else
                {
                    _logger.LogWarning("Could not parse previous state value '{LastState}' as yyyy-MM-dd[|hash]. Falling back to today only.", lastState);
                }
            }

            _logger.LogInformation("Checking VS Code changelog updates from {StartDate} to {EndDate} (inclusive)",
                startDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                todayString);

            var notes = await _releaseNotesService.GetReleaseNotesForDateRangeAsync(startDate, today);
            if (notes == null || notes.Features.Count == 0)
            {
                _logger.LogInformation("No VS Code Insiders release notes found for range {StartDate} to {EndDate}.",
                    startDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    todayString);
                return;
            }

            var latestReleaseDate = (notes.LatestFeatureDate ?? today).Date;
            var latestReleaseDateString = latestReleaseDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            if (lastReleaseDate.HasValue
                && latestReleaseDate <= lastReleaseDate.Value.Date)
            {
                _logger.LogInformation(
                    "Latest release-note date {LatestDate} is not newer than last posted date {LastDate}. Skipping.",
                    latestReleaseDateString,
                    lastReleaseDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                return;
            }

            _logger.LogInformation("Found {Count} features for range {StartDate} to {EndDate}. Preparing tweet.",
                notes.Features.Count,
                startDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                latestReleaseDateString);

            var cacheFormat = $"daily-tweet-{startDate:yyyyMMdd}-{latestReleaseDate:yyyyMMdd}";
            var summary = await _releaseNotesService.GenerateSummaryAsync(
                notes,
                maxLength: 800,
                format: cacheFormat,
                forceRefresh: false,
                aiOnly: false,
                isThisWeek: false);

            var useXPremiumMode = IsEnabled("X_VSCODE_PREMIUM_MODE");
            var xPosts = useXPremiumMode
                ? new List<string>
                {
                    _tweetFormatterService.FormatVSCodeChangelogPremiumPostForX(
                        notes.Features,
                        notes.Features.Count,
                        startDate,
                        latestReleaseDate,
                        notes.WebsiteUrl)
                }
                : _tweetFormatterService.FormatVSCodeChangelogThreadForX(summary, notes.Features.Count, startDate, latestReleaseDate, notes.WebsiteUrl).ToList();
            var blueskyThread = _tweetFormatterService.FormatVSCodeChangelogThreadForBluesky(summary, notes.Features.Count, startDate, latestReleaseDate, notes.WebsiteUrl);

            _logger.LogInformation("Formatted VS Code changelog X {Mode} ({PostCount} posts, first {Length} chars):\n{Post}",
                useXPremiumMode ? "premium post" : "thread",
                xPosts.Count,
                xPosts[0].Length,
                xPosts[0]);
            _logger.LogInformation("Formatted VS Code changelog Bluesky thread ({PostCount} posts, first {Length} chars):\n{Post}",
                blueskyThread.Count, blueskyThread[0].Length, blueskyThread[0]);

            var success = await _publisher.PostThreadToAllAsync(client =>
                string.Equals(client.PlatformName, "Bluesky", StringComparison.OrdinalIgnoreCase)
                    ? blueskyThread
                    : xPosts);
            if (success)
            {
                _logger.LogInformation(
                    "Persisting VS Code changelog state as latest release-note date {Date} into {StateFileName}.",
                    latestReleaseDateString,
                    StateFileName);
                await _stateTrackingService.SetLastProcessedIdAsync(latestReleaseDateString, StateFileName);
                _logger.LogInformation("Successfully posted VS Code changelog for range {StartDate} to {EndDate}",
                    startDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    latestReleaseDateString);
            }
            else
            {
                _logger.LogWarning("Failed to post VS Code changelog for range {StartDate} to {EndDate}",
                    startDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    latestReleaseDateString);
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

    private static bool IsEnabled(string envVar)
    {
        var value = Environment.GetEnvironmentVariable(envVar);
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return bool.TryParse(value, out var enabled) && enabled;
    }
}
