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
        if (!IsConfigured)
        {
            _logger.LogWarning("Twitter credentials not configured. Skipping tweet.");
            return false;
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
            request.Content = JsonContent.Create(new TweetRequest { Text = text });

            var response = await _httpClient.SendAsync(request);
            var responseContent = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                var tweetResponse = JsonSerializer.Deserialize<TweetResponse>(responseContent);
                _logger.LogInformation("Tweet posted successfully. Tweet ID: {TweetId}", 
                    tweetResponse?.Data?.Id);
                return true;
            }
            else
            {
                _logger.LogError("Failed to post tweet. Status: {StatusCode}, Response: {Response}", 
                    response.StatusCode, responseContent);
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error posting tweet");
            return false;
        }
    }
}

public class TweetRequest
{
    [JsonPropertyName("text")]
    public required string Text { get; set; }
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
