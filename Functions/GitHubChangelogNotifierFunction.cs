using AutoTweetRss.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace AutoTweetRss.Functions;

public class GitHubChangelogNotifierFunction
{
    private const string StateFileName = "github-changelog-last-processed-id.txt";
    private const int MaxPostedIdHistory = 200;

    private readonly ILogger<GitHubChangelogNotifierFunction> _logger;
    private readonly GitHubChangelogFeedService _feedService;
    private readonly GitHubChangelogTwitterApiClient _twitterApiClient;
    private readonly TweetFormatterService _tweetFormatterService;
    private readonly StateTrackingService _stateTrackingService;

    public GitHubChangelogNotifierFunction(
        ILogger<GitHubChangelogNotifierFunction> logger,
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

    [Function("GitHubChangelogNotifier")]
    public async Task Run([TimerTrigger("0 */15 * * * *")] TimerInfo timerInfo)
    {
        _logger.LogInformation("GitHubChangelogNotifier started at: {Time}", DateTime.UtcNow);

        if (!_twitterApiClient.IsConfigured)
        {
            _logger.LogWarning("GitHub changelog Twitter credentials are not configured. Skipping.");
            return;
        }

        try
        {
            var entries = await _feedService.GetEntriesAsync();
            if (entries.Count == 0)
            {
                _logger.LogInformation("No GitHub changelog entries found.");
                return;
            }

            var state = await _stateTrackingService.GetStateAsync(
                StateFileName,
                GitHubChangelogPostingState.FromLegacyId);
            var newEntries = GetNewEntries(entries, state);
            if (newEntries.Count == 0)
            {
                _logger.LogInformation("No new GitHub changelog entries to process.");
                return;
            }

            _logger.LogInformation("Found {Count} new GitHub changelog entries.", newEntries.Count);

            foreach (var entry in newEntries.OrderBy(entry => entry.Updated))
            {
                var premiumMode = IsEnabled("X_GITHUB_CHANGELOG_PREMIUM_MODE");
                bool success;

                if (premiumMode)
                {
                    var post = await _tweetFormatterService.FormatGitHubChangelogPremiumPostForXAsync(entry, useAi: true);
                    success = await _twitterApiClient.PostTweetAsync(post);
                }
                else
                {
                    var thread = await _tweetFormatterService.FormatGitHubChangelogThreadForXAsync(entry, useAi: true);
                    success = await _twitterApiClient.PostTweetThreadAsync(thread);
                }

                if (success)
                {
                    state ??= new GitHubChangelogPostingState();
                    state.RecordPostedId(entry.Id, MaxPostedIdHistory);
                    await _stateTrackingService.SetStateAsync(state, StateFileName);
                    _logger.LogInformation("Successfully posted GitHub changelog entry: {Title}", entry.Title);
                }
                else
                {
                    _logger.LogWarning("Failed to post GitHub changelog entry: {Title}", entry.Title);
                }

                if (newEntries.Count > 1)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in GitHubChangelogNotifier");
        }

        _logger.LogInformation("GitHubChangelogNotifier completed at: {Time}", DateTime.UtcNow);
    }

    private List<GitHubChangelogEntry> GetNewEntries(
        IReadOnlyList<GitHubChangelogEntry> entries,
        GitHubChangelogPostingState? state)
    {
        var postedIds = new HashSet<string>(
            state?.PostedIds?.Where(id => !string.IsNullOrWhiteSpace(id)) ?? [],
            StringComparer.OrdinalIgnoreCase);
        var lastProcessedId = state?.LastProcessedId;

        if (string.IsNullOrWhiteSpace(lastProcessedId))
        {
            _logger.LogInformation("First GitHub changelog run detected. Processing only the most recent entry.");
            var latestUnpostedEntry = entries
                .OrderByDescending(entry => entry.Updated)
                .FirstOrDefault(entry => !postedIds.Contains(entry.Id));

            return latestUnpostedEntry == null ? [] : [latestUnpostedEntry];
        }

        var newEntries = new List<GitHubChangelogEntry>();
        foreach (var entry in entries)
        {
            if (string.Equals(entry.Id, lastProcessedId, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            if (postedIds.Contains(entry.Id))
            {
                _logger.LogInformation("Skipping already-posted GitHub changelog entry: {Title}", entry.Title);
                continue;
            }

            newEntries.Add(entry);
        }

        return newEntries;
    }

    private static bool IsEnabled(string envVar)
    {
        var value = Environment.GetEnvironmentVariable(envVar);
        return bool.TryParse(value, out var enabled) && enabled;
    }

    private sealed class GitHubChangelogPostingState
    {
        public string? LastProcessedId { get; set; }
        public List<string> PostedIds { get; set; } = [];

        public static GitHubChangelogPostingState? FromLegacyId(string? legacyId)
        {
            if (string.IsNullOrWhiteSpace(legacyId))
            {
                return null;
            }

            var trimmedId = legacyId.Trim();
            return new GitHubChangelogPostingState
            {
                LastProcessedId = trimmedId,
                PostedIds = [trimmedId]
            };
        }

        public void RecordPostedId(string id, int maxPostedIdHistory)
        {
            LastProcessedId = id;
            PostedIds = PostedIds
                .Where(existingId => !string.IsNullOrWhiteSpace(existingId))
                .Where(existingId => !string.Equals(existingId, id, StringComparison.OrdinalIgnoreCase))
                .Append(id)
                .TakeLast(maxPostedIdHistory)
                .ToList();
        }
    }
}
