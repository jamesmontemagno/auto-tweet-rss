using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AutoTweetRss.Services;

public class VSCodeTwitterApiClient
{
    private const string TwitterApiUrl = "https://api.x.com/2/tweets";

    private readonly ILogger<VSCodeTwitterApiClient> _logger;
    private readonly HttpClient _httpClient;
    private readonly VSCodeOAuth1Helper _oauth1Helper;

    public VSCodeTwitterApiClient(
        ILogger<VSCodeTwitterApiClient> logger,
        IHttpClientFactory httpClientFactory,
        VSCodeOAuth1Helper oauth1Helper)
    {
        _logger = logger;
        _httpClient = httpClientFactory.CreateClient();
        _oauth1Helper = oauth1Helper;
    }

    public async Task<bool> PostTweetAsync(string text)
    {
        try
        {
            _logger.LogInformation("Posting VS Code tweet: {TweetPreview}...",
                text.Length > 50 ? text[..50] : text);

            var authHeader = _oauth1Helper.GenerateAuthorizationHeader("POST", TwitterApiUrl);

            using var request = new HttpRequestMessage(HttpMethod.Post, TwitterApiUrl);
            request.Headers.Add("Authorization", authHeader);
            request.Content = JsonContent.Create(new TweetRequest { Text = text });

            var response = await _httpClient.SendAsync(request);
            var responseContent = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                var tweetResponse = JsonSerializer.Deserialize<TweetResponse>(responseContent);
                _logger.LogInformation("VS Code tweet posted successfully. Tweet ID: {TweetId}",
                    tweetResponse?.Data?.Id);
                return true;
            }

            _logger.LogError("Failed to post VS Code tweet. Status: {StatusCode}, Response: {Response}",
                response.StatusCode, responseContent);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error posting VS Code tweet");
            return false;
        }
    }
}

