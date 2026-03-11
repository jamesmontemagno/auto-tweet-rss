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

    /// <summary>
    /// Posts a series of messages as a thread (reply chain) on the platform.
    /// Each post after the first is posted as a reply to the previous one.
    /// </summary>
    /// <param name="posts">Ordered list of post texts; must contain at least one post.</param>
    /// <returns>True if all posts were published successfully; false otherwise.</returns>
    Task<bool> PostThreadAsync(IReadOnlyList<string> posts);
}

/// <summary>
/// Represents a social media thread: an ordered list of posts to be published as a reply chain.
/// </summary>
public record SocialMediaThread(IReadOnlyList<string> Posts)
{
    /// <summary>Returns true when the thread contains more than one post.</summary>
    public bool IsThread => Posts.Count > 1;
}
