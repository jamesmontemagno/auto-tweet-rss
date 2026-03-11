using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace AutoTweetRss.Services;

public partial class TweetFormatterService
{
    private const int GitHubChangelogThreadBuffer = 20;
    private const int GitHubChangelogMaxThreadPosts = 4;
    private const int ThreadIndicatorReserve = 8;
    private const int GitHubChangelogSinglePostMaxLength = 260;
    private const int GitHubChangelogSinglePostBuffer = 4;
    private const int GitHubChangelogThreadParagraphMaxLength = 200;
    private const int GitHubChangelogPremiumParagraphMaxLength = 420;
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
        var safeMaxPostLength = GetGitHubChangelogSafePostLength();
        var plan = await BuildGitHubChangelogSummaryPlanAsync(entry, premiumMode: false, useAi, isWeekly: false);
        var header = TruncateForDisplay(entry.Title, 110);
        var threadContent = SplitGitHubChangelogSummaryContent(plan.Paragraphs, plan.TopThingsToKnow, $"{header} introduces a new GitHub changelog update.");
        var highlights = FitHighlightsForFirstPost(header, plan.TopThingsToKnow, safeMaxPostLength)
            .Select(item => $"• {SanitizeBullet(item)}")
            .ToList();
        var hashtags = FormatGitHubChangelogHashtags(GetGitHubChangelogHashtags(entry));
        var maxBodyPosts = GitHubChangelogMaxThreadPosts - 2;
        var bodyPosts = PackParagraphsIntoPosts(threadContent.RemainingParagraphs, safeMaxPostLength, maxBodyPosts);
        var posts = AssembleGitHubChangelogThread(
            header,
            threadContent.SummarySentence,
            highlights,
            bodyPosts,
            entry.Link,
            hashtags,
            safeMaxPostLength);

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
        var premiumContent = SplitGitHubChangelogSummaryContent(plan.Paragraphs, plan.TopThingsToKnow, $"{entry.Title} introduces a new GitHub changelog update.");
        var text = BuildGitHubChangelogPremiumPost(
            entry.Title,
            entry.Link,
            hashtags,
            premiumContent.SummarySentence,
            plan.TopThingsToKnow,
            premiumContent.RemainingParagraphs);

        return new SocialMediaPost(text);
    }

    public async Task<SocialMediaPost> FormatGitHubChangelogSinglePostForXAsync(
        GitHubChangelogEntry entry,
        bool useAi = false)
    {
        var reservedLength = UrlLength
            + GetTextLength("\n\n", useXWeightedLength: true)
            + GitHubChangelogSinglePostBuffer;
        var availableForSummary = Math.Max(0, GitHubChangelogSinglePostMaxLength - reservedLength);

        string summary;
        var shouldUseAi = useAi || ShouldUseAiFromEnvironment();
        if (shouldUseAi && _releaseSummarizer != null)
        {
            try
            {
                summary = await _releaseSummarizer.SummarizeGitHubChangelogSinglePostAsync(
                    entry.Title,
                    BuildGitHubChangelogAiPayload(entry),
                    availableForSummary) ?? string.Empty;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to generate GitHub changelog single post AI summary for {Title}. Falling back.", entry.Title);
                summary = string.Empty;
            }
        }
        else
        {
            summary = string.Empty;
        }

        if (string.IsNullOrWhiteSpace(summary))
        {
            summary = BuildGitHubChangelogSinglePostFallback(entry, availableForSummary);
        }

        summary = GitHubChangelogSinglePostSummaryNormalizer.Normalize(summary, availableForSummary);
        var text = $"{summary}\n\n{entry.Link}";
        if (!XPostLengthHelper.FitsWithinLimit(text, GitHubChangelogSinglePostMaxLength))
        {
            summary = GitHubChangelogSinglePostSummaryNormalizer.Normalize(summary, Math.Max(0, availableForSummary - 1));
            text = $"{summary}\n\n{entry.Link}";
        }

        return new SocialMediaPost(text);
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
        var safeMaxPostLength = GetGitHubChangelogSafePostLength();
        var highlights = FitHighlightsForFirstPost(header, plan.TopThingsToKnow, safeMaxPostLength)
            .Select(item => $"• {SanitizeBullet(item)}")
            .ToList();
        var hashtags = FormatGitHubChangelogHashtags(GetGitHubChangelogHashtags(entries));
        var weeklyContent = SplitGitHubChangelogSummaryContent(plan.Paragraphs, plan.TopThingsToKnow, $"This week shipped {entries.Count} GitHub changelog updates.");
        var bodyPosts = PackParagraphsIntoPosts(weeklyContent.RemainingParagraphs, safeMaxPostLength);
        var posts = AssembleGitHubChangelogThread(
            header,
            weeklyContent.SummarySentence,
            highlights,
            bodyPosts,
            "https://github.blog/changelog/",
            hashtags,
            safeMaxPostLength);

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
        var premiumContent = SplitGitHubChangelogSummaryContent(plan.Paragraphs, plan.TopThingsToKnow, $"This week shipped {entries.Count} GitHub changelog updates.");
        var text = BuildGitHubChangelogPremiumPost(
            $"🗓️ GitHub weekly recap ({dateRange})",
            "https://github.blog/changelog/",
            hashtags,
            premiumContent.SummarySentence,
            plan.TopThingsToKnow,
            premiumContent.RemainingParagraphs);

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
                    BuildGitHubChangelogAiPayload(entry),
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
        var combinedContent = string.Join("\n\n---\n\n", entries.Select(BuildGitHubChangelogAiPayload));
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
            paragraphs.Add(TruncateParagraph(cleanSummary, premiumMode ? GitHubChangelogPremiumParagraphMaxLength : GitHubChangelogThreadParagraphMaxLength));
        }

        if (features.Count > 0)
        {
            var labelText = labels.Count > 0 ? $" This update touches {string.Join(", ", labels.Take(3))}." : string.Empty;
            var highlights = string.Join(", ", features.Take(3));
            paragraphs.Add(TruncateParagraph($"Key highlights include {highlights}.{labelText}", premiumMode ? GitHubChangelogPremiumParagraphMaxLength : GitHubChangelogThreadParagraphMaxLength));
        }

        if (paragraphs.Count == 0)
        {
            paragraphs.Add(TruncateParagraph($"{title} introduces notable GitHub product updates with practical workflow improvements.", premiumMode ? GitHubChangelogPremiumParagraphMaxLength : GitHubChangelogThreadParagraphMaxLength));
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
            TruncateParagraph($"This week on GitHub ({dateRange}), {entries.Count} changelog updates shipped across {string.Join(", ", labels.DefaultIfEmpty("multiple product areas"))}.", premiumMode ? GitHubChangelogPremiumParagraphMaxLength : GitHubChangelogThreadParagraphMaxLength)
        };

        if (topTitles.Count > 0)
        {
            paragraphs.Add(TruncateParagraph($"Standout updates included {string.Join(", ", topTitles.Take(3))}.", premiumMode ? GitHubChangelogPremiumParagraphMaxLength : GitHubChangelogThreadParagraphMaxLength));
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

    private static string BuildGitHubChangelogSinglePostFallback(GitHubChangelogEntry entry, int maxLength)
    {
        var plan = BuildFallbackChangelogPlan(entry.Title, entry.SummaryText, entry.ContentHtml, entry.Labels, premiumMode: false, isWeekly: false);
        var content = SplitGitHubChangelogSummaryContent(plan.Paragraphs, plan.TopThingsToKnow, "GitHub shipped a new changelog update.");
        var bulletLines = plan.TopThingsToKnow
            .Select(item => $"• {StripLeadingDecoration(CollapseWhitespace(item))}")
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Take(4)
            .ToList();

        var summary = bulletLines.Count == 0
            ? content.SummarySentence
            : $"{content.SummarySentence}\n\n{string.Join("\n", bulletLines)}";
        return GitHubChangelogSinglePostSummaryNormalizer.Normalize(summary, maxLength);
    }

    private static string BuildGitHubChangelogAiPayload(GitHubChangelogEntry entry)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Title: {entry.Title}");
        sb.AppendLine($"Link: {entry.Link}");
        sb.AppendLine($"Updated: {entry.Updated:O}");
        sb.AppendLine($"Changelog Type: {entry.ChangelogType}");
        sb.AppendLine($"Labels: {(entry.Labels.Count > 0 ? string.Join(", ", entry.Labels) : "none")}");

        if (entry.Media.Count > 0)
        {
            sb.AppendLine("Media:");
            foreach (var media in entry.Media)
            {
                sb.AppendLine($"- {media.MediaType}: {media.Url}");
            }
        }

        if (!string.IsNullOrWhiteSpace(entry.SummaryText))
        {
            sb.AppendLine();
            sb.AppendLine("Summary Text:");
            sb.AppendLine(entry.SummaryText);
        }

        if (!string.IsNullOrWhiteSpace(entry.ContentText))
        {
            sb.AppendLine();
            sb.AppendLine("Content Text:");
            sb.AppendLine(entry.ContentText);
        }

        if (!string.IsNullOrWhiteSpace(entry.ContentHtml))
        {
            sb.AppendLine();
            sb.AppendLine("Content HTML:");
            sb.AppendLine(entry.ContentHtml);
        }

        return sb.ToString().Trim();
    }

    private static int GetGitHubChangelogSafePostLength()
        => Math.Max(200, MaxTweetLength - GitHubChangelogThreadBuffer);

    private static IReadOnlyList<string> AssembleGitHubChangelogThread(
        string header,
        string summarySentence,
        IReadOnlyList<string> highlights,
        IReadOnlyList<string> followUpPosts,
        string link,
        string hashtags,
        int maxPostLength)
    {
        var posts = new List<string>();
        const string leadIn = "🧵 See thread below 👇";

        var summaryBlock = string.IsNullOrWhiteSpace(summarySentence)
            ? string.Empty
            : CollapseWhitespace(summarySentence);
        var highlightBlock = highlights.Count > 0 ? string.Join("\n", highlights) : string.Empty;
        var firstPost = string.IsNullOrEmpty(summaryBlock)
            ? (string.IsNullOrEmpty(highlightBlock)
                ? $"{header}\n\n{leadIn}"
                : $"{header}\n\n{highlightBlock}\n\n{leadIn}")
            : (string.IsNullOrEmpty(highlightBlock)
                ? $"{header}\n\n{summaryBlock}\n\n{leadIn}"
                : $"{header}\n\n{summaryBlock}\n{highlightBlock}\n\n{leadIn}");

        posts.Add(EnsurePostFits(firstPost, maxPostLength));
        posts.AddRange(followUpPosts.Select(post => EnsurePostFits(post, maxPostLength)));

        var lastPostContent = $"{link}\n\n{hashtags}";
        if (posts.Count >= 2)
        {
            var prevIndex = posts.Count - 1;
            var merged = $"{posts[prevIndex]}\n\n{lastPostContent}";
            if (XPostLengthHelper.FitsWithinLimit(merged, maxPostLength))
            {
                posts[prevIndex] = merged;
            }
            else
            {
                posts.Add(EnsurePostFits(lastPostContent, maxPostLength));
            }
        }
        else
        {
            posts.Add(EnsurePostFits(lastPostContent, maxPostLength));
        }

        for (var index = 0; index < posts.Count; index++)
        {
            var indicator = $"🧵 {index + 1}/{posts.Count}";
            var withIndicator = $"{posts[index]}\n\n{indicator}";
            if (XPostLengthHelper.FitsWithinLimit(withIndicator, maxPostLength))
            {
                posts[index] = withIndicator;
                continue;
            }

            var availableForContent = maxPostLength - XPostLengthHelper.GetWeightedLength($"\n\n{indicator}");
            if (availableForContent > 0)
            {
                posts[index] = $"{XPostLengthHelper.TruncateToWeightedLength(posts[index], availableForContent)}\n\n{indicator}";
            }
        }

        return posts;
    }

    private static string EnsurePostFits(string text, int maxPostLength)
        => XPostLengthHelper.FitsWithinLimit(text, maxPostLength)
            ? text
            : XPostLengthHelper.TruncateToWeightedLength(text, maxPostLength);

    private static IReadOnlyList<string> FitHighlightsForFirstPost(
        string header,
        IReadOnlyList<string> highlights,
        int maxPostLength)
    {
        if (highlights.Count == 0)
        {
            return [];
        }

        const string leadIn = "🧵 See thread below 👇";
        var safeMaxLength = Math.Max(80, maxPostLength - ThreadIndicatorReserve);
        var fittedHighlights = new List<string>();

        foreach (var highlight in highlights)
        {
            var sanitizedHighlight = SanitizeBullet(highlight);
            var candidateHighlights = fittedHighlights
                .Append($"• {sanitizedHighlight}")
                .ToList();
            var highlightBlock = string.Join("\n", candidateHighlights);
            var candidate = $"{header}\n\n{highlightBlock}\n\n{leadIn}";

            if (!XPostLengthHelper.FitsWithinLimit(candidate, safeMaxLength))
            {
                break;
            }

            fittedHighlights.Add(sanitizedHighlight);
        }

        return fittedHighlights;
    }

    private static IReadOnlyList<string> PackParagraphsIntoPosts(
        IReadOnlyList<string> paragraphs,
        int maxPostLength,
        int? maxPosts = null)
    {
        var posts = new List<string>();
        var safeMaxLength = Math.Max(80, maxPostLength - ThreadIndicatorReserve);

        foreach (var paragraph in paragraphs)
        {
            var cleanParagraph = CollapseWhitespace(paragraph);
            if (string.IsNullOrWhiteSpace(cleanParagraph))
            {
                continue;
            }

            if (XPostLengthHelper.FitsWithinLimit(cleanParagraph, safeMaxLength))
            {
                posts.Add(cleanParagraph);
                continue;
            }

            var current = new StringBuilder();
            foreach (var sentence in SplitIntoSentences(cleanParagraph))
            {
                var candidate = current.Length == 0 ? sentence : $"{current} {sentence}";
                if (XPostLengthHelper.FitsWithinLimit(candidate, safeMaxLength))
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

                posts.Add(TruncateParagraph(sentence, safeMaxLength));
            }

            if (current.Length > 0)
            {
                posts.Add(current.ToString());
            }
        }

        if (maxPosts.HasValue && posts.Count > maxPosts.Value)
        {
            var trimmedPosts = posts.Take(maxPosts.Value - 1).ToList();
            var remainingContent = string.Join(" ", posts.Skip(maxPosts.Value - 1));
            trimmedPosts.Add(TruncateParagraph(remainingContent, safeMaxLength));
            return trimmedPosts;
        }

        return posts;
    }

    private static string BuildGitHubChangelogPremiumPost(
        string header,
        string link,
        string hashtags,
        string summarySentence,
        IReadOnlyList<string> topThingsToKnow,
        IReadOnlyList<string> paragraphs)
    {
        var sb = new StringBuilder();
        sb.AppendLine(header);
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(summarySentence))
        {
            sb.AppendLine(CollapseWhitespace(summarySentence));
            sb.AppendLine();
        }

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
        return XPostLengthHelper.FitsWithinLimit(text, MaxPremiumTweetLength)
            ? text
            : XPostLengthHelper.TruncateToWeightedLength(text, MaxPremiumTweetLength);
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

    private static (string SummarySentence, IReadOnlyList<string> RemainingParagraphs) SplitGitHubChangelogSummaryContent(
        IReadOnlyList<string> paragraphs,
        IReadOnlyList<string> highlights,
        string fallbackSummary)
    {
        var remainingParagraphs = new List<string>();

        foreach (var paragraph in paragraphs)
        {
            var cleanParagraph = CollapseWhitespace(paragraph);
            if (string.IsNullOrWhiteSpace(cleanParagraph))
            {
                continue;
            }

            var sentences = SplitIntoSentences(cleanParagraph);
            if (sentences.Count == 0)
            {
                continue;
            }

            var summarySentence = EnsureSentence(sentences[0]);
            if (sentences.Count > 1)
            {
                remainingParagraphs.Add(string.Join(" ", sentences.Skip(1)));
            }

            remainingParagraphs.AddRange(
                paragraphs
                    .SkipWhile(item => !ReferenceEquals(item, paragraph))
                    .Skip(1)
                    .Select(CollapseWhitespace)
                    .Where(item => !string.IsNullOrWhiteSpace(item)));

            return (summarySentence, remainingParagraphs);
        }

        var fallbackHighlight = highlights
            .Select(SanitizeBullet)
            .FirstOrDefault(item => !string.IsNullOrWhiteSpace(item));

        return (!string.IsNullOrWhiteSpace(fallbackHighlight)
                ? EnsureSentence(fallbackHighlight)
                : EnsureSentence(fallbackSummary),
            []);
    }

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
        if (XPostLengthHelper.FitsWithinLimit(clean, maxLength))
        {
            return clean;
        }

        return XPostLengthHelper.TruncateToWeightedLength(clean, maxLength);
    }

    private static string TruncateSentence(string text)
        => TruncateForDisplay(CollapseWhitespace(text), 72);

    private static string EnsureSentence(string text)
    {
        var clean = CollapseWhitespace(text);
        if (string.IsNullOrWhiteSpace(clean))
        {
            return string.Empty;
        }

        return clean[^1] is '.' or '!' or '?'
            ? clean
            : $"{clean}.";
    }

    private static string TruncateForDisplay(string text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text) || XPostLengthHelper.FitsWithinLimit(text, maxLength))
        {
            return text.Trim();
        }

        return XPostLengthHelper.TruncateToWeightedLength(text, maxLength);
    }

    private static string CollapseWhitespace(string text)
        => string.Join(" ", text.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries))
            .Trim();
}
