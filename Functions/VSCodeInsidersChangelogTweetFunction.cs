using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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
    /// Polls every 30 minutes to check if VS Code Insiders release notes have been updated since the previous tweet.
    /// Fetches an inclusive date range from the last tweeted date through today so delayed backfills are included.
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
            string? lastNotesHash = null;

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
                    startDate = parsedLastDate.Date <= today ? parsedLastDate.Date : today;

                    if (stateParts.Length > 1 && !string.IsNullOrWhiteSpace(stateParts[1]))
                    {
                        lastNotesHash = stateParts[1];
                    }

                    _logger.LogInformation(
                        "Loaded previous changelog state: release date {Date}{HashSuffix}.",
                        lastReleaseDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        string.IsNullOrWhiteSpace(lastNotesHash) ? string.Empty : $", hash {lastNotesHash}");
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
            var notesHash = ComputeNotesHash(notes, latestReleaseDateString);

            if (lastReleaseDate.HasValue
                && string.Equals(lastNotesHash, notesHash, StringComparison.Ordinal)
                && lastReleaseDate.Value.Date == latestReleaseDate)
            {
                _logger.LogInformation(
                    "No changelog content changes since last post. Last release date {Date} with hash {Hash}. Skipping.",
                    latestReleaseDateString,
                    notesHash);
                return;
            }

            _logger.LogInformation("Found {Count} features for range {StartDate} to {EndDate}. Preparing tweet.",
                notes.Features.Count,
                startDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                latestReleaseDateString);

            var cacheFormat = $"daily-tweet-{startDate:yyyyMMdd}-{latestReleaseDate:yyyyMMdd}";
            var summary = await _releaseNotesService.GenerateSummaryAsync(
                notes,
                maxLength: 260,
                format: cacheFormat,
                forceRefresh: false,
                aiOnly: false,
                isThisWeek: false);

            var xPost = _tweetFormatterService.FormatVSCodeChangelogTweetForX(summary, startDate, latestReleaseDate, notes.WebsiteUrl);
            var blueskyPost = _tweetFormatterService.FormatVSCodeChangelogTweetForBluesky(summary, startDate, latestReleaseDate, notes.WebsiteUrl);

            _logger.LogInformation("Formatted VS Code changelog X post ({Length} chars):\n{Post}", xPost.Length, xPost);
            _logger.LogInformation("Formatted VS Code changelog Bluesky post ({Length} chars):\n{Post}", blueskyPost.Length, blueskyPost);

            var success = await _publisher.PostToAllAsync(client =>
                string.Equals(client.PlatformName, "Bluesky", StringComparison.OrdinalIgnoreCase)
                    ? blueskyPost
                    : xPost);
            if (success)
            {
                var stateToPersist = $"{latestReleaseDateString}|{notesHash}";
                _logger.LogInformation(
                    "Persisting VS Code changelog state as latest release date {Date} with hash {Hash} into {StateFileName}.",
                    latestReleaseDateString,
                    notesHash,
                    StateFileName);
                await _stateTrackingService.SetLastProcessedIdAsync(stateToPersist, StateFileName);
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

    private static string ComputeNotesHash(VSCodeReleaseNotes notes, string latestReleaseDate)
    {
        var builder = new StringBuilder();
        builder.Append(latestReleaseDate);
        builder.Append('|');
        builder.Append(notes.VersionUrl);

        foreach (var feature in notes.Features
            .OrderBy(f => f.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Description, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Category ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Link ?? string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append('|');
            builder.Append(feature.Title);
            builder.Append('|');
            builder.Append(feature.Description);
            builder.Append('|');
            builder.Append(feature.Category ?? string.Empty);
            builder.Append('|');
            builder.Append(feature.Link ?? string.Empty);
        }

        var bytes = Encoding.UTF8.GetBytes(builder.ToString());
        var hashBytes = SHA256.HashData(bytes);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}
