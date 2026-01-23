using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using AutoTweetRss.Services;

namespace AutoTweetRss.Functions;

public class SdkReleaseNotifierFunction
{
    private readonly ILogger<SdkReleaseNotifierFunction> _logger;
    private readonly RssFeedService _rssFeedService;
    private readonly TwitterApiClient _twitterApiClient;
    private readonly TweetFormatterService _tweetFormatterService;
    private readonly StateTrackingService _stateTrackingService;

    public SdkReleaseNotifierFunction(
        ILogger<SdkReleaseNotifierFunction> logger,
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

    [Function("SdkReleaseNotifier")]
    public async Task Run([TimerTrigger("0 */15 * * * *")] TimerInfo timerInfo)
    {
        // Check if timers are enabled
        var timersEnabled = Environment.GetEnvironmentVariable("ENABLE_TIMERS");
        if (string.IsNullOrEmpty(timersEnabled) || !bool.Parse(timersEnabled))
        {
            _logger.LogInformation("SdkReleaseNotifier timer is disabled via ENABLE_TIMERS configuration");
            return;
        }

        _logger.LogInformation("SdkReleaseNotifier function started at: {Time}", DateTime.UtcNow);

        try
        {
            const string feedUrl = "https://github.com/github/copilot-sdk/releases.atom";
            const string stateFileName = "sdk-last-processed-id.txt";

            // Fetch non-pre-release entries
            var entries = await _rssFeedService.GetNonPreReleaseEntriesAsync(feedUrl, isSdkFeed: true);
            
            if (entries.Count == 0)
            {
                _logger.LogInformation("No stable SDK releases found in feed");
                return;
            }

            // Get last processed entry ID
            var lastProcessedId = await _stateTrackingService.GetLastProcessedIdAsync(stateFileName);
            
            // Find new entries (entries that came after the last processed one)
            var newEntries = GetNewEntries(entries, lastProcessedId);
            
            if (newEntries.Count == 0)
            {
                _logger.LogInformation("No new SDK releases to process");
                return;
            }

            _logger.LogInformation("Found {Count} new SDK release(s) to tweet", newEntries.Count);

            // Process new entries (oldest first to maintain chronological order)
            foreach (var entry in newEntries.OrderBy(e => e.Updated))
            {
                // Format the tweet
                var tweet = await _tweetFormatterService.FormatSdkTweetAsync(entry);
                
                _logger.LogInformation("Formatted SDK tweet ({Length} chars):\n{Tweet}", tweet.Length, tweet);

                // Post to Twitter
                var success = await _twitterApiClient.PostTweetAsync(tweet);
                
                if (success)
                {
                    // Update state after successful post
                    await _stateTrackingService.SetLastProcessedIdAsync(entry.Id, stateFileName);
                    _logger.LogInformation("Successfully tweeted SDK release: {Title}", entry.Title);
                }
                else
                {
                    // Log and skip on failure (as per requirements)
                    _logger.LogWarning("Failed to tweet SDK release: {Title}. Skipping.", entry.Title);
                }

                // Small delay between tweets to avoid rate limiting
                if (newEntries.Count > 1)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in SdkReleaseNotifier function");
        }

        _logger.LogInformation("SdkReleaseNotifier function completed at: {Time}", DateTime.UtcNow);
    }

    private List<ReleaseEntry> GetNewEntries(List<ReleaseEntry> entries, string? lastProcessedId)
    {
        if (string.IsNullOrEmpty(lastProcessedId))
        {
            // First run - only process the most recent entry to avoid spamming
            _logger.LogInformation("First run detected. Processing only the most recent SDK release.");
            return entries.OrderByDescending(e => e.Updated).Take(1).ToList();
        }

        var newEntries = new List<ReleaseEntry>();
        
        foreach (var entry in entries)
        {
            if (string.Equals(entry.Id, lastProcessedId, StringComparison.OrdinalIgnoreCase))
            {
                // Found the last processed entry, stop looking
                break;
            }
            newEntries.Add(entry);
        }

        return newEntries;
    }
}
