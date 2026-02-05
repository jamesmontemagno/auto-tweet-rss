using HtmlAgilityPack;
using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;

namespace AutoTweetRss.Services;

/// <summary>
/// Service for fetching and parsing VS Code Insiders release notes
/// </summary>
public partial class VSCodeReleaseNotesService
{
    private readonly ILogger<VSCodeReleaseNotesService> _logger;
    private readonly HttpClient _httpClient;
    private readonly ReleaseSummarizerService? _releaseSummarizer;
    private readonly VSCodeSummaryCacheService? _cacheService;
    
    // Base URL for VS Code updates - version changes monthly
    private const string BaseUpdateUrl = "https://code.visualstudio.com/updates/";
    
    // Constants for text length thresholds
    private const int MinListItemLength = 5;
    private const int MinFeatureTextLength = 20;
    private const int MaxTitleLength = 80;
    private const int MaxSentenceEndIndex = 100;
    private const int TruncatedTitleLength = 77;
    
    // Compiled regex for extracting date from headings like "January 26, 2026" or "January 26"
    [GeneratedRegex(@"(January|February|March|April|May|June|July|August|September|October|November|December)\s+(\d{1,2})(?:,\s*(\d{4}))?", RegexOptions.IgnoreCase)]
    private static partial Regex DatePattern();

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
    /// <returns>Release notes if updates exist for today, null otherwise</returns>
    public async Task<VSCodeReleaseNotes?> GetTodayReleaseNotesAsync()
    {
        var today = DateTime.UtcNow.Date;
        return await GetReleaseNotesForDateAsync(today);
    }

    /// <summary>
    /// Gets the VS Code Insiders release notes for a specific date
    /// </summary>
    /// <param name="targetDate">The date to fetch release notes for</param>
    /// <returns>Release notes if updates exist for the date, null otherwise</returns>
    public async Task<VSCodeReleaseNotes?> GetReleaseNotesForDateAsync(DateTime targetDate)
    {
        foreach (var versionUrl in GetCandidateVersionUrls(targetDate))
        {
            try
            {
                _logger.LogInformation("Fetching VS Code Insiders release notes from {Url} for date {Date}",
                    versionUrl, targetDate.ToString("yyyy-MM-dd"));

                var html = await _httpClient.GetStringAsync(versionUrl);
                var doc = new HtmlDocument();
                doc.LoadHtml(html);

                // Find all date-based sections in the page
                var features = ParseFeaturesForDate(doc, targetDate);

                if (features.Count == 0)
                {
                    _logger.LogInformation("No release notes found for date {Date} at {Url}",
                        targetDate.ToString("yyyy-MM-dd"), versionUrl);
                    continue;
                }

                _logger.LogInformation("Found {Count} features for date {Date} at {Url}",
                    features.Count, targetDate.ToString("yyyy-MM-dd"), versionUrl);

                return new VSCodeReleaseNotes
                {
                    Date = targetDate,
                    Features = features,
                    VersionUrl = versionUrl
                };
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, "Failed to fetch VS Code release notes from {Url}", versionUrl);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching VS Code Insiders release notes from {Url}", versionUrl);
            }
        }

        return null;
    }

    /// <summary>
    /// Gets VS Code Insiders release notes for a date range (inclusive)
    /// </summary>
    /// <param name="startDate">Start date for the range</param>
    /// <param name="endDate">End date for the range</param>
    /// <returns>Release notes if updates exist for the range, null otherwise</returns>
    public async Task<VSCodeReleaseNotes?> GetReleaseNotesForDateRangeAsync(DateTime startDate, DateTime endDate)
    {
        if (startDate > endDate)
        {
            (startDate, endDate) = (endDate, startDate);
        }

        var candidateUrls = GetCandidateVersionUrls(endDate)
            .Concat(GetCandidateVersionUrls(startDate))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var allFeatures = new List<VSCodeFeature>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? versionUrlForResponse = null;

        foreach (var versionUrl in candidateUrls)
        {
            try
            {
                _logger.LogInformation("Fetching VS Code Insiders release notes from {Url} for range {StartDate} to {EndDate}",
                    versionUrl, startDate.ToString("yyyy-MM-dd"), endDate.ToString("yyyy-MM-dd"));

                var html = await _httpClient.GetStringAsync(versionUrl);
                var doc = new HtmlDocument();
                doc.LoadHtml(html);

                var features = ParseFeaturesForDateRange(doc, startDate, endDate);
                if (features.Count == 0)
                {
                    _logger.LogInformation("No release notes found for range {StartDate} to {EndDate} at {Url}",
                        startDate.ToString("yyyy-MM-dd"), endDate.ToString("yyyy-MM-dd"), versionUrl);
                    continue;
                }

                _logger.LogInformation("Found {Count} features for range {StartDate} to {EndDate} at {Url}",
                    features.Count, startDate.ToString("yyyy-MM-dd"), endDate.ToString("yyyy-MM-dd"), versionUrl);

                if (versionUrlForResponse == null)
                {
                    versionUrlForResponse = versionUrl;
                }

                foreach (var feature in features)
                {
                    var key = $"{feature.Title}|{feature.Description}|{feature.Category}";
                    if (seen.Add(key))
                    {
                        allFeatures.Add(feature);
                    }
                }
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, "Failed to fetch VS Code release notes from {Url}", versionUrl);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching VS Code Insiders release notes from {Url}", versionUrl);
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
    /// <returns>All release notes from the current version page</returns>
    public async Task<VSCodeReleaseNotes?> GetFullReleaseNotesAsync()
    {
        var today = DateTime.UtcNow.Date;
        
        foreach (var versionUrl in GetCandidateVersionUrls(today))
        {
            try
            {
                _logger.LogInformation("Fetching full VS Code Insiders release notes from {Url}", versionUrl);

                var html = await _httpClient.GetStringAsync(versionUrl);
                var doc = new HtmlDocument();
                doc.LoadHtml(html);

                // Parse all features without date filtering
                var features = ParseAllFeatures(doc);

                if (features.Count == 0)
                {
                    _logger.LogInformation("No release notes found at {Url}", versionUrl);
                    continue;
                }

                _logger.LogInformation("Found {Count} total features at {Url}", features.Count, versionUrl);

                // Use today's date as the reference date for the full release
                return new VSCodeReleaseNotes
                {
                    Date = today,
                    Features = features,
                    VersionUrl = versionUrl
                };
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, "Failed to fetch VS Code release notes from {Url}", versionUrl);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching VS Code Insiders release notes from {Url}", versionUrl);
            }
        }

        return null;
    }

    /// <summary>
    /// Generates an AI-powered summary of the release notes
    /// </summary>
    /// <param name="notes">The release notes to summarize</param>
    /// <param name="maxLength">Maximum summary length in characters</param>
    /// <param name="format">Format identifier for caching (e.g., "text", "json")</param>
    /// <param name="forceRefresh">If true, bypasses cache and generates a new summary</param>
    /// <param name="aiOnly">If true, summarize only AI-related features</param>
    /// <param name="isThisWeek">If true, use the weekly prompt variant</param>
    /// <returns>AI-generated summary</returns>
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
                    : "vscode";

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

    /// <summary>
    /// Gets candidate URLs for the release notes based on the target date.
    /// VS Code releases on the first Thursday of the month, and the version page
    /// changes then; also try the next version in case notes appear early.
    /// </summary>
    private static IEnumerable<string> GetCandidateVersionUrls(DateTime targetDate)
    {
        var releaseMonth = GetReleaseMonth(targetDate);
        var nextMonth = releaseMonth.AddMonths(1);

        var urls = new List<string>
        {
            GetVersionUrlForMonth(releaseMonth),
            GetVersionUrlForMonth(nextMonth)
        };

        return urls.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Gets the URL for the version page based on a release month.
    /// VS Code uses version numbers like v1_109 for January 2026.
    /// </summary>
    private static string GetVersionUrlForMonth(DateTime releaseMonth)
    {
        // Reference point: v1.109 = January 2026
        var referenceDate = new DateTime(2026, 1, 1);
        var referenceVersion = 109;

        var monthsDiff = ((releaseMonth.Year - referenceDate.Year) * 12) + releaseMonth.Month - referenceDate.Month;
        var version = referenceVersion + monthsDiff;

        return $"{BaseUpdateUrl}v1_{version}";
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

    /// <summary>
    /// Parses all features from the HTML document without date filtering
    /// </summary>
    private List<VSCodeFeature> ParseAllFeatures(HtmlDocument doc)
    {
        var features = new List<VSCodeFeature>();
        
        // Look for all headings that might contain features
        var headings = doc.DocumentNode.SelectNodes("//h2 | //h3 | //h4");
        if (headings == null) return features;

        foreach (var heading in headings)
        {
            var headingText = heading.InnerText.Trim();
            var category = headingText;
            
            // Extract features from this section
            var sectionFeatures = ExtractFeaturesFromSection(heading);
            features.AddRange(sectionFeatures);
        }

        return features;
    }

    /// <summary>
    /// Parses features from the HTML document for a specific date
    /// </summary>
    private List<VSCodeFeature> ParseFeaturesForDate(HtmlDocument doc, DateTime targetDate)
    {
        var features = new List<VSCodeFeature>();
        
        // VS Code Insiders release notes use h2/h3 headings with dates and list items for features
        // We need to find sections that match the target date
        
        // Look for date headings in the document
        var headings = doc.DocumentNode.SelectNodes("//h2 | //h3 | //h4");
        if (headings == null) return features;

        foreach (var heading in headings)
        {
            var headingText = heading.InnerText.Trim();
            var dateMatch = DatePattern().Match(headingText);
            
            if (!dateMatch.Success) continue;
            
            var parsedDate = ParseDateFromMatch(dateMatch, targetDate.Year);
            
            // Check if this section matches our target date
            if (parsedDate?.Date != targetDate.Date) continue;
            
            // Found a matching date section - extract features from the following content
            var sectionFeatures = ExtractFeaturesFromSection(heading);
            features.AddRange(sectionFeatures);
        }

        return features;
    }

    /// <summary>
    /// Parses features from the HTML document for a date range (inclusive)
    /// </summary>
    private List<VSCodeFeature> ParseFeaturesForDateRange(HtmlDocument doc, DateTime startDate, DateTime endDate)
    {
        var features = new List<VSCodeFeature>();

        var headings = doc.DocumentNode.SelectNodes("//h2 | //h3 | //h4");
        if (headings == null) return features;

        foreach (var heading in headings)
        {
            var headingText = heading.InnerText.Trim();
            var dateMatch = DatePattern().Match(headingText);

            if (!dateMatch.Success) continue;

            var parsedDate = ParseDateFromMatch(dateMatch, endDate.Year);
            if (parsedDate == null) continue;

            if (parsedDate.Value.Date < startDate.Date || parsedDate.Value.Date > endDate.Date)
            {
                continue;
            }

            var sectionFeatures = ExtractFeaturesFromSection(heading);
            features.AddRange(sectionFeatures);
        }

        return features;
    }

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

    private List<VSCodeFeature> ExtractFeaturesFromSection(HtmlNode headingNode)
    {
        var features = new List<VSCodeFeature>();
        
        // Get the current category from the heading text (might be like "January 26, 2026 - Chat improvements")
        var headingText = headingNode.InnerText.Trim();
        var category = ExtractCategory(headingText);
        
        // Traverse siblings until we hit the next heading
        var sibling = headingNode.NextSibling;
        
        while (sibling != null)
        {
            // Stop if we hit another date heading
            if (sibling.Name.Equals("h2", StringComparison.OrdinalIgnoreCase) ||
                sibling.Name.Equals("h3", StringComparison.OrdinalIgnoreCase) ||
                sibling.Name.Equals("h4", StringComparison.OrdinalIgnoreCase))
            {
                // Check if this is a new date section
                if (DatePattern().IsMatch(sibling.InnerText))
                {
                    break;
                }
                // Update category for sub-sections
                category = sibling.InnerText.Trim();
            }
            
            // Extract list items as features
            if (sibling.Name.Equals("ul", StringComparison.OrdinalIgnoreCase) ||
                sibling.Name.Equals("ol", StringComparison.OrdinalIgnoreCase))
            {
                var listItems = sibling.SelectNodes(".//li");
                if (listItems != null)
                {
                    foreach (var li in listItems)
                    {
                        var feature = ParseFeatureFromListItem(li, category);
                        if (feature != null)
                        {
                            features.Add(feature);
                        }
                    }
                }
            }
            
            // Also check for paragraphs that might contain feature descriptions
            if (sibling.Name.Equals("p", StringComparison.OrdinalIgnoreCase))
            {
                var text = HtmlEntity.DeEntitize(sibling.InnerText).Trim();
                if (!string.IsNullOrWhiteSpace(text) && text.Length > MinFeatureTextLength)
                {
                    features.Add(new VSCodeFeature
                    {
                        Title = TruncateTitle(text),
                        Description = text,
                        Category = category
                    });
                }
            }
            
            sibling = sibling.NextSibling;
        }
        
        return features;
    }

    private static VSCodeFeature? ParseFeatureFromListItem(HtmlNode li, string category)
    {
        var text = HtmlEntity.DeEntitize(li.InnerText).Trim();
        
        if (string.IsNullOrWhiteSpace(text) || text.Length < MinListItemLength)
        {
            return null;
        }
        
        // Extract link if present and convert relative URLs to absolute
        var linkNode = li.SelectSingleNode(".//a");
        var link = linkNode?.GetAttributeValue("href", null);
        if (link != null && link.StartsWith('/'))
        {
            link = "https://code.visualstudio.com" + link;
        }
        
        // Try to extract title from bold/strong text or use the first part
        var strongNode = li.SelectSingleNode(".//strong | .//b");
        var title = strongNode != null 
            ? HtmlEntity.DeEntitize(strongNode.InnerText).Trim() 
            : TruncateTitle(text);
        
        return new VSCodeFeature
        {
            Title = title,
            Description = text,
            Category = category,
            Link = link
        };
    }

    private static string TruncateTitle(string text)
    {
        // Get first sentence or first N characters
        var firstPeriod = text.IndexOf('.');
        if (firstPeriod > 0 && firstPeriod < MaxSentenceEndIndex)
        {
            return text[..firstPeriod];
        }
        
        return text.Length > MaxTitleLength ? text[..TruncatedTitleLength] + "..." : text;
    }

    private static string ExtractCategory(string headingText)
    {
        // Try to extract category after the date, e.g., "January 26 - Chat" -> "Chat"
        var dashIndex = headingText.IndexOf('-');
        if (dashIndex > 0 && dashIndex < headingText.Length - 2)
        {
            return headingText[(dashIndex + 1)..].Trim();
        }
        
        // Remove date portion and return remainder
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
}

/// <summary>
/// Represents VS Code Insiders release notes for a specific date
/// </summary>
public class VSCodeReleaseNotes
{
    public DateTime Date { get; set; }
    public required List<VSCodeFeature> Features { get; set; }
    public required string VersionUrl { get; set; }
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
