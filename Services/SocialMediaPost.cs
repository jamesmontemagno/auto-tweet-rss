namespace AutoTweetRss.Services;

/// <summary>
/// Represents a social media post, optionally with media URLs to attach.
/// </summary>
public sealed record SocialMediaPost(string Text, IReadOnlyList<string>? MediaUrls = null)
{
    public IReadOnlyList<string> MediaUrlsOrEmpty => MediaUrls ?? [];
}
