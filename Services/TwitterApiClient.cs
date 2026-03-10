using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace AutoTweetRss.Services;

public class TwitterApiClient
{
    private const string TwitterApiUrl = "https://api.x.com/2/tweets";
    private const string MediaUploadUrl = "https://upload.twitter.com/1.1/media/upload.json";
    private const int MediaUploadChunkSize = 4 * 1024 * 1024;

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

    public Task<bool> PostTweetAsync(string text)
        => PostTweetAsync(new SocialMediaPost(text));

    public async Task<bool> PostTweetAsync(string text, IReadOnlyList<string>? mediaUrls)
        => await PostTweetAsync(new SocialMediaPost(text, mediaUrls));

    public async Task<bool> PostTweetAsync(SocialMediaPost post)
        => await PostTweetAndGetIdAsync(post) != null;

    /// <summary>
    /// Posts a tweet and returns the tweet ID, or null on failure.
    /// </summary>
    public Task<string?> PostTweetAndGetIdAsync(string text, string? replyToTweetId = null)
        => PostTweetAndGetIdAsync(new SocialMediaPost(text), replyToTweetId);

    public Task<string?> PostTweetAndGetIdAsync(string text, string? replyToTweetId, IReadOnlyList<string>? mediaUrls)
        => PostTweetAndGetIdAsync(new SocialMediaPost(text, mediaUrls), replyToTweetId);

    public async Task<string?> PostTweetAndGetIdAsync(SocialMediaPost post, string? replyToTweetId = null)
    {
        if (!IsConfigured)
        {
            _logger.LogWarning("Twitter credentials not configured. Skipping tweet.");
            return null;
        }

        try
        {
            var weightedLength = XPostLengthHelper.GetWeightedLength(post.Text);
            _logger.LogInformation(
                "Posting tweet (raw={RawLength}, weighted={WeightedLength}, media={MediaCount}): {TweetPreview}...",
                post.Text.Length,
                weightedLength,
                post.MediaUrlsOrEmpty.Count,
                post.Text.Length > 50 ? post.Text[..50] : post.Text);
            _logger.LogInformation("OAuth configured: {IsConfigured}, Consumer key starts with: {KeyPrefix}",
                _oauth1Helper.IsConfigured,
                _oauth1Helper.ConsumerKeyPrefix);

            var mediaIds = await UploadMediaAsync(post.MediaUrlsOrEmpty);
            var authHeader = _oauth1Helper.GenerateAuthorizationHeader("POST", TwitterApiUrl);

            using var request = new HttpRequestMessage(HttpMethod.Post, TwitterApiUrl);
            request.Headers.Add("Authorization", authHeader);

            var body = new TweetRequest { Text = post.Text };
            if (!string.IsNullOrEmpty(replyToTweetId))
            {
                body.Reply = new TweetReplyRequest { InReplyToTweetId = replyToTweetId };
            }

            if (mediaIds.Count > 0)
            {
                body.Media = new TweetMediaRequest { MediaIds = mediaIds };
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

            _logger.LogError("Failed to post tweet. Status: {StatusCode}, Response: {Response}",
                response.StatusCode, responseContent);
            return null;
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
    public Task<bool> PostTweetThreadAsync(IReadOnlyList<string> posts)
        => PostTweetThreadAsync(posts.Select(text => new SocialMediaPost(text)).ToList());

    public async Task<bool> PostTweetThreadAsync(IReadOnlyList<SocialMediaPost> posts)
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

            if (i < posts.Count - 1)
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        }

        return allSucceeded;
    }

    private async Task<List<string>> UploadMediaAsync(IReadOnlyList<string> mediaUrls)
    {
        var mediaIds = new List<string>();

        foreach (var mediaUrl in mediaUrls.Where(url => !string.IsNullOrWhiteSpace(url)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var mediaId = await UploadSingleMediaAsync(mediaUrl);
            if (!string.IsNullOrWhiteSpace(mediaId))
            {
                mediaIds.Add(mediaId);
            }
        }

        return mediaIds;
    }

    private async Task<string?> UploadSingleMediaAsync(string mediaUrl)
    {
        using var mediaResponse = await _httpClient.GetAsync(mediaUrl);
        if (!mediaResponse.IsSuccessStatusCode)
        {
            _logger.LogWarning("Failed to download media from {MediaUrl}. Status: {StatusCode}",
                mediaUrl, mediaResponse.StatusCode);
            return null;
        }

        var bytes = await mediaResponse.Content.ReadAsByteArrayAsync();
        if (bytes.Length == 0)
        {
            _logger.LogWarning("Downloaded media from {MediaUrl} was empty.", mediaUrl);
            return null;
        }

        var contentType = mediaResponse.Content.Headers.ContentType?.MediaType
            ?? GuessMediaTypeFromUrl(mediaUrl);

        if (contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
        {
            return await UploadChunkedVideoAsync(bytes, contentType);
        }

        return await UploadSimpleMediaAsync(bytes, contentType);
    }

    private async Task<string?> UploadSimpleMediaAsync(byte[] bytes, string contentType)
    {
        var authHeader = _oauth1Helper.GenerateAuthorizationHeader("POST", MediaUploadUrl);

        using var request = new HttpRequestMessage(HttpMethod.Post, MediaUploadUrl);
        request.Headers.Add("Authorization", authHeader);

        using var multipart = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = new(contentType);
        multipart.Add(fileContent, "media", "media");
        multipart.Add(new StringContent(GetMediaCategory(contentType)), "media_category");
        multipart.Add(new StringContent(contentType), "media_type");
        request.Content = multipart;

        var response = await _httpClient.SendAsync(request);
        var payload = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Simple media upload failed. Status: {StatusCode}, Response: {Response}",
                response.StatusCode, payload);
            return null;
        }

        var upload = JsonSerializer.Deserialize<TwitterMediaUploadResponse>(payload);
        return upload?.MediaIdString;
    }

    private async Task<string?> UploadChunkedVideoAsync(byte[] bytes, string contentType)
    {
        var initParameters = new Dictionary<string, string>
        {
            ["command"] = "INIT",
            ["total_bytes"] = bytes.Length.ToString(),
            ["media_type"] = contentType,
            ["media_category"] = "tweet_video"
        };

        var mediaId = await SendUploadCommandAsync(initParameters);
        if (string.IsNullOrWhiteSpace(mediaId))
        {
            return null;
        }

        for (var index = 0; index * MediaUploadChunkSize < bytes.Length; index++)
        {
            var chunk = bytes
                .Skip(index * MediaUploadChunkSize)
                .Take(MediaUploadChunkSize)
                .ToArray();

            var appended = await AppendUploadChunkAsync(mediaId, index, chunk, contentType);
            if (!appended)
            {
                return null;
            }
        }

        var finalizeParameters = new Dictionary<string, string>
        {
            ["command"] = "FINALIZE",
            ["media_id"] = mediaId
        };

        var finalized = await FinalizeUploadAsync(finalizeParameters);
        if (!finalized)
        {
            return null;
        }

        return mediaId;
    }

    private async Task<string?> SendUploadCommandAsync(IReadOnlyDictionary<string, string> parameters)
    {
        var authHeader = _oauth1Helper.GenerateAuthorizationHeader("POST", MediaUploadUrl, parameters);

        using var request = new HttpRequestMessage(HttpMethod.Post, MediaUploadUrl);
        request.Headers.Add("Authorization", authHeader);
        request.Content = new FormUrlEncodedContent(parameters);

        var response = await _httpClient.SendAsync(request);
        var payload = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Media upload command {Command} failed. Status: {StatusCode}, Response: {Response}",
                parameters.GetValueOrDefault("command"), response.StatusCode, payload);
            return null;
        }

        var upload = JsonSerializer.Deserialize<TwitterMediaUploadResponse>(payload);
        return upload?.MediaIdString;
    }

    private async Task<bool> AppendUploadChunkAsync(string mediaId, int segmentIndex, byte[] chunk, string contentType)
    {
        var authHeader = _oauth1Helper.GenerateAuthorizationHeader("POST", MediaUploadUrl);

        using var request = new HttpRequestMessage(HttpMethod.Post, MediaUploadUrl);
        request.Headers.Add("Authorization", authHeader);

        using var multipart = new MultipartFormDataContent();
        multipart.Add(new StringContent("APPEND"), "command");
        multipart.Add(new StringContent(mediaId), "media_id");
        multipart.Add(new StringContent(segmentIndex.ToString()), "segment_index");

        var fileContent = new ByteArrayContent(chunk);
        fileContent.Headers.ContentType = new(contentType);
        multipart.Add(fileContent, "media", $"chunk-{segmentIndex}");
        request.Content = multipart;

        var response = await _httpClient.SendAsync(request);
        if (response.IsSuccessStatusCode)
        {
            return true;
        }

        var payload = await response.Content.ReadAsStringAsync();
        _logger.LogWarning("Media APPEND failed for media {MediaId} segment {SegmentIndex}. Status: {StatusCode}, Response: {Response}",
            mediaId, segmentIndex, response.StatusCode, payload);
        return false;
    }

    private async Task<bool> FinalizeUploadAsync(IReadOnlyDictionary<string, string> parameters)
    {
        var authHeader = _oauth1Helper.GenerateAuthorizationHeader("POST", MediaUploadUrl, parameters);

        using var request = new HttpRequestMessage(HttpMethod.Post, MediaUploadUrl);
        request.Headers.Add("Authorization", authHeader);
        request.Content = new FormUrlEncodedContent(parameters);

        var response = await _httpClient.SendAsync(request);
        var payload = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Media FINALIZE failed. Status: {StatusCode}, Response: {Response}",
                response.StatusCode, payload);
            return false;
        }

        var upload = JsonSerializer.Deserialize<TwitterMediaUploadResponse>(payload);
        if (upload?.ProcessingInfo == null)
        {
            return true;
        }

        return await PollUploadStatusAsync(upload.MediaIdString ?? string.Empty);
    }

    private async Task<bool> PollUploadStatusAsync(string mediaId)
    {
        if (string.IsNullOrWhiteSpace(mediaId))
        {
            return false;
        }

        for (var attempt = 0; attempt < 10; attempt++)
        {
            var query = new Dictionary<string, string>
            {
                ["command"] = "STATUS",
                ["media_id"] = mediaId
            };

            var url = $"{MediaUploadUrl}?command=STATUS&media_id={Uri.EscapeDataString(mediaId)}";
            var authHeader = _oauth1Helper.GenerateAuthorizationHeader("GET", MediaUploadUrl, query);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Authorization", authHeader);

            var response = await _httpClient.SendAsync(request);
            var payload = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Media STATUS failed for media {MediaId}. Status: {StatusCode}, Response: {Response}",
                    mediaId, response.StatusCode, payload);
                return false;
            }

            var upload = JsonSerializer.Deserialize<TwitterMediaUploadResponse>(payload);
            var state = upload?.ProcessingInfo?.State;
            if (string.Equals(state, "succeeded", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.Equals(state, "failed", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Media processing failed for media {MediaId}: {Error}",
                    mediaId, upload?.ProcessingInfo?.Error?.Message ?? "unknown error");
                return false;
            }

            var delaySeconds = upload?.ProcessingInfo?.CheckAfterSeconds ?? 2;
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, delaySeconds)));
        }

        _logger.LogWarning("Media processing did not complete for media {MediaId} within polling window.", mediaId);
        return false;
    }

    private static string GetMediaCategory(string contentType)
    {
        if (contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
        {
            return "tweet_video";
        }

        return "tweet_image";
    }

    private static string GuessMediaTypeFromUrl(string mediaUrl)
    {
        var path = mediaUrl.Split('?', '#')[0];
        var extension = Path.GetExtension(path);

        return extension.ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            ".mp4" => "video/mp4",
            ".mov" => "video/quicktime",
            _ => "application/octet-stream"
        };
    }
}

public class TweetRequest
{
    [JsonPropertyName("text")]
    public required string Text { get; set; }

    [JsonPropertyName("reply")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TweetReplyRequest? Reply { get; set; }

    [JsonPropertyName("media")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TweetMediaRequest? Media { get; set; }
}

public class TweetReplyRequest
{
    [JsonPropertyName("in_reply_to_tweet_id")]
    public required string InReplyToTweetId { get; set; }
}

public class TweetMediaRequest
{
    [JsonPropertyName("media_ids")]
    public required IReadOnlyList<string> MediaIds { get; set; }
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

public class TwitterMediaUploadResponse
{
    [JsonPropertyName("media_id")]
    public long MediaId { get; set; }

    [JsonPropertyName("media_id_string")]
    public string? MediaIdString { get; set; }

    [JsonPropertyName("processing_info")]
    public TwitterMediaProcessingInfo? ProcessingInfo { get; set; }
}

public class TwitterMediaProcessingInfo
{
    [JsonPropertyName("state")]
    public string? State { get; set; }

    [JsonPropertyName("check_after_secs")]
    public int? CheckAfterSeconds { get; set; }

    [JsonPropertyName("error")]
    public TwitterMediaProcessingError? Error { get; set; }
}

public class TwitterMediaProcessingError
{
    [JsonPropertyName("message")]
    public string? Message { get; set; }
}
