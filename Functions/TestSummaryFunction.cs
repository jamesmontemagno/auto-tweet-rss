using System.Globalization;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using AutoTweetRss.Services;
using System.Net;

namespace AutoTweetRss.Functions;

public class TestSummaryFunction
{
    private readonly ILogger<TestSummaryFunction> _logger;
    private readonly RssFeedService _rssFeedService;
    private readonly GitHubChangelogFeedService _gitHubChangelogFeedService;
    private readonly TweetFormatterService _tweetFormatterService;
    private readonly VSCodeReleaseNotesService _vsCodeReleaseNotesService;

    public TestSummaryFunction(
        ILogger<TestSummaryFunction> logger,
        RssFeedService rssFeedService,
        GitHubChangelogFeedService gitHubChangelogFeedService,
        TweetFormatterService tweetFormatterService,
        VSCodeReleaseNotesService vsCodeReleaseNotesService)
    {
        _logger = logger;
        _rssFeedService = rssFeedService;
        _gitHubChangelogFeedService = gitHubChangelogFeedService;
        _tweetFormatterService = tweetFormatterService;
        _vsCodeReleaseNotesService = vsCodeReleaseNotesService;
    }

    [Function("TestSummary")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "test-summary/{type}")] HttpRequestData req,
        string type)
    {
        _logger.LogInformation("TestSummary function called for type: {Type}", type);

        var response = req.CreateResponse();

        try
        {
            var typeLower = type.ToLowerInvariant();
            var premiumMode = IsEnabledQuery(req, "premium");
            var singleMode = IsEnabledQuery(req, "single");

            // Validate type parameter
            if (typeLower != "cli" && typeLower != "sdk" && typeLower != "vscode" && typeLower != "github-changelog")
            {
                response.StatusCode = HttpStatusCode.BadRequest;
                await response.WriteStringAsync($"Invalid type: {type}. Must be 'cli', 'sdk', 'vscode', or 'github-changelog'.");
                return response;
            }

            // VS Code path
            if (typeLower == "vscode")
            {
                return await HandleVSCodeTestSummaryAsync(req, response, premiumMode);
            }

            if (typeLower == "github-changelog")
            {
                return await HandleGitHubChangelogTestSummaryAsync(response, premiumMode, singleMode);
            }

            // Determine feed URL based on type
            string feedUrl;
            bool isSdkFeed;
            if (typeLower == "sdk")
            {
                feedUrl = "https://github.com/github/copilot-sdk/releases.atom";
                isSdkFeed = true;
            }
            else
            {
                feedUrl = Environment.GetEnvironmentVariable("RSS_FEED_URL") 
                    ?? "https://github.com/github/copilot-cli/releases.atom";
                isSdkFeed = false;
            }

            _logger.LogInformation("Fetching feed from: {FeedUrl}", feedUrl);

            // Fetch the latest release
            var entries = await _rssFeedService.GetNonPreReleaseEntriesAsync(feedUrl, isSdkFeed);
            
            if (entries.Count == 0)
            {
                response.StatusCode = HttpStatusCode.NotFound;
                await response.WriteStringAsync("No stable releases found in feed.");
                return response;
            }

            // Get the most recent entry
            var latestEntry = entries.OrderByDescending(e => e.Updated).First();
            
            _logger.LogInformation("Processing release: {Title}", latestEntry.Title);

            // Generate the thread (always use AI for test endpoint)
            IReadOnlyList<string> thread;
            if (premiumMode)
            {
                var post = isSdkFeed
                    ? await _tweetFormatterService.FormatSdkPremiumPostForXAsync(latestEntry, useAi: true)
                    : await _tweetFormatterService.FormatCliPremiumPostForXAsync(latestEntry, useAi: true);
                thread = [post];
            }
            else
            {
                if (isSdkFeed)
                {
                    thread = await _tweetFormatterService.FormatSdkThreadForXAsync(latestEntry, useAi: true);
                }
                else
                {
                    thread = await _tweetFormatterService.FormatCliThreadForXAsync(latestEntry, useAi: true);
                }
            }

            // Return the formatted thread
            response.StatusCode = HttpStatusCode.OK;
            response.Headers.Add("Content-Type", "text/plain; charset=utf-8");
            
            var output = $"Latest Release: {latestEntry.Title}\n";
            output += $"Updated: {latestEntry.Updated:yyyy-MM-dd HH:mm:ss}\n";
            output += $"Link: {latestEntry.Link}\n\n";
            output += $"Thread Preview ({thread.Count} posts):\n";
            output += "═══════════════════════════════════════\n";
            for (var i = 0; i < thread.Count; i++)
            {
                output += $"[Post {i + 1}/{thread.Count}] ({thread[i].Length} chars):\n";
                output += thread[i];
                if (i < thread.Count - 1)
                {
                    output += "\n───────────────────────────────────────\n";
                }
            }
            
            await response.WriteStringAsync(output);
            
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating test summary for type: {Type}", type);
            response.StatusCode = HttpStatusCode.InternalServerError;
            await response.WriteStringAsync($"Error: {ex.Message}");
            return response;
        }
    }

    private async Task<HttpResponseData> HandleVSCodeTestSummaryAsync(HttpRequestData req, HttpResponseData response, bool premiumMode)
    {
        var dateParam = GetQueryParameter(req, "date");
        DateTime targetDate;
        if (!string.IsNullOrEmpty(dateParam) &&
            DateTime.TryParseExact(dateParam, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            targetDate = parsed.Date;
        }
        else
        {
            targetDate = DateTime.UtcNow.Date;
        }

        _logger.LogInformation("Fetching VS Code Insiders release notes for {Date}", targetDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

        var notes = await _vsCodeReleaseNotesService.GetReleaseNotesForDateRangeAsync(targetDate, targetDate);
        if (notes == null || notes.Features.Count == 0)
        {
            response.StatusCode = HttpStatusCode.NotFound;
            await response.WriteStringAsync($"No VS Code Insiders release notes found for {targetDate:yyyy-MM-dd}.");
            return response;
        }

        var summary = await _vsCodeReleaseNotesService.GenerateSummaryAsync(
            notes,
            maxLength: 800,
            format: $"test-daily-{targetDate:yyyyMMdd}",
            forceRefresh: true);

        IReadOnlyList<string> thread;
        if (premiumMode)
        {
            var post = _tweetFormatterService.FormatVSCodeChangelogPremiumPostForX(
                notes.Features,
                notes.Features.Count,
                targetDate,
                targetDate,
                notes.WebsiteUrl);
            thread = [post];
        }
        else
        {
            thread = _tweetFormatterService.FormatVSCodeChangelogThreadForX(summary, notes.Features.Count, targetDate, targetDate, notes.WebsiteUrl);
        }

        response.StatusCode = HttpStatusCode.OK;
        response.Headers.Add("Content-Type", "text/plain; charset=utf-8");

        var output = $"VS Code Insiders: {targetDate:yyyy-MM-dd}\n";
        output += $"Features: {notes.Features.Count}\n";
        output += $"Source: {notes.VersionUrl}\n\n";
        output += $"Thread Preview ({thread.Count} posts):\n";
        output += "═══════════════════════════════════════\n";
        for (var i = 0; i < thread.Count; i++)
        {
            output += $"[Post {i + 1}/{thread.Count}] ({thread[i].Length} chars):\n";
            output += thread[i];
            if (i < thread.Count - 1)
            {
                output += "\n───────────────────────────────────────\n";
            }
        }

        await response.WriteStringAsync(output);
        return response;
    }

    private async Task<HttpResponseData> HandleGitHubChangelogTestSummaryAsync(HttpResponseData response, bool premiumMode, bool singleMode)
    {
        var entries = await _gitHubChangelogFeedService.GetEntriesAsync();
        if (entries.Count == 0)
        {
            response.StatusCode = HttpStatusCode.NotFound;
            await response.WriteStringAsync("No GitHub changelog entries found.");
            return response;
        }

        var latestEntry = entries.OrderByDescending(entry => entry.Updated).First();
        IReadOnlyList<SocialMediaPost> posts = premiumMode
            ? [await _tweetFormatterService.FormatGitHubChangelogPremiumPostForXAsync(latestEntry, useAi: true)]
            : singleMode
                ? [await _tweetFormatterService.FormatGitHubChangelogSinglePostForXAsync(latestEntry, useAi: true)]
                : await _tweetFormatterService.FormatGitHubChangelogThreadForXAsync(latestEntry, useAi: true);

        response.StatusCode = HttpStatusCode.OK;
        response.Headers.Add("Content-Type", "text/plain; charset=utf-8");

        var output = $"GitHub Changelog: {latestEntry.Title}\n";
        output += $"Updated: {latestEntry.Updated:yyyy-MM-dd HH:mm:ss}\n";
        output += $"Link: {latestEntry.Link}\n";
        output += $"Labels: {string.Join(", ", latestEntry.Labels)}\n";
        output += $"Media: {string.Join(", ", latestEntry.Media.Select(item => item.Url))}\n\n";
        output += $"Mode: {(premiumMode ? "premium" : singleMode ? "single" : "thread")}\n";
        output += $"Thread Preview ({posts.Count} posts):\n";
        output += "═══════════════════════════════════════\n";

        for (var i = 0; i < posts.Count; i++)
        {
            var weightedLength = XPostLengthHelper.GetWeightedLength(posts[i].Text);
            output += $"[Post {i + 1}/{posts.Count}] (raw={posts[i].Text.Length}, weighted={weightedLength}";
            if (posts[i].MediaUrlsOrEmpty.Count > 0)
            {
                output += $", media={posts[i].MediaUrlsOrEmpty.Count}";
            }

            output += "):\n";
            output += posts[i].Text;
            if (posts[i].MediaUrlsOrEmpty.Count > 0)
            {
                output += $"\nMedia: {string.Join(", ", posts[i].MediaUrlsOrEmpty)}";
            }

            if (i < posts.Count - 1)
            {
                output += "\n───────────────────────────────────────\n";
            }
        }

        await response.WriteStringAsync(output);
        return response;
    }

    private static string? GetQueryParameter(HttpRequestData req, string name)
    {
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        return query[name];
    }

    private static bool IsEnabledQuery(HttpRequestData req, string name)
    {
        var value = GetQueryParameter(req, name);
        return bool.TryParse(value, out var enabled) && enabled;
    }
}
