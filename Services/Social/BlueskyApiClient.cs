using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace AutoTweetRss.Services;

/// <summary>
/// Client for posting to Bluesky via the AT Protocol.
/// Authenticates with an App Password via com.atproto.server.createSession.
/// </summary>
public partial class BlueskyApiClient : ISocialMediaClient
{
    private const string BlueskyBaseUrl = "https://bsky.social/xrpc";

    private readonly ILogger<BlueskyApiClient> _logger;
    private readonly HttpClient _httpClient;
    private readonly string? _handle;
    private readonly string? _appPassword;

    public string PlatformName => "Bluesky";

    public bool IsConfigured =>
        !string.IsNullOrEmpty(_handle) && !string.IsNullOrEmpty(_appPassword);

    public BlueskyApiClient(
        ILogger<BlueskyApiClient> logger,
        IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _httpClient = httpClientFactory.CreateClient();
        _handle = Environment.GetEnvironmentVariable("BLUESKY_HANDLE");
        _appPassword = Environment.GetEnvironmentVariable("BLUESKY_APP_PASSWORD");
    }

    public async Task<bool> PostAsync(string text)
    {
        return await PostAndGetRefAsync(text) != null;
    }

    /// <summary>
    /// Posts to Bluesky and returns the (URI, CID) pair needed for threading, or null on failure.
    /// </summary>
    private async Task<(string Uri, string Cid)?> PostAndGetRefAsync(
        string text,
        BlueskyReplyRef? reply = null,
        BlueskySession? existingSession = null)
    {
        if (!IsConfigured)
        {
            _logger.LogWarning("Bluesky credentials not configured. Skipping post.");
            return null;
        }

        try
        {
            _logger.LogInformation("Posting to Bluesky: {PostPreview}...",
                text.Length > 50 ? text[..50] : text);

            var session = existingSession ?? await CreateSessionAsync();
            if (session is null)
                return null;

            var facets = ExtractUrlFacets(text);
            var record = new BlueskyPostRecord
            {
                Type = "app.bsky.feed.post",
                Text = text,
                CreatedAt = DateTime.UtcNow.ToString("o"),
                Facets = facets.Count > 0 ? facets : null,
                Reply = reply
            };

            var createRecordRequest = new BlueskyCreateRecordRequest
            {
                Repo = session.Did,
                Collection = "app.bsky.feed.post",
                Record = record
            };

            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"{BlueskyBaseUrl}/com.atproto.repo.createRecord");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessJwt);
            request.Content = JsonContent.Create(createRecordRequest, options: JsonOptions);

            var response = await _httpClient.SendAsync(request);
            var responseContent = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                var result = JsonSerializer.Deserialize<BlueskyCreateRecordResponse>(responseContent, JsonOptions);
                _logger.LogInformation("Bluesky post created successfully. URI: {Uri}", result?.Uri);
                if (result?.Uri != null && result.Cid != null)
                {
                    return (result.Uri, result.Cid);
                }
                return null;
            }
            else
            {
                _logger.LogError("Failed to post to Bluesky. Status: {StatusCode}, Response: {Response}",
                    response.StatusCode, responseContent);
                return null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error posting to Bluesky");
            return null;
        }
    }

    /// <summary>
    /// Posts multiple records as a reply chain (thread) on Bluesky.
    /// </summary>
    public async Task<bool> PostThreadAsync(IReadOnlyList<string> posts)
    {
        if (posts == null || posts.Count == 0)
        {
            return false;
        }

        if (posts.Count == 1)
        {
            return await PostAsync(posts[0]);
        }

        if (!IsConfigured)
        {
            _logger.LogWarning("Bluesky credentials not configured. Skipping thread.");
            return false;
        }

        // Create a single session for all posts in the thread
        var session = await CreateSessionAsync();
        if (session is null)
        {
            return false;
        }

        var allSucceeded = true;
        (string Uri, string Cid)? rootRef = null;
        (string Uri, string Cid)? parentRef = null;

        for (var i = 0; i < posts.Count; i++)
        {
            BlueskyReplyRef? replyRef = null;
            if (rootRef != null && parentRef != null)
            {
                replyRef = new BlueskyReplyRef
                {
                    Root = new BlueskyStrongRef { Uri = rootRef.Value.Uri, Cid = rootRef.Value.Cid },
                    Parent = new BlueskyStrongRef { Uri = parentRef.Value.Uri, Cid = parentRef.Value.Cid }
                };
            }

            var result = await PostAndGetRefAsync(posts[i], replyRef, session);
            if (result == null)
            {
                _logger.LogWarning("Thread post {Index}/{Total} failed. Stopping thread.", i + 1, posts.Count);
                allSucceeded = false;
                break;
            }

            if (rootRef == null)
            {
                rootRef = result;
            }
            parentRef = result;

            // Small delay between thread posts
            if (i < posts.Count - 1)
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        }

        return allSucceeded;
    }

    private async Task<BlueskySession?> CreateSessionAsync()
    {
        try
        {
            var body = new BlueskySessionRequest
            {
                Identifier = _handle!,
                Password = _appPassword!
            };

            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"{BlueskyBaseUrl}/com.atproto.server.createSession");
            request.Content = JsonContent.Create(body, options: JsonOptions);

            var response = await _httpClient.SendAsync(request);
            var responseContent = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                var session = JsonSerializer.Deserialize<BlueskySession>(responseContent, JsonOptions);
                _logger.LogInformation("Bluesky session created for DID: {Did}", session?.Did);
                return session;
            }
            else
            {
                _logger.LogError("Failed to create Bluesky session. Status: {StatusCode}, Response: {Response}",
                    response.StatusCode, responseContent);
                return null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating Bluesky session");
            return null;
        }
    }

    /// <summary>
    /// Scans text for URLs and builds AT Protocol facets with byte offsets so links are clickable.
    /// </summary>
    private static List<BlueskyFacet> ExtractUrlFacets(string text)
    {
        var facets = new List<BlueskyFacet>();

        foreach (Match match in UrlRegex().Matches(text))
        {
            // Calculate byte offsets for the matched URL
            var beforeMatch = text[..match.Index];
            var byteStart = System.Text.Encoding.UTF8.GetByteCount(beforeMatch);
            var byteEnd = byteStart + System.Text.Encoding.UTF8.GetByteCount(match.Value);

            facets.Add(new BlueskyFacet
            {
                Index = new BlueskyFacetIndex
                {
                    ByteStart = byteStart,
                    ByteEnd = byteEnd
                },
                Features =
                [
                    new BlueskyFacetFeature
                    {
                        Type = "app.bsky.richtext.facet#link",
                        Uri = match.Value
                    }
                ]
            });
        }

        return facets;
    }

    [GeneratedRegex(@"https?://[^\s\)\]]+", RegexOptions.Compiled)]
    private static partial Regex UrlRegex();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

#region Bluesky API Models

public class BlueskySessionRequest
{
    [JsonPropertyName("identifier")]
    public required string Identifier { get; set; }

    [JsonPropertyName("password")]
    public required string Password { get; set; }
}

public class BlueskySession
{
    [JsonPropertyName("did")]
    public string? Did { get; set; }

    [JsonPropertyName("handle")]
    public string? Handle { get; set; }

    [JsonPropertyName("accessJwt")]
    public string? AccessJwt { get; set; }

    [JsonPropertyName("refreshJwt")]
    public string? RefreshJwt { get; set; }
}

public class BlueskyCreateRecordRequest
{
    [JsonPropertyName("repo")]
    public string? Repo { get; set; }

    [JsonPropertyName("collection")]
    public string? Collection { get; set; }

    [JsonPropertyName("record")]
    public BlueskyPostRecord? Record { get; set; }
}

public class BlueskyPostRecord
{
    [JsonPropertyName("$type")]
    public string? Type { get; set; }

    [JsonPropertyName("text")]
    public required string Text { get; set; }

    [JsonPropertyName("createdAt")]
    public string? CreatedAt { get; set; }

    [JsonPropertyName("facets")]
    public List<BlueskyFacet>? Facets { get; set; }

    [JsonPropertyName("reply")]
    public BlueskyReplyRef? Reply { get; set; }
}

public class BlueskyReplyRef
{
    [JsonPropertyName("root")]
    public required BlueskyStrongRef Root { get; set; }

    [JsonPropertyName("parent")]
    public required BlueskyStrongRef Parent { get; set; }
}

public class BlueskyStrongRef
{
    [JsonPropertyName("uri")]
    public required string Uri { get; set; }

    [JsonPropertyName("cid")]
    public required string Cid { get; set; }
}

public class BlueskyFacet
{
    [JsonPropertyName("index")]
    public BlueskyFacetIndex? Index { get; set; }

    [JsonPropertyName("features")]
    public List<BlueskyFacetFeature>? Features { get; set; }
}

public class BlueskyFacetIndex
{
    [JsonPropertyName("byteStart")]
    public int ByteStart { get; set; }

    [JsonPropertyName("byteEnd")]
    public int ByteEnd { get; set; }
}

public class BlueskyFacetFeature
{
    [JsonPropertyName("$type")]
    public string? Type { get; set; }

    [JsonPropertyName("uri")]
    public string? Uri { get; set; }
}

public class BlueskyCreateRecordResponse
{
    [JsonPropertyName("uri")]
    public string? Uri { get; set; }

    [JsonPropertyName("cid")]
    public string? Cid { get; set; }
}

#endregion

