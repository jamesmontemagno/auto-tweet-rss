using System.Text.RegularExpressions;
using AutoTweetRss.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutoTweetRss.Tests;

public partial class TweetFormatterServiceUrlTests
{
    private const string CanonicalUrl = "https://github.com/github/copilot-cli/releases/tag/v1.2.3";

    [GeneratedRegex(@"https?://[^\s]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlPattern();

    [Fact]
    public void FormatTweet_RemovesExtraUrlsFromExtractedReleaseContent()
    {
        var formatter = CreateFormatter();
        var entry = new ReleaseEntry
        {
            Id = "release-1",
            Title = "1.2.3",
            Content = """
                <ul>
                    <li>New agent mode details at https://example.com/agent-mode</li>
                    <li>Improved docs: https://docs.example.com/copilot-cli</li>
                </ul>
                """,
            Link = CanonicalUrl,
            Updated = DateTimeOffset.UtcNow
        };

        var tweet = formatter.FormatTweet(entry);

        AssertOnlyCanonicalUrl(tweet);
        Assert.DoesNotContain("example.com", tweet, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FormatVSCodeChangelogThreadForX_RemovesExtraUrlsFromThreadPosts()
    {
        var formatter = CreateFormatter();
        const string canonicalUrl = "https://code.visualstudio.com/updates/v1_100";
        const string summary = """
            Inline chat now supports links to https://example.com/inline-chat
            Terminal fixes are documented at https://docs.example.com/terminal
            """;

        var posts = formatter.FormatVSCodeChangelogThreadForX(
            summary,
            featureCount: 2,
            startDate: new DateTime(2026, 7, 8),
            endDate: new DateTime(2026, 7, 9),
            url: canonicalUrl);

        AssertOnlyCanonicalUrl(string.Join("\n", posts), canonicalUrl);
        Assert.DoesNotContain("example.com", string.Join("\n", posts), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FormatVSCodeChangelogPremiumPostForX_RemovesExtraUrlsFromFeatureText()
    {
        var formatter = CreateFormatter();
        const string canonicalUrl = "https://code.visualstudio.com/updates/v1_100";
        var features = new[]
        {
            new VSCodeFeature
            {
                Title = "Inline chat",
                Description = "See more at https://example.com/inline-chat",
                Link = "https://example.com/inline-chat"
            },
            new VSCodeFeature
            {
                Title = "Terminal fixes https://docs.example.com/terminal",
                Description = "Bug fixes",
                Link = "https://docs.example.com/terminal"
            }
        };

        var post = formatter.FormatVSCodeChangelogPremiumPostForX(
            features,
            featureCount: features.Length,
            startDate: new DateTime(2026, 7, 8),
            endDate: new DateTime(2026, 7, 9),
            url: canonicalUrl);

        AssertOnlyCanonicalUrl(post, canonicalUrl);
        Assert.DoesNotContain("example.com", post, StringComparison.OrdinalIgnoreCase);
    }

    private static TweetFormatterService CreateFormatter()
        => new(NullLogger<TweetFormatterService>.Instance);

    private static void AssertOnlyCanonicalUrl(string text, string canonicalUrl = CanonicalUrl)
    {
        var urls = UrlPattern().Matches(text).Select(match => match.Value).ToList();
        var url = Assert.Single(urls);
        Assert.Equal(canonicalUrl, url);
    }
}
