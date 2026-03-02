using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace AutoTweetRss.Services;

public class TwitterApiClient
{
    private const string TwitterApiUrl = "https://api.x.com/2/tweets";
    
    private readonly ILogger _logger;
    private readonly HttpClient _httpClient;
    private readonly OAuth1Helper _oauth1Helper;

    /// <summary>
    /// Whether the Twitter credentials are configured.
    /// </summary>
    public bool IsConfigured => _oauth1Helper.IsConfigured;

    public TwitterApiClient(
        ILogger<TwitterApiClient> logger, 
        IHttpClientFactory httpClientFactory,
        OAuth1Helper oauth1Helper)
    {
        _logger = logger;
        _httpClient = httpClientFactory.CreateClient();
        _oauth1Helper = oauth1Helper;
    }

    /// <summary>
    /// Protected constructor for subclasses with different logger types.
    /// </summary>
    protected TwitterApiClient(
        ILogger logger,
        IHttpClientFactory httpClientFactory,
        OAuth1Helper oauth1Helper)
    {
        _logger = logger;
        _httpClient = httpClientFactory.CreateClient();
        _oauth1Helper = oauth1Helper;
    }

    public async Task<bool> PostTweetAsync(string text)
    {
        return await PostTweetAndGetIdAsync(text) != null;
    }

    /// <summary>
    /// Posts a tweet and returns the tweet ID, or null on failure.
    /// </summary>
    public async Task<string?> PostTweetAndGetIdAsync(string text, string? replyToTweetId = null)
    {
        if (!IsConfigured)
        {
            _logger.LogWarning("Twitter credentials not configured. Skipping tweet.");
            return null;
        }

        try
        {
            _logger.LogInformation("Posting tweet: {TweetPreview}...", 
                text.Length > 50 ? text[..50] : text);
            _logger.LogInformation("OAuth configured: {IsConfigured}, Consumer key starts with: {KeyPrefix}",
                _oauth1Helper.IsConfigured,
                _oauth1Helper.ConsumerKeyPrefix);

            var authHeader = _oauth1Helper.GenerateAuthorizationHeader("POST", TwitterApiUrl);
            
            using var request = new HttpRequestMessage(HttpMethod.Post, TwitterApiUrl);
            request.Headers.Add("Authorization", authHeader);

            var body = new TweetRequest { Text = text };
            if (!string.IsNullOrEmpty(replyToTweetId))
            {
                body.Reply = new TweetReplyRequest { InReplyToTweetId = replyToTweetId };
            }
            request.Content = JsonContent.Create(body);

            var response = await _httpClient.SendAsync(request);
            var responseContent = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                var tweetResponse = JsonSerializer.Deserialize<TweetResponse>(responseContent);
                var tweetId = tweetResponse?.Data?.Id;
                _logger.LogInformation("Tweet posted successfully. Tweet ID: {TweetId}", tweetId);
                return tweetId;
            }
            else
            {
                _logger.LogError("Failed to post tweet. Status: {StatusCode}, Response: {Response}", 
                    response.StatusCode, responseContent);
                return null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error posting tweet");
            return null;
        }
    }

    /// <summary>
    /// Posts multiple tweets as a reply chain (thread). Returns true if all posts succeeded.
    /// </summary>
    public async Task<bool> PostTweetThreadAsync(IReadOnlyList<string> posts)
    {
        if (posts == null || posts.Count == 0)
        {
            return false;
        }

        if (posts.Count == 1)
        {
            return await PostTweetAsync(posts[0]);
        }

        string? lastTweetId = null;
        var allSucceeded = true;

        for (var i = 0; i < posts.Count; i++)
        {
            var tweetId = await PostTweetAndGetIdAsync(posts[i], lastTweetId);
            if (tweetId == null)
            {
                _logger.LogWarning("Thread post {Index}/{Total} failed. Stopping thread.", i + 1, posts.Count);
                allSucceeded = false;
                break;
            }
            lastTweetId = tweetId;

            // Small delay between thread posts to avoid rate limiting
            if (i < posts.Count - 1)
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        }

        return allSucceeded;
    }
}

public class TweetRequest
{
    [JsonPropertyName("text")]
    public required string Text { get; set; }

    [JsonPropertyName("reply")]
    public TweetReplyRequest? Reply { get; set; }
}

public class TweetReplyRequest
{
    [JsonPropertyName("in_reply_to_tweet_id")]
    public required string InReplyToTweetId { get; set; }
}

public class TweetResponse
{
    [JsonPropertyName("data")]
    public TweetData? Data { get; set; }
    
    [JsonPropertyName("errors")]
    public List<TweetError>? Errors { get; set; }
}

public class TweetData
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }
    
    [JsonPropertyName("text")]
    public string? Text { get; set; }
}

public class TweetError
{
    [JsonPropertyName("message")]
    public string? Message { get; set; }
}
