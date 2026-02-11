namespace AutoTweetRss.Services;

/// <summary>
/// Abstraction for posting to a social media platform.
/// </summary>
public interface ISocialMediaClient
{
    /// <summary>
    /// Display name of the platform (e.g., "Twitter", "Bluesky") used in logging.
    /// </summary>
    string PlatformName { get; }

    /// <summary>
    /// Whether the client has valid credentials configured.
    /// </summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Posts a text message to the platform.
    /// </summary>
    /// <returns>True if the post was successful; false otherwise.</returns>
    Task<bool> PostAsync(string text);
}
