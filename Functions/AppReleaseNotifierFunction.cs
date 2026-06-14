using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using AutoTweetRss.Services;

namespace AutoTweetRss.Functions;

public class AppReleaseNotifierFunction
{
    private readonly ILogger<AppReleaseNotifierFunction> _logger;
    private readonly RssFeedService _rssFeedService;
    private readonly TwitterApiClient _twitterApiClient;
    private readonly TweetFormatterService _tweetFormatterService;
    private readonly StateTrackingService _stateTrackingService;

    public AppReleaseNotifierFunction(
        ILogger<AppReleaseNotifierFunction> logger,
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

    [Function("AppReleaseNotifier")]
    public async Task Run([TimerTrigger("0 */15 * * * *")] TimerInfo timerInfo)
    {
        _logger.LogInformation("AppReleaseNotifier function started at: {Time}", DateTime.UtcNow);

        try
        {
            const string feedUrl = "https://github.com/github/app/releases.atom";
            const string stateFileName = "app-last-processed-id.txt";

            var entries = await _rssFeedService.GetNonPreReleaseEntriesAsync(feedUrl);
            if (entries.Count == 0)
            {
                _logger.LogInformation("No stable App releases found in feed");
                return;
            }

            var lastProcessedId = await _stateTrackingService.GetLastProcessedIdAsync(stateFileName);
            var newEntries = GetNewEntries(entries, lastProcessedId);

            if (newEntries.Count == 0)
            {
                _logger.LogInformation("No new App releases to process");
                return;
            }

            _logger.LogInformation("Found {Count} new App release(s) to tweet", newEntries.Count);

            foreach (var entry in newEntries.OrderBy(e => e.Updated))
            {
                var usePremiumMode = IsEnabled("X_CLI_CHANGELOG_PREMIUM_MODE");
                bool success;

                if (usePremiumMode)
                {
                    var premiumPost = await _tweetFormatterService.FormatAppPremiumPostForXAsync(entry);
                    _logger.LogInformation("Formatted App premium post ({Length} chars):\n{Post}",
                        premiumPost.Length, premiumPost);
                    success = await _twitterApiClient.PostTweetAsync(premiumPost);
                }
                else
                {
                    var thread = await _tweetFormatterService.FormatAppThreadForXAsync(entry);
                    _logger.LogInformation("Formatted App thread ({PostCount} posts, first {Length} chars):\n{Post}",
                        thread.Count, thread[0].Length, thread[0]);
                    success = await _twitterApiClient.PostTweetThreadAsync(thread);
                }

                if (success)
                {
                    await _stateTrackingService.SetLastProcessedIdAsync(entry.Id, stateFileName);
                    _logger.LogInformation("Successfully tweeted App thread: {Title}", entry.Title);
                }
                else
                {
                    _logger.LogWarning("Failed to tweet App thread: {Title}. Skipping.", entry.Title);
                }

                if (newEntries.Count > 1)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in AppReleaseNotifier function");
        }

        _logger.LogInformation("AppReleaseNotifier function completed at: {Time}", DateTime.UtcNow);
    }

    private List<ReleaseEntry> GetNewEntries(List<ReleaseEntry> entries, string? lastProcessedId)
    {
        if (string.IsNullOrEmpty(lastProcessedId))
        {
            _logger.LogInformation("First run detected. Processing only the most recent App release.");
            return entries.OrderByDescending(e => e.Updated).Take(1).ToList();
        }

        var newEntries = new List<ReleaseEntry>();
        foreach (var entry in entries)
        {
            if (string.Equals(entry.Id, lastProcessedId, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            newEntries.Add(entry);
        }

        return newEntries;
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
