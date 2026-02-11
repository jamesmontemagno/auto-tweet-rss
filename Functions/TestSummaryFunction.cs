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
    private readonly TweetFormatterService _tweetFormatterService;
    private readonly VSCodeReleaseNotesService _vsCodeReleaseNotesService;

    public TestSummaryFunction(
        ILogger<TestSummaryFunction> logger,
        RssFeedService rssFeedService,
        TweetFormatterService tweetFormatterService,
        VSCodeReleaseNotesService vsCodeReleaseNotesService)
    {
        _logger = logger;
        _rssFeedService = rssFeedService;
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

            // Validate type parameter
            if (typeLower != "cli" && typeLower != "sdk" && typeLower != "vscode")
            {
                response.StatusCode = HttpStatusCode.BadRequest;
                await response.WriteStringAsync($"Invalid type: {type}. Must be 'cli', 'sdk', or 'vscode'.");
                return response;
            }

            // VS Code path
            if (typeLower == "vscode")
            {
                return await HandleVSCodeTestSummaryAsync(response);
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

            // Generate the tweet (always use AI for test endpoint)
            string tweet;
            if (isSdkFeed)
            {
                tweet = await _tweetFormatterService.FormatSdkTweetAsync(latestEntry, useAi: true);
            }
            else
            {
                tweet = await _tweetFormatterService.FormatTweetAsync(latestEntry, useAi: true);
            }

            // Return the formatted tweet
            response.StatusCode = HttpStatusCode.OK;
            response.Headers.Add("Content-Type", "text/plain; charset=utf-8");
            
            var output = $"Latest Release: {latestEntry.Title}\n";
            output += $"Updated: {latestEntry.Updated:yyyy-MM-dd HH:mm:ss}\n";
            output += $"Link: {latestEntry.Link}\n\n";
            output += $"Formatted Tweet ({tweet.Length} chars):\n";
            output += "═══════════════════════════════════════\n";
            output += tweet;
            
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

    private async Task<HttpResponseData> HandleVSCodeTestSummaryAsync(HttpResponseData response)
    {
        _logger.LogInformation("Fetching today's VS Code Insiders release notes");

        var notes = await _vsCodeReleaseNotesService.GetTodayReleaseNotesAsync();
        if (notes == null || notes.Features.Count == 0)
        {
            response.StatusCode = HttpStatusCode.NotFound;
            await response.WriteStringAsync("No VS Code Insiders release notes found for today.");
            return response;
        }

        var summary = await _vsCodeReleaseNotesService.GenerateSummaryAsync(
            notes,
            maxLength: 220,
            format: "test-daily",
            forceRefresh: true);

        var today = DateTime.UtcNow.Date;
        var tweet = _tweetFormatterService.FormatVSCodeChangelogTweet(summary, today, today, notes.WebsiteUrl);

        response.StatusCode = HttpStatusCode.OK;
        response.Headers.Add("Content-Type", "text/plain; charset=utf-8");

        var output = $"VS Code Insiders: {notes.Date:yyyy-MM-dd}\n";
        output += $"Features: {notes.Features.Count}\n";
        output += $"Source: {notes.VersionUrl}\n\n";
        output += $"Formatted Tweet ({tweet.Length} chars):\n";
        output += "═══════════════════════════════════════\n";
        output += tweet;

        await response.WriteStringAsync(output);
        return response;
    }
}
