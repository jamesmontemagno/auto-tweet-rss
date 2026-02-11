using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;

namespace AutoTweetRss.Services;

/// <summary>
/// Service for fetching and parsing VS Code Insiders release notes from raw markdown on GitHub
/// </summary>
public partial class VSCodeReleaseNotesService
{
    private readonly ILogger<VSCodeReleaseNotesService> _logger;
    private readonly HttpClient _httpClient;
    private readonly ReleaseSummarizerService? _releaseSummarizer;
    private readonly VSCodeSummaryCacheService? _cacheService;
    
    // Raw GitHub URL pattern for release notes markdown
    private const string RawGitHubBaseUrl = "https://raw.githubusercontent.com/microsoft/vscode-docs/refs/heads/main/release-notes/";
    
    // aka.ms redirect that always resolves to the current insiders release notes markdown
    private const string InsidersRedirectUrl = "https://aka.ms/vscode/updates/insiders";
    
    // Required front-matter value to validate this is an Insiders release
    private const string RequiredProductEdition = "Insiders";
    
    // Cached resolved version number from the aka.ms redirect (avoids repeated HTTP calls)
    private int? _resolvedVersionNumber;
    
    // Constants for text length thresholds
    private const int MinBulletLength = 5;
    private const int MaxTitleLength = 80;
    private const int MaxSentenceEndIndex = 100;
    private const int TruncatedTitleLength = 77;
    
    // Compiled regex for matching markdown date headings like "## February 11, 2026"
    [GeneratedRegex(@"^##\s+(January|February|March|April|May|June|July|August|September|October|November|December)\s+(\d{1,2})(?:,\s*(\d{4}))?", RegexOptions.IgnoreCase)]
    private static partial Regex MarkdownDateHeadingPattern();

    // Regex for extracting date from any text (used for category extraction)
    [GeneratedRegex(@"(January|February|March|April|May|June|July|August|September|October|November|December)\s+(\d{1,2})(?:,\s*(\d{4}))?", RegexOptions.IgnoreCase)]
    private static partial Regex DatePattern();

    // Regex for extracting version number from URLs like v1_110.md
    [GeneratedRegex(@"v1_(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex VersionPattern();

    // Regex for extracting markdown links like [#291961](https://github.com/...)
    [GeneratedRegex(@"\[#?\d+\]\((https?://[^\)]+)\)")]
    private static partial Regex MarkdownLinkPattern();

    // Regex for stripping all markdown link syntax: [text](url) → text
    [GeneratedRegex(@"\[([^\]]*)\]\([^\)]*\)")]
    private static partial Regex MarkdownLinkStripPattern();

    public VSCodeReleaseNotesService(
        ILogger<VSCodeReleaseNotesService> logger,
        IHttpClientFactory httpClientFactory,
        ReleaseSummarizerService? releaseSummarizer = null,
        VSCodeSummaryCacheService? cacheService = null)
    {
        _logger = logger;
        _httpClient = httpClientFactory.CreateClient();
        _releaseSummarizer = releaseSummarizer;
        _cacheService = cacheService;
    }

    /// <summary>
    /// Gets VS Code Insiders release notes for today's date
    /// </summary>
    public async Task<VSCodeReleaseNotes?> GetTodayReleaseNotesAsync()
    {
        var today = DateTime.UtcNow.Date;
        return await GetReleaseNotesForDateAsync(today);
    }

    /// <summary>
    /// Gets the VS Code Insiders release notes for a specific date
    /// </summary>
    public async Task<VSCodeReleaseNotes?> GetReleaseNotesForDateAsync(DateTime targetDate)
    {
        foreach (var mdUrl in await GetCandidateMarkdownUrlsAsync(targetDate))
        {
            try
            {
                _logger.LogInformation("Fetching VS Code Insiders release notes from {Url} for date {Date}",
                    mdUrl, targetDate.ToString("yyyy-MM-dd"));

                var markdown = await _httpClient.GetStringAsync(mdUrl);

                if (!ValidateFrontMatter(markdown, mdUrl))
                    continue;

                var sections = ParseMarkdownSections(markdown, targetDate.Year);
                var features = sections
                    .Where(s => s.Date.Date == targetDate.Date)
                    .SelectMany(s => s.Features)
                    .ToList();

                if (features.Count == 0)
                {
                    _logger.LogInformation("No release notes found for date {Date} at {Url}",
                        targetDate.ToString("yyyy-MM-dd"), mdUrl);
                    continue;
                }

                _logger.LogInformation("Found {Count} features for date {Date} at {Url}",
                    features.Count, targetDate.ToString("yyyy-MM-dd"), mdUrl);

                return new VSCodeReleaseNotes
                {
                    Date = targetDate,
                    Features = features,
                    VersionUrl = mdUrl
                };
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, "Failed to fetch VS Code release notes from {Url}", mdUrl);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching VS Code Insiders release notes from {Url}", mdUrl);
            }
        }

        return null;
    }

    /// <summary>
    /// Gets VS Code Insiders release notes for a date range (inclusive)
    /// </summary>
    public async Task<VSCodeReleaseNotes?> GetReleaseNotesForDateRangeAsync(DateTime startDate, DateTime endDate)
    {
        if (startDate > endDate)
        {
            (startDate, endDate) = (endDate, startDate);
        }

        var endUrls = await GetCandidateMarkdownUrlsAsync(endDate);
        var startUrls = await GetCandidateMarkdownUrlsAsync(startDate);
        var candidateUrls = endUrls
            .Concat(startUrls)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var allFeatures = new List<VSCodeFeature>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? versionUrlForResponse = null;

        foreach (var mdUrl in candidateUrls)
        {
            try
            {
                _logger.LogInformation("Fetching VS Code Insiders release notes from {Url} for range {StartDate} to {EndDate}",
                    mdUrl, startDate.ToString("yyyy-MM-dd"), endDate.ToString("yyyy-MM-dd"));

                var markdown = await _httpClient.GetStringAsync(mdUrl);

                if (!ValidateFrontMatter(markdown, mdUrl))
                    continue;

                var sections = ParseMarkdownSections(markdown, endDate.Year);
                var features = sections
                    .Where(s => s.Date.Date >= startDate.Date && s.Date.Date <= endDate.Date)
                    .SelectMany(s => s.Features)
                    .ToList();

                if (features.Count == 0)
                {
                    _logger.LogInformation("No release notes found for range {StartDate} to {EndDate} at {Url}",
                        startDate.ToString("yyyy-MM-dd"), endDate.ToString("yyyy-MM-dd"), mdUrl);
                    continue;
                }

                _logger.LogInformation("Found {Count} features for range {StartDate} to {EndDate} at {Url}",
                    features.Count, startDate.ToString("yyyy-MM-dd"), endDate.ToString("yyyy-MM-dd"), mdUrl);

                versionUrlForResponse ??= mdUrl;

                foreach (var feature in features)
                {
                    var key = $"{feature.Title}|{feature.Description}";
                    if (seen.Add(key))
                    {
                        allFeatures.Add(feature);
                    }
                }
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, "Failed to fetch VS Code release notes from {Url}", mdUrl);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching VS Code Insiders release notes from {Url}", mdUrl);
            }
        }

        if (allFeatures.Count == 0)
        {
            return null;
        }

        return new VSCodeReleaseNotes
        {
            Date = endDate.Date,
            Features = allFeatures,
            VersionUrl = versionUrlForResponse ?? candidateUrls.First()
        };
    }

    /// <summary>
    /// Gets the full VS Code Insiders release notes for the current version
    /// </summary>
    public async Task<VSCodeReleaseNotes?> GetFullReleaseNotesAsync()
    {
        var today = DateTime.UtcNow.Date;
        
        foreach (var mdUrl in await GetCandidateMarkdownUrlsAsync(today))
        {
            try
            {
                _logger.LogInformation("Fetching full VS Code Insiders release notes from {Url}", mdUrl);

                var markdown = await _httpClient.GetStringAsync(mdUrl);

                if (!ValidateFrontMatter(markdown, mdUrl))
                    continue;

                var sections = ParseMarkdownSections(markdown, today.Year);
                var features = sections.SelectMany(s => s.Features).ToList();

                if (features.Count == 0)
                {
                    _logger.LogInformation("No release notes found at {Url}", mdUrl);
                    continue;
                }

                _logger.LogInformation("Found {Count} total features at {Url}", features.Count, mdUrl);

                return new VSCodeReleaseNotes
                {
                    Date = today,
                    Features = features,
                    VersionUrl = mdUrl
                };
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, "Failed to fetch VS Code release notes from {Url}", mdUrl);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching VS Code Insiders release notes from {Url}", mdUrl);
            }
        }

        return null;
    }

    /// <summary>
    /// Generates an AI-powered summary of the release notes
    /// </summary>
    public async Task<string> GenerateSummaryAsync(
        VSCodeReleaseNotes notes,
        int maxLength = 500,
        string format = "default",
        bool forceRefresh = false,
        bool aiOnly = false,
        bool isThisWeek = false)
    {
        // Try to get from cache first (unless forceRefresh is true)
        if (!forceRefresh && _cacheService != null)
        {
            var cachedSummary = await _cacheService.GetCachedSummaryAsync(notes.Date, format);
            if (cachedSummary != null)
            {
                _logger.LogInformation("Using cached summary for {Date} with format {Format}", 
                    notes.Date.ToString("yyyy-MM-dd"), format);
                return cachedSummary;
            }
        }

        // Generate new summary
        string summary;
        if (_releaseSummarizer == null)
        {
            summary = GenerateFallbackSummary(notes);
        }
        else
        {
            try
            {
                // Build content string from features
                var content = string.Join("\n", notes.Features.Select(f => $"- {f.Title}: {f.Description}"));

                var feedType = aiOnly
                    ? (isThisWeek ? "vscode-week-ai" : "vscode-ai")
                    : (isThisWeek ? "vscode-week" : "vscode");

                summary = await _releaseSummarizer.SummarizeReleaseAsync(
                    $"VS Code Insiders {notes.Date:MMMM d, yyyy}",
                    content,
                    maxLength,
                    feedType: feedType);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AI summarization failed, using fallback");
                summary = GenerateFallbackSummary(notes);
            }
        }

        // Cache the generated summary
        if (_cacheService != null)
        {
            await _cacheService.SetCachedSummaryAsync(notes.Date, summary, format);
        }

        return summary;
    }

    private static string GenerateFallbackSummary(VSCodeReleaseNotes notes)
    {
        var featureCount = notes.Features.Count;
        var categories = notes.Features
            .Select(f => f.Category)
            .Where(c => !string.IsNullOrEmpty(c))
            .Distinct()
            .ToList();

        var categoryText = categories.Count > 0 
            ? $" including updates to {string.Join(", ", categories.Take(3))}"
            : "";

        return $"VS Code Insiders received {featureCount} update{(featureCount != 1 ? "s" : "")} on {notes.Date:MMMM d, yyyy}{categoryText}.";
    }

    // ── Front-matter validation ──────────────────────────────────────────────

    /// <summary>
    /// Validates that the markdown contains front matter with ProductEdition: Insiders.
    /// Returns false (and logs) if validation fails, so callers skip to the next candidate.
    /// </summary>
    private bool ValidateFrontMatter(string markdown, string url)
    {
        // Front matter is between the first pair of --- delimiters
        if (!markdown.StartsWith("---"))
        {
            _logger.LogWarning("No front matter found in {Url}, skipping", url);
            return false;
        }

        var endIndex = markdown.IndexOf("---", 3, StringComparison.Ordinal);
        if (endIndex < 0)
        {
            _logger.LogWarning("Malformed front matter in {Url}, skipping", url);
            return false;
        }

        var frontMatter = markdown[3..endIndex];

        // Look for ProductEdition: Insiders (case-insensitive value check)
        foreach (var line in frontMatter.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("ProductEdition:", StringComparison.OrdinalIgnoreCase))
            {
                var value = trimmed["ProductEdition:".Length..].Trim();
                if (string.Equals(value, RequiredProductEdition, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                _logger.LogWarning("ProductEdition is '{Value}' (expected '{Expected}') in {Url}, skipping",
                    value, RequiredProductEdition, url);
                return false;
            }
        }

        _logger.LogWarning("ProductEdition not found in front matter of {Url}, skipping", url);
        return false;
    }

    // ── Markdown parsing ─────────────────────────────────────────────────────

    /// <summary>
    /// Parses the markdown into date-sections, each containing a list of features
    /// extracted from bullet points (* ...) under ## Date headings.
    /// </summary>
    private List<MarkdownDateSection> ParseMarkdownSections(string markdown, int defaultYear)
    {
        var sections = new List<MarkdownDateSection>();
        var lines = markdown.Split('\n');

        MarkdownDateSection? currentSection = null;
        var currentBulletLines = new List<string>();
        string currentCategory = "General";

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimEnd('\r');

            // Check for ## date heading
            var dateMatch = MarkdownDateHeadingPattern().Match(trimmed);
            if (dateMatch.Success)
            {
                // Flush current bullet if any
                FlushBullet(currentBulletLines, currentSection, currentCategory);

                var parsedDate = ParseDateFromMatch(dateMatch, defaultYear);
                if (parsedDate != null)
                {
                    currentSection = new MarkdownDateSection { Date = parsedDate.Value };
                    sections.Add(currentSection);
                    currentCategory = ExtractCategory(trimmed);
                }
                continue;
            }

            // If no current section yet, skip (preamble / front matter)
            if (currentSection == null) continue;

            // Check for a new bullet point (* ...)
            if (trimmed.StartsWith("* "))
            {
                // Flush previous bullet
                FlushBullet(currentBulletLines, currentSection, currentCategory);
                currentBulletLines.Add(trimmed[2..].TrimEnd());
                continue;
            }

            // Continuation line of a multi-line bullet (indented or non-empty, non-heading)
            if (currentBulletLines.Count > 0 && !string.IsNullOrWhiteSpace(trimmed)
                && !trimmed.StartsWith('#'))
            {
                currentBulletLines.Add(trimmed.TrimEnd());
                continue;
            }

            // Blank line or heading — flush bullet
            if (currentBulletLines.Count > 0)
            {
                FlushBullet(currentBulletLines, currentSection, currentCategory);
            }
        }

        // Flush any trailing bullet
        FlushBullet(currentBulletLines, currentSection, currentCategory);

        return sections;
    }

    /// <summary>
    /// Joins accumulated bullet continuation lines into a single feature and adds it to the section.
    /// </summary>
    private void FlushBullet(List<string> bulletLines, MarkdownDateSection? section, string category)
    {
        if (bulletLines.Count == 0 || section == null) return;

        var rawText = string.Join(" ", bulletLines).Trim();
        bulletLines.Clear();

        if (rawText.Length < MinBulletLength) return;

        // Extract the first issue/PR link if present
        var linkMatch = MarkdownLinkPattern().Match(rawText);
        var link = linkMatch.Success ? linkMatch.Groups[1].Value : null;

        // Strip markdown link syntax for clean text
        var cleanText = MarkdownLinkStripPattern().Replace(rawText, "$1").Trim();
        // Remove trailing issue numbers like #291961
        cleanText = Regex.Replace(cleanText, @"\s*#\d+\s*$", "").Trim();

        if (string.IsNullOrWhiteSpace(cleanText) || cleanText.Length < MinBulletLength) return;

        var title = TruncateTitle(cleanText);

        section.Features.Add(new VSCodeFeature
        {
            Title = title,
            Description = cleanText,
            Category = category,
            Link = link
        });
    }

    // ── URL resolution ───────────────────────────────────────────────────────

    /// <summary>
    /// Gets candidate raw-GitHub markdown URLs by resolving the aka.ms redirect
    /// to discover the current version, then trying that version and the previous one.
    /// Falls back to date-based calculation if the redirect fails.
    /// </summary>
    private async Task<IReadOnlyList<string>> GetCandidateMarkdownUrlsAsync(DateTime targetDate)
    {
        var currentVersion = await ResolveCurrentVersionAsync();

        if (currentVersion.HasValue)
        {
            var urls = new List<string>
            {
                $"{RawGitHubBaseUrl}v1_{currentVersion.Value}.md",
                $"{RawGitHubBaseUrl}v1_{currentVersion.Value - 1}.md"
            };
            return urls.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        _logger.LogWarning("Could not resolve version from redirect, falling back to date-based URL calculation");
        return GetCandidateMarkdownUrlsByDate(targetDate);
    }

    /// <summary>
    /// Resolves the aka.ms redirect to extract the current version number.
    /// Caches the result for the lifetime of the service instance.
    /// </summary>
    private async Task<int?> ResolveCurrentVersionAsync()
    {
        if (_resolvedVersionNumber.HasValue)
        {
            return _resolvedVersionNumber;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, InsidersRedirectUrl);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

            var finalUrl = response.RequestMessage?.RequestUri?.ToString();
            if (string.IsNullOrEmpty(finalUrl))
            {
                _logger.LogWarning("aka.ms redirect did not produce a final URL");
                return null;
            }

            var match = VersionPattern().Match(finalUrl);
            if (!match.Success)
            {
                _logger.LogWarning("Could not extract version number from resolved URL: {Url}", finalUrl);
                return null;
            }

            _resolvedVersionNumber = int.Parse(match.Groups[1].Value);
            _logger.LogInformation("Resolved VS Code Insiders version from redirect: v1_{Version} ({Url})",
                _resolvedVersionNumber, finalUrl);
            return _resolvedVersionNumber;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve aka.ms redirect for VS Code version");
            return null;
        }
    }

    /// <summary>
    /// Fallback: calculates candidate markdown URLs from a hardcoded reference point.
    /// </summary>
    private static IReadOnlyList<string> GetCandidateMarkdownUrlsByDate(DateTime targetDate)
    {
        var releaseMonth = GetReleaseMonth(targetDate);
        var nextMonth = releaseMonth.AddMonths(1);

        var urls = new List<string>
        {
            GetMarkdownUrlForMonth(releaseMonth),
            GetMarkdownUrlForMonth(nextMonth)
        };

        return urls.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string GetMarkdownUrlForMonth(DateTime releaseMonth)
    {
        // Reference point: v1.109 = January 2026
        var referenceDate = new DateTime(2026, 1, 1);
        var referenceVersion = 109;

        var monthsDiff = ((releaseMonth.Year - referenceDate.Year) * 12) + releaseMonth.Month - referenceDate.Month;
        var version = referenceVersion + monthsDiff;

        return $"{RawGitHubBaseUrl}v1_{version}.md";
    }

    private static DateTime GetReleaseMonth(DateTime targetDate)
    {
        var firstThursday = GetFirstThursdayOfMonth(targetDate.Year, targetDate.Month);
        if (targetDate.Date < firstThursday.Date)
        {
            var previousMonth = targetDate.AddMonths(-1);
            return new DateTime(previousMonth.Year, previousMonth.Month, 1);
        }

        return new DateTime(targetDate.Year, targetDate.Month, 1);
    }

    private static DateTime GetFirstThursdayOfMonth(int year, int month)
    {
        var firstDay = new DateTime(year, month, 1);
        var offset = ((int)DayOfWeek.Thursday - (int)firstDay.DayOfWeek + 7) % 7;
        return firstDay.AddDays(offset);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static DateTime? ParseDateFromMatch(Match match, int defaultYear)
    {
        try
        {
            var month = match.Groups[1].Value;
            var day = int.Parse(match.Groups[2].Value);
            var year = match.Groups[3].Success ? int.Parse(match.Groups[3].Value) : defaultYear;
            var monthNumber = DateTime.ParseExact(month, "MMMM", System.Globalization.CultureInfo.InvariantCulture).Month;
            return new DateTime(year, monthNumber, day);
        }
        catch
        {
            return null;
        }
    }

    private static string TruncateTitle(string text)
    {
        var firstPeriod = text.IndexOf('.');
        if (firstPeriod > 0 && firstPeriod < MaxSentenceEndIndex)
        {
            return text[..firstPeriod];
        }
        
        return text.Length > MaxTitleLength ? text[..TruncatedTitleLength] + "..." : text;
    }

    private static string ExtractCategory(string headingText)
    {
        // "## February 11, 2026 - Chat improvements" → "Chat improvements"
        var dashIndex = headingText.IndexOf('-');
        if (dashIndex > 0 && dashIndex < headingText.Length - 2)
        {
            return headingText[(dashIndex + 1)..].Trim();
        }

        var dateMatch = DatePattern().Match(headingText);
        if (dateMatch.Success)
        {
            var startIndex = dateMatch.Index + dateMatch.Length;
            var remainder = headingText[startIndex..].Trim();
            if (!string.IsNullOrWhiteSpace(remainder))
            {
                return remainder.TrimStart('-', ':', ' ');
            }
        }

        return "General";
    }

    /// <summary>
    /// Internal representation of a date section while parsing markdown
    /// </summary>
    private sealed class MarkdownDateSection
    {
        public DateTime Date { get; init; }
        public List<VSCodeFeature> Features { get; } = [];
    }
}

/// <summary>
/// Represents VS Code Insiders release notes for a specific date
/// </summary>
public class VSCodeReleaseNotes
{
    private const string WebsiteBaseUrl = "https://code.visualstudio.com/updates/";

    public DateTime Date { get; set; }
    public required List<VSCodeFeature> Features { get; set; }
    public required string VersionUrl { get; set; }

    /// <summary>
    /// The human-readable website URL (e.g. https://code.visualstudio.com/updates/v1_110),
    /// derived from the raw GitHub VersionUrl.
    /// </summary>
    public string WebsiteUrl
    {
        get
        {
            var match = System.Text.RegularExpressions.Regex.Match(VersionUrl, @"(v1_\d+)");
            return match.Success
                ? $"{WebsiteBaseUrl}{match.Groups[1].Value}"
                : VersionUrl;
        }
    }
}

/// <summary>
/// Represents a single feature in the release notes
/// </summary>
public class VSCodeFeature
{
    public required string Title { get; set; }
    public required string Description { get; set; }
    public string? Category { get; set; }
    public string? Link { get; set; }
}
