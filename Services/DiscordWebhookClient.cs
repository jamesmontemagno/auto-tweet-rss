using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace AutoTweetRss.Services;

public class DiscordWebhookClient
{
    private readonly ILogger<DiscordWebhookClient> _logger;
    private readonly HttpClient _httpClient;

    public DiscordWebhookClient(
        ILogger<DiscordWebhookClient> logger,
        IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _httpClient = httpClientFactory.CreateClient();
    }

    public async Task<bool> PostMessageAsync(string webhookUrl, string content)
    {
        try
        {
            _logger.LogInformation("Posting Discord message: {Preview}...", 
                content.Length > 80 ? content[..80] : content);

            var response = await _httpClient.PostAsJsonAsync(
                webhookUrl, 
                new DiscordWebhookRequest { Content = content });
            var responseContent = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Discord message posted successfully.");
                return true;
            }

            _logger.LogError("Failed to post Discord message. Status: {StatusCode}, Response: {Response}",
                response.StatusCode, responseContent);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error posting Discord message");
            return false;
        }
    }

    public bool TryGetWebhookUrl(out string webhookUrl)
    {
        webhookUrl = string.Empty;
        var enableDiscord = Environment.GetEnvironmentVariable("ENABLE_DISCORD_POSTS");
        if (string.IsNullOrWhiteSpace(enableDiscord))
        {
            return false;
        }

        if (!bool.TryParse(enableDiscord, out var enabled))
        {
            _logger.LogError("ENABLE_DISCORD_POSTS must be 'true' or 'false'");
            return false;
        }

        if (!enabled)
        {
            return false;
        }

        webhookUrl = Environment.GetEnvironmentVariable("DISCORD_WEBHOOK_URL") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(webhookUrl))
        {
            _logger.LogError("ENABLE_DISCORD_POSTS is true but DISCORD_WEBHOOK_URL is not configured");
            return false;
        }

        return true;
    }
}

public class DiscordWebhookRequest
{
    [JsonPropertyName("content")]
    public required string Content { get; set; }
}
