using System.Text.RegularExpressions;
using AutoTweetRss.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutoTweetRss.Tests;

public partial class TweetFormatterServiceTests
{
    [Fact]
    public void FormatTweet_StripsExtraUrlsFromVisibleText()
    {
        var formatter = CreateFormatter();
        var canonicalUrl = "https://github.com/github/copilot-cli/releases/tag/v1.2.3";
        var entry = CreateEntry(
            title: "1.2.3",
            link: canonicalUrl,
            content: """
                <ul>
                    <li>Improved setup guidance https://example.com/setup</li>
                    <li>https://example.com/url-only</li>
                    <li>Fixed terminal rendering bugs</li>
                </ul>
                """);

        var tweet = formatter.FormatTweet(entry);

        AssertCanonicalUrlOnly(tweet, canonicalUrl);
        Assert.Contains("Improved setup guidance", tweet);
        Assert.DoesNotContain("example.com", tweet, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\n•\n", tweet, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatVSCodeChangelogThreadForX_StripsExtraUrlsAcrossPosts()
    {
        var formatter = CreateFormatter();
        var canonicalUrl = "https://code.visualstudio.com/updates";
        var summary = """
            Highlights:
            - Streamlined notebook editing with better inline execution details https://example.com/notes
            - https://example.com/url-only
            - Improved source control history rendering for large repositories
            - Added more resilient terminal restore behavior after window reloads
            - Expanded debug console filtering with richer inline context previews
            - Refined accessibility announcements for sticky scroll navigation
            - Fixed search view flicker when switching between workspace folders
            """;

        var posts = formatter.FormatVSCodeChangelogThreadForX(
            summary,
            featureCount: 7,
            startDate: new DateTime(2026, 7, 1),
            endDate: new DateTime(2026, 7, 2),
            url: canonicalUrl);

        Assert.True(posts.Count > 1);
        AssertCanonicalUrlOnly(string.Join("\n", posts), canonicalUrl);
        Assert.All(posts, post => Assert.DoesNotContain("example.com", post, StringComparison.OrdinalIgnoreCase));
        Assert.All(
            posts.SelectMany(post => post.Split('\n')),
            line => Assert.NotEqual("•", line.Trim()));
    }

    [Fact]
    public async Task FormatCliPremiumPostForXAsync_StripsExtraUrlsFromSections()
    {
        var formatter = CreateFormatter();
        var canonicalUrl = "https://github.com/github/copilot-cli/releases/tag/v2.0.0";
        var entry = CreateEntry(
            title: "2.0.0",
            link: canonicalUrl,
            content: """
                <ul>
                    <li>Feature: Added guided onboarding [release docs](https://example.com/docs)</li>
                    <li>Bugfix: Fixed shell detection on Windows https://example.com/fix</li>
                    <li>https://example.com/url-only</li>
                    <li>Docs: Refreshed troubleshooting references</li>
                </ul>
                """);

        var post = await formatter.FormatCliPremiumPostForXAsync(entry);

        AssertCanonicalUrlOnly(post, canonicalUrl);
        Assert.Contains("release docs", post);
        Assert.DoesNotContain("example.com", post, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\n•\n", post, StringComparison.Ordinal);
    }

    private static TweetFormatterService CreateFormatter()
        => new(NullLogger<TweetFormatterService>.Instance);

    private static ReleaseEntry CreateEntry(string title, string link, string content)
        => new()
        {
            Id = title,
            Title = title,
            Content = content,
            Link = link,
            Updated = DateTimeOffset.UtcNow
        };

    private static void AssertCanonicalUrlOnly(string text, string canonicalUrl)
    {
        var urls = UrlPattern().Matches(text).Select(match => match.Value).ToArray();
        Assert.Equal([canonicalUrl], urls);
    }

    [GeneratedRegex(@"https?://\S+")]
    private static partial Regex UrlPattern();
}
