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
    
    // Base URL for VS Code updates - version changes monthly
    private const string BaseUpdateUrl = "https://code.visualstudio.com/updates/";
    
    // Compiled regex for extracting date from headings like "January 26, 2026" or "January 26"
    [GeneratedRegex(@"(January|February|March|April|May|June|July|August|September|October|November|December)\s+(\d{1,2})(?:,\s*(\d{4}))?", RegexOptions.IgnoreCase)]
    private static partial Regex DatePattern();

    public VSCodeReleaseNotesService(
        ILogger<VSCodeReleaseNotesService> logger,
        IHttpClientFactory httpClientFactory,
        ReleaseSummarizerService? releaseSummarizer = null)
    {
        _logger = logger;
        _httpClient = httpClientFactory.CreateClient();
        _releaseSummarizer = releaseSummarizer;
    }

    /// <summary>
    /// Gets the current VS Code Insiders release notes for the current day
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
        try
        {
            var versionUrl = GetVersionUrl(targetDate);
            _logger.LogInformation("Fetching VS Code Insiders release notes from {Url} for date {Date}", 
                versionUrl, targetDate.ToString("yyyy-MM-dd"));

            var html = await _httpClient.GetStringAsync(versionUrl);
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            // Find all date-based sections in the page
            var features = ParseFeaturesForDate(doc, targetDate);

            if (features.Count == 0)
            {
                _logger.LogInformation("No release notes found for date {Date}", targetDate.ToString("yyyy-MM-dd"));
                return null;
            }

            _logger.LogInformation("Found {Count} features for date {Date}", features.Count, targetDate.ToString("yyyy-MM-dd"));

            return new VSCodeReleaseNotes
            {
                Date = targetDate,
                Features = features,
                VersionUrl = versionUrl
            };
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Failed to fetch VS Code release notes - page may not exist yet");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching VS Code Insiders release notes");
            return null;
        }
    }

    /// <summary>
    /// Generates an AI-powered summary of the release notes
    /// </summary>
    /// <param name="notes">The release notes to summarize</param>
    /// <param name="maxLength">Maximum summary length in characters</param>
    /// <returns>AI-generated summary</returns>
    public async Task<string> GenerateSummaryAsync(VSCodeReleaseNotes notes, int maxLength = 500)
    {
        if (_releaseSummarizer == null)
        {
            return GenerateFallbackSummary(notes);
        }

        try
        {
            // Build content string from features
            var content = string.Join("\n", notes.Features.Select(f => $"- {f.Title}: {f.Description}"));
            
            var summary = await _releaseSummarizer.SummarizeReleaseAsync(
                $"VS Code Insiders {notes.Date:MMMM d, yyyy}", 
                content, 
                maxLength, 
                feedType: "vscode");

            return summary;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AI summarization failed, using fallback");
            return GenerateFallbackSummary(notes);
        }
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
    /// Gets the URL for the current version based on the target date
    /// VS Code uses version numbers like v1_109 for January 2026
    /// </summary>
    private static string GetVersionUrl(DateTime targetDate)
    {
        // Calculate version number based on date
        // VS Code versioning: year.month starting from some base
        // v1.109 is January 2026, so we calculate based on that
        // The pattern is: v1_{baseVersion + monthsFromBase}
        
        // Reference point: v1.109 = January 2026
        var referenceDate = new DateTime(2026, 1, 1);
        var referenceVersion = 109;
        
        var monthsDiff = ((targetDate.Year - referenceDate.Year) * 12) + targetDate.Month - referenceDate.Month;
        var version = referenceVersion + monthsDiff;
        
        return $"{BaseUpdateUrl}v1_{version}";
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
                if (!string.IsNullOrWhiteSpace(text) && text.Length > 20)
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
        
        if (string.IsNullOrWhiteSpace(text) || text.Length < 5)
        {
            return null;
        }
        
        // Extract link if present
        var linkNode = li.SelectSingleNode(".//a");
        var link = linkNode?.GetAttributeValue("href", null);
        
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
        if (firstPeriod > 0 && firstPeriod < 100)
        {
            return text[..firstPeriod];
        }
        
        return text.Length > 80 ? text[..77] + "..." : text;
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
