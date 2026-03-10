using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace AutoTweetRss.Services;

public partial class TweetFormatterService
{
    private const string GitHubChangelogHashtag = "#GitHub";
    private const string GitHubCopilotHashtag = "#GitHubCopilot";
    private const string GitHubActionsHashtag = "#GitHubActions";
    private const string GitHubCodespacesHashtag = "#GitHubCodespaces";
    private const string GitHubCliHashtag = "#GitHubCLI";
    private const string GitHubEnterpriseHashtag = "#GitHubEnterprise";
    private const string GitHubIssuesHashtag = "#GitHubIssues";
    private const string GitHubProjectsHashtag = "#GitHubProjects";
    private const string GitHubSecurityHashtag = "#GitHubSecurity";
    private const string GitHubDiscussionsHashtag = "#GitHubDiscussions";
    private const string GitHubMobileHashtag = "#GitHubMobile";
    private const string GitHubPrsHashtag = "#GitHubPRs";
    private const string GitHubAiHashtag = "#AI";

    [GeneratedRegex(@"^[\p{So}\p{Sk}\p{P}\s]+", RegexOptions.Compiled)]
    private static partial Regex LeadingDecorationPattern();

    [GeneratedRegex(@"(?<=[.!?])\s+", RegexOptions.Compiled)]
    private static partial Regex SentenceSplitPattern();

    public async Task<IReadOnlyList<SocialMediaPost>> FormatGitHubChangelogThreadForXAsync(
        GitHubChangelogEntry entry,
        bool useAi = false)
    {
        var plan = await BuildGitHubChangelogSummaryPlanAsync(entry, premiumMode: false, useAi, isWeekly: false);
        var header = $"📣 GitHub Changelog\n{TruncateForDisplay(entry.Title, 110)}";
        var hashtags = FormatGitHubChangelogHashtags(GetGitHubChangelogHashtags(entry));
        var bodyPosts = PackParagraphsIntoPosts(plan.Paragraphs, MaxTweetLength);
        var posts = AssembleThread(
            header,
            plan.TopThingsToKnow.Select(item => $"• {SanitizeBullet(item)}").ToList(),
            bodyPosts,
            plan.TopThingsToKnow.Count,
            entry.Link,
            hashtags,
            MaxTweetLength);

        var preferredMedia = SelectPreferredMedia(entry);
        return posts
            .Select((text, index) => index == 0
                ? new SocialMediaPost(text, preferredMedia)
                : new SocialMediaPost(text))
            .ToList();
    }

    public async Task<SocialMediaPost> FormatGitHubChangelogPremiumPostForXAsync(
        GitHubChangelogEntry entry,
        bool useAi = false)
    {
        var plan = await BuildGitHubChangelogSummaryPlanAsync(entry, premiumMode: true, useAi, isWeekly: false);
        var hashtags = FormatGitHubChangelogHashtags(GetGitHubChangelogHashtags(entry));
        var text = BuildGitHubChangelogPremiumPost(
            $"📣 GitHub Changelog: {entry.Title}",
            entry.Link,
            hashtags,
            plan.TopThingsToKnow,
            plan.Paragraphs);

        return new SocialMediaPost(text, SelectPreferredMedia(entry));
    }

    public async Task<IReadOnlyList<SocialMediaPost>> FormatGitHubChangelogWeeklyRecapThreadForXAsync(
        IReadOnlyList<GitHubChangelogEntry> entries,
        DateTimeOffset weekStartPacific,
        DateTimeOffset weekEndPacific,
        bool useAi = false)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
        {
            throw new ArgumentException("At least one changelog entry is required.", nameof(entries));
        }

        var plan = await BuildGitHubChangelogWeeklySummaryPlanAsync(entries, weekStartPacific, weekEndPacific, premiumMode: false, useAi);
        var dateRange = FormatDateRange(weekStartPacific, weekEndPacific);
        var header = $"🗓️ GitHub weekly recap ({dateRange})\n📌 {entries.Count} updates this week";
        var hashtags = FormatGitHubChangelogHashtags(GetGitHubChangelogHashtags(entries));
        var bodyPosts = PackParagraphsIntoPosts(plan.Paragraphs, MaxTweetLength);
        var posts = AssembleThread(
            header,
            plan.TopThingsToKnow.Select(item => $"• {SanitizeBullet(item)}").ToList(),
            bodyPosts,
            entries.Count,
            "https://github.blog/changelog/",
            hashtags,
            MaxTweetLength);

        return posts.Select(text => new SocialMediaPost(text)).ToList();
    }

    public async Task<SocialMediaPost> FormatGitHubChangelogWeeklyRecapPremiumPostForXAsync(
        IReadOnlyList<GitHubChangelogEntry> entries,
        DateTimeOffset weekStartPacific,
        DateTimeOffset weekEndPacific,
        bool useAi = false)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
        {
            throw new ArgumentException("At least one changelog entry is required.", nameof(entries));
        }

        var plan = await BuildGitHubChangelogWeeklySummaryPlanAsync(entries, weekStartPacific, weekEndPacific, premiumMode: true, useAi);
        var dateRange = FormatDateRange(weekStartPacific, weekEndPacific);
        var hashtags = FormatGitHubChangelogHashtags(GetGitHubChangelogHashtags(entries));
        var text = BuildGitHubChangelogPremiumPost(
            $"🗓️ GitHub weekly recap ({dateRange})",
            "https://github.blog/changelog/",
            hashtags,
            plan.TopThingsToKnow,
            plan.Paragraphs);

        return new SocialMediaPost(text);
    }

    private async Task<ChangelogSummaryPlan> BuildGitHubChangelogSummaryPlanAsync(
        GitHubChangelogEntry entry,
        bool premiumMode,
        bool useAi,
        bool isWeekly)
    {
        var shouldUseAi = useAi || ShouldUseAiFromEnvironment();
        if (shouldUseAi && _releaseSummarizer != null)
        {
            try
            {
                var plan = await _releaseSummarizer.PlanGitHubChangelogSummaryAsync(
                    entry.Title,
                    entry.ContentHtml,
                    entry.SummaryText,
                    entry.Labels,
                    premiumMode,
                    isWeekly);

                if (plan != null)
                {
                    return plan;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to generate GitHub changelog AI summary for {Title}. Falling back.", entry.Title);
            }
        }

        return BuildFallbackChangelogPlan(entry.Title, entry.SummaryText, entry.ContentHtml, entry.Labels, premiumMode, isWeekly);
    }

    private async Task<ChangelogSummaryPlan> BuildGitHubChangelogWeeklySummaryPlanAsync(
        IReadOnlyList<GitHubChangelogEntry> entries,
        DateTimeOffset weekStartPacific,
        DateTimeOffset weekEndPacific,
        bool premiumMode,
        bool useAi)
    {
        var title = $"GitHub Changelog weekly recap ({FormatDateRange(weekStartPacific, weekEndPacific)})";
        var combinedSummary = string.Join(" ", entries.Select(entry => entry.SummaryText));
        var combinedContent = string.Join(
            "\n\n",
            entries.Select(entry =>
                $"{entry.Title}\nLabels: {string.Join(", ", entry.Labels)}\n{entry.ContentHtml}"));
        var labels = entries.SelectMany(entry => entry.Labels).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var shouldUseAi = useAi || ShouldUseAiFromEnvironment();

        if (shouldUseAi && _releaseSummarizer != null)
        {
            try
            {
                var plan = await _releaseSummarizer.PlanGitHubChangelogSummaryAsync(
                    title,
                    combinedContent,
                    combinedSummary,
                    labels,
                    premiumMode,
                    isWeekly: true);

                if (plan != null)
                {
                    return plan;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to generate GitHub changelog weekly AI summary. Falling back.");
            }
        }

        return BuildFallbackWeeklyChangelogPlan(entries, weekStartPacific, weekEndPacific, premiumMode);
    }

    private static ChangelogSummaryPlan BuildFallbackChangelogPlan(
        string title,
        string summaryText,
        string contentHtml,
        IReadOnlyList<string> labels,
        bool premiumMode,
        bool isWeekly)
    {
        var features = ExtractFeatureList(contentHtml)
            .Select(StripLeadingDecoration)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToList();

        if (features.Count == 0 && !string.IsNullOrWhiteSpace(summaryText))
        {
            features = SplitIntoSentences(summaryText)
                .Select(TruncateSentence)
                .Take(4)
                .ToList();
        }

        var paragraphs = new List<string>();
        var cleanSummary = CollapseWhitespace(summaryText);
        if (!string.IsNullOrWhiteSpace(cleanSummary))
        {
            paragraphs.Add(TruncateParagraph(cleanSummary, premiumMode ? 420 : 220));
        }

        if (features.Count > 0)
        {
            var labelText = labels.Count > 0 ? $" This update touches {string.Join(", ", labels.Take(3))}." : string.Empty;
            var highlights = string.Join(", ", features.Take(3));
            paragraphs.Add(TruncateParagraph($"Key highlights include {highlights}.{labelText}", premiumMode ? 420 : 220));
        }

        if (paragraphs.Count == 0)
        {
            paragraphs.Add(TruncateParagraph($"{title} introduces notable GitHub product updates with practical workflow improvements.", premiumMode ? 420 : 220));
        }

        return new ChangelogSummaryPlan
        {
            TopThingsToKnow = features.Select(SanitizeBullet).Take(4).ToList(),
            Paragraphs = paragraphs.Where(paragraph => !string.IsNullOrWhiteSpace(paragraph)).Take(2).ToList()
        };
    }

    private static ChangelogSummaryPlan BuildFallbackWeeklyChangelogPlan(
        IReadOnlyList<GitHubChangelogEntry> entries,
        DateTimeOffset weekStartPacific,
        DateTimeOffset weekEndPacific,
        bool premiumMode)
    {
        var topTitles = entries
            .OrderByDescending(entry => entry.Updated)
            .Select(entry => SanitizeBullet(entry.Title))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToList();

        var labels = entries
            .SelectMany(entry => entry.Labels)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToList();

        var dateRange = FormatDateRange(weekStartPacific, weekEndPacific);
        var paragraphs = new List<string>
        {
            TruncateParagraph($"This week on GitHub ({dateRange}), {entries.Count} changelog updates shipped across {string.Join(", ", labels.DefaultIfEmpty("multiple product areas"))}.", premiumMode ? 420 : 220)
        };

        if (topTitles.Count > 0)
        {
            paragraphs.Add(TruncateParagraph($"Standout updates included {string.Join(", ", topTitles.Take(3))}.", premiumMode ? 420 : 220));
        }

        return new ChangelogSummaryPlan
        {
            TopThingsToKnow = topTitles,
            Paragraphs = paragraphs
        };
    }

    private static IReadOnlyList<string> SelectPreferredMedia(GitHubChangelogEntry entry)
    {
        var firstVideo = entry.Media.FirstOrDefault(item => item.MediaType == GitHubChangelogMediaType.Video);
        if (firstVideo != null)
        {
            return [firstVideo.Url];
        }

        return entry.Media
            .Where(item => item.MediaType == GitHubChangelogMediaType.Image)
            .Take(2)
            .Select(item => item.Url)
            .ToList();
    }

    private static IReadOnlyList<string> PackParagraphsIntoPosts(IReadOnlyList<string> paragraphs, int maxPostLength)
    {
        var posts = new List<string>();

        foreach (var paragraph in paragraphs)
        {
            var cleanParagraph = CollapseWhitespace(paragraph);
            if (string.IsNullOrWhiteSpace(cleanParagraph))
            {
                continue;
            }

            if (cleanParagraph.Length <= maxPostLength)
            {
                posts.Add(cleanParagraph);
                continue;
            }

            var current = new StringBuilder();
            foreach (var sentence in SplitIntoSentences(cleanParagraph))
            {
                var candidate = current.Length == 0 ? sentence : $"{current} {sentence}";
                if (candidate.Length <= maxPostLength)
                {
                    current.Clear();
                    current.Append(candidate);
                    continue;
                }

                if (current.Length > 0)
                {
                    posts.Add(current.ToString());
                    current.Clear();
                }

                posts.Add(TruncateParagraph(sentence, maxPostLength));
            }

            if (current.Length > 0)
            {
                posts.Add(current.ToString());
            }
        }

        return posts;
    }

    private static string BuildGitHubChangelogPremiumPost(
        string header,
        string link,
        string hashtags,
        IReadOnlyList<string> topThingsToKnow,
        IReadOnlyList<string> paragraphs)
    {
        var sb = new StringBuilder();
        sb.AppendLine(header);
        sb.AppendLine();
        sb.AppendLine("Top things to know:");

        foreach (var bullet in topThingsToKnow.Take(5))
        {
            sb.AppendLine($"• {SanitizeBullet(bullet)}");
        }

        if (paragraphs.Count > 0)
        {
            sb.AppendLine();
            foreach (var paragraph in paragraphs)
            {
                sb.AppendLine(CollapseWhitespace(paragraph));
                sb.AppendLine();
            }
        }

        sb.AppendLine(link);
        sb.AppendLine();
        sb.Append(hashtags);

        var text = sb.ToString().Trim();
        return text.Length <= MaxPremiumTweetLength
            ? text
            : text[..(MaxPremiumTweetLength - 3)] + "...";
    }

    private static string FormatGitHubChangelogHashtags(IReadOnlyList<string> hashtags)
        => string.Join(" ", hashtags.Where(tag => !string.IsNullOrWhiteSpace(tag)).Distinct(StringComparer.OrdinalIgnoreCase).Take(3));

    private static IReadOnlyList<string> GetGitHubChangelogHashtags(GitHubChangelogEntry entry)
        => GetGitHubChangelogHashtags([entry]);

    private static IReadOnlyList<string> GetGitHubChangelogHashtags(IReadOnlyList<GitHubChangelogEntry> entries)
    {
        var corpus = string.Join(
            " | ",
            entries.Select(entry => $"{entry.Title} {string.Join(" ", entry.Labels)}"));
        var lowerCorpus = corpus.ToLowerInvariant();
        var hashtags = new List<string>();

        void AddIf(bool condition, string hashtag)
        {
            if (condition && !hashtags.Contains(hashtag, StringComparer.OrdinalIgnoreCase))
            {
                hashtags.Add(hashtag);
            }
        }

        AddIf(lowerCorpus.Contains("copilot"), GitHubCopilotHashtag);
        AddIf(lowerCorpus.Contains("actions"), GitHubActionsHashtag);
        AddIf(lowerCorpus.Contains("codespaces"), GitHubCodespacesHashtag);
        AddIf(lowerCorpus.Contains("github cli") || lowerCorpus.Contains(" cli "), GitHubCliHashtag);
        AddIf(lowerCorpus.Contains("enterprise"), GitHubEnterpriseHashtag);
        AddIf(lowerCorpus.Contains("issues"), GitHubIssuesHashtag);
        AddIf(lowerCorpus.Contains("projects"), GitHubProjectsHashtag);
        AddIf(lowerCorpus.Contains("security") || lowerCorpus.Contains("secret scanning") || lowerCorpus.Contains("code scanning"), GitHubSecurityHashtag);
        AddIf(lowerCorpus.Contains("discussion"), GitHubDiscussionsHashtag);
        AddIf(lowerCorpus.Contains("mobile"), GitHubMobileHashtag);
        AddIf(lowerCorpus.Contains("pull request") || lowerCorpus.Contains("pr "), GitHubPrsHashtag);
        AddIf(lowerCorpus.Contains("agent") || lowerCorpus.Contains("model") || lowerCorpus.Contains("mcp"), GitHubAiHashtag);
        AddIf(lowerCorpus.Contains("visual studio code") || lowerCorpus.Contains("vs code") || lowerCorpus.Contains("vscode"), VSCodeHashtag);

        hashtags.Add(GitHubChangelogHashtag);
        return hashtags.Take(3).ToList();
    }

    private static string SanitizeBullet(string text)
        => TruncateForDisplay(StripLeadingDecoration(CollapseWhitespace(text)), 72);

    private static string StripLeadingDecoration(string text)
        => LeadingDecorationPattern().Replace(text, string.Empty).Trim();

    private static List<string> SplitIntoSentences(string text)
        => SentenceSplitPattern()
            .Split(CollapseWhitespace(text))
            .Select(sentence => sentence.Trim())
            .Where(sentence => !string.IsNullOrWhiteSpace(sentence))
            .ToList();

    private static string TruncateParagraph(string text, int maxLength)
    {
        var clean = CollapseWhitespace(text);
        if (clean.Length <= maxLength)
        {
            return clean;
        }

        return clean[..(maxLength - 3)].TrimEnd() + "...";
    }

    private static string TruncateSentence(string text)
        => TruncateForDisplay(CollapseWhitespace(text), 72);

    private static string TruncateForDisplay(string text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length <= maxLength)
        {
            return text.Trim();
        }

        return text[..(maxLength - 3)].TrimEnd() + "...";
    }

    private static string CollapseWhitespace(string text)
        => string.Join(" ", text.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries))
            .Trim();
}
