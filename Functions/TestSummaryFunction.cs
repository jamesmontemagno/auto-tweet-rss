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

    public TestSummaryFunction(
        ILogger<TestSummaryFunction> logger,
        RssFeedService rssFeedService,
        TweetFormatterService tweetFormatterService)
    {
        _logger = logger;
        _rssFeedService = rssFeedService;
        _tweetFormatterService = tweetFormatterService;
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
            // Validate type parameter
        if (type.ToLowerInvariant() != "cli" && type.ToLowerInvariant() != "sdk")
        {
            response.StatusCode = HttpStatusCode.BadRequest;
            await response.WriteStringAsync($"Invalid type: {type}. Must be 'cli' or 'sdk'.");
            return response;
        }

            // Determine feed URL based on type
            string feedUrl;
            bool isSdkFeed;
            if (type.ToLowerInvariant() == "sdk")
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

            var discordMessage = _tweetFormatterService.FormatDiscordChangelog(latestEntry, isSdkFeed);

            // Return the formatted tweet
            response.StatusCode = HttpStatusCode.OK;
            response.Headers.Add("Content-Type", "text/plain; charset=utf-8");
            
            var output = $"Latest Release: {latestEntry.Title}\n";
            output += $"Updated: {latestEntry.Updated:yyyy-MM-dd HH:mm:ss}\n";
            output += $"Link: {latestEntry.Link}\n\n";
            output += $"Formatted Tweet ({tweet.Length} chars):\n";
            output += "═══════════════════════════════════════\n";
            output += tweet;
            output += "\n\nFormatted Discord Message:\n";
            output += "═══════════════════════════════════════\n";
            output += discordMessage;
            
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
}
