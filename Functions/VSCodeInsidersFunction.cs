using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using AutoTweetRss.Services;
using System.Net;
using System.Text.Json;

namespace AutoTweetRss.Functions;

public class VSCodeInsidersFunction
{
    private readonly ILogger<VSCodeInsidersFunction> _logger;
    private readonly VSCodeReleaseNotesService _releaseNotesService;

    public VSCodeInsidersFunction(
        ILogger<VSCodeInsidersFunction> logger,
        VSCodeReleaseNotesService releaseNotesService)
    {
        _logger = logger;
        _releaseNotesService = releaseNotesService;
    }

    /// <summary>
    /// Gets VS Code Insiders release notes for the current day.
    /// Returns an AI-powered summary if updates exist, otherwise returns empty response.
    /// </summary>
    /// <remarks>
    /// Optional query parameters:
    /// - date: Specific date in yyyy-MM-dd format (defaults to today), "full" for entire current release,
    ///         or "this week"/"this-week" for the last 7 days
    /// - format: Response format - "json" or "text" (defaults to "json")
    /// - forceRefresh: Set to "true" to bypass cache and generate new summary (defaults to "false")
    /// - aionly: Set to "true" to summarize only AI-related features (defaults to "false")
    /// - newline: "br" (default), "lf", "crlf", or "literal" to control summary newlines in JSON
    /// </remarks>
    [Function("VSCodeInsiders")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "vscode-insiders")] HttpRequestData req)
    {
        _logger.LogInformation("VSCodeInsiders function called at {Time}", DateTime.UtcNow);

        var response = req.CreateResponse();

        try
        {
            // Parse optional date parameter
            var dateParam = GetQueryParameter(req, "date");
            DateTime targetDate;
            bool isFullRelease = false;
            bool isThisWeek = false;
            DateTime rangeStart = DateTime.MinValue;
            DateTime rangeEnd = DateTime.MinValue;
            
            if (!string.IsNullOrEmpty(dateParam))
            {
                // Check if user wants full release notes
                if (dateParam.Equals("full", StringComparison.OrdinalIgnoreCase))
                {
                    isFullRelease = true;
                    targetDate = DateTime.UtcNow.Date; // Use today for cache key
                }
                else if (IsThisWeekParam(dateParam))
                {
                    isThisWeek = true;
                    rangeEnd = DateTime.UtcNow.Date;
                    rangeStart = rangeEnd.AddDays(-6); // Last 7 days, inclusive
                    targetDate = rangeEnd; // Use end date for cache key
                }
                else if (!DateTime.TryParseExact(dateParam, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out targetDate))
                {
                    response.StatusCode = HttpStatusCode.BadRequest;
                    await response.WriteStringAsync($"Invalid date format: {dateParam}. Use yyyy-MM-dd format, 'full', or 'this week'.");
                    return response;
                }
            }
            else
            {
                targetDate = DateTime.UtcNow.Date;
            }

            if (isThisWeek)
            {
                _logger.LogInformation("Fetching VS Code Insiders release notes for last 7 days: {StartDate} to {EndDate}",
                    rangeStart.ToString("yyyy-MM-dd"), rangeEnd.ToString("yyyy-MM-dd"));
            }
            else
            {
                _logger.LogInformation("Fetching VS Code Insiders release notes for {Mode}: {Date}",
                    isFullRelease ? "full release" : "date", targetDate.ToString("yyyy-MM-dd"));
            }

            // Fetch release notes
            VSCodeReleaseNotes? releaseNotes;
            if (isFullRelease)
            {
                releaseNotes = await _releaseNotesService.GetFullReleaseNotesAsync();
            }
            else if (isThisWeek)
            {
                releaseNotes = await _releaseNotesService.GetReleaseNotesForDateRangeAsync(rangeStart, rangeEnd);
            }
            else
            {
                releaseNotes = await _releaseNotesService.GetReleaseNotesForDateAsync(targetDate);
            }

            if (releaseNotes == null || releaseNotes.Features.Count == 0)
            {
                _logger.LogInformation("No release notes found for date: {Date}", targetDate.ToString("yyyy-MM-dd"));
                
                // Return empty response as per requirements
                response.StatusCode = HttpStatusCode.OK;
                response.Headers.Add("Content-Type", "application/json");
                
                var emptyResult = new VSCodeInsidersResponse
                {
                    Date = isThisWeek ? "this-week" : targetDate.ToString("yyyy-MM-dd"),
                    HasUpdates = false,
                    Summary = null,
                    Features = [],
                    VersionUrl = null
                };
                
                await response.WriteStringAsync(JsonSerializer.Serialize(emptyResult, JsonOptions));
                return response;
            }

            // Generate AI-powered summary
            var format = GetQueryParameter(req, "format")?.ToLowerInvariant() ?? "json";
            var forceRefreshParam = GetQueryParameter(req, "forceRefresh")?.ToLowerInvariant();
            var forceRefresh = forceRefreshParam == "true" || forceRefreshParam == "1";
            var aiOnlyParam = GetQueryParameter(req, "aionly")?.ToLowerInvariant();
            var aiOnly = aiOnlyParam == "true" || aiOnlyParam == "1";
            var newlineParam = GetQueryParameter(req, "newline")?.ToLowerInvariant() ?? "br";
            
            // Use different cache key format and much longer summary for full releases
            var cacheFormat = isFullRelease ? $"full-{format}" : isThisWeek ? $"week-{format}" : format;
            if (aiOnly)
            {
                cacheFormat = $"ai-{cacheFormat}";
            }
            var maxLength = isFullRelease ? 2000 : isThisWeek ? 900 : 500; // Rich summary for full releases
            var summary = await _releaseNotesService.GenerateSummaryAsync(
                releaseNotes,
                maxLength: maxLength,
                format: cacheFormat,
                forceRefresh: forceRefresh,
                aiOnly: aiOnly,
                isThisWeek: isThisWeek);

            summary = NormalizeSummaryNewlines(summary, newlineParam);

            var featuresForResponse = isThisWeek
                ? releaseNotes.Features.Take(8).ToList()
                : releaseNotes.Features;

            var displayNotes = isThisWeek
                ? new VSCodeReleaseNotes
                {
                    Date = releaseNotes.Date,
                    Features = featuresForResponse,
                    VersionUrl = releaseNotes.VersionUrl
                }
                : releaseNotes;

            // Determine response format

            if (format == "text")
            {
                response.StatusCode = HttpStatusCode.OK;
                response.Headers.Add("Content-Type", "text/plain; charset=utf-8");
                
                var textOutput = BuildTextResponse(displayNotes, summary);
                await response.WriteStringAsync(textOutput);
            }
            else
            {
                response.StatusCode = HttpStatusCode.OK;
                response.Headers.Add("Content-Type", "application/json");
                
                var jsonResult = new VSCodeInsidersResponse
                {
                    Date = isFullRelease ? "full" : isThisWeek ? "this-week" : targetDate.ToString("yyyy-MM-dd"),
                    HasUpdates = true,
                    Summary = summary,
                    Features = featuresForResponse.Select(f => new FeatureResponse
                    {
                        Title = f.Title,
                        Description = f.Description,
                        Category = f.Category,
                        Link = f.Link
                    }).ToList(),
                    VersionUrl = releaseNotes.VersionUrl
                };
                
                await response.WriteStringAsync(JsonSerializer.Serialize(jsonResult, JsonOptions));
            }

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in VSCodeInsiders function");
            response.StatusCode = HttpStatusCode.InternalServerError;
            await response.WriteStringAsync("An error occurred while processing your request.");
            return response;
        }
    }

    private static string? GetQueryParameter(HttpRequestData req, string name)
    {
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        return query[name];
    }

    private static bool IsThisWeekParam(string dateParam)
    {
        return dateParam.Equals("this week", StringComparison.OrdinalIgnoreCase) ||
               dateParam.Equals("this-week", StringComparison.OrdinalIgnoreCase) ||
               dateParam.Equals("week", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeSummaryNewlines(string summary, string newlineParam)
    {
        if (string.IsNullOrEmpty(summary))
        {
            return summary;
        }

        var normalized = summary.Replace("\r\n", "\n", StringComparison.Ordinal);
        return newlineParam switch
        {
            "lf" => normalized,
            "crlf" => normalized.Replace("\n", "\r\n", StringComparison.Ordinal),
            "literal" => normalized.Replace("\n", "\\n", StringComparison.Ordinal),
            _ => normalized.Replace("\n", "<br>", StringComparison.Ordinal)
        };
    }

    private static string BuildTextResponse(VSCodeReleaseNotes notes, string summary)
    {
        var builder = new System.Text.StringBuilder();
        
        builder.AppendLine($"VS Code Insiders - {notes.Date:MMMM d, yyyy}");
        builder.AppendLine(new string('═', 50));
        builder.AppendLine();
        builder.AppendLine("SUMMARY");
        builder.AppendLine(new string('-', 50));
        builder.AppendLine(summary);
        builder.AppendLine();
        builder.AppendLine("FEATURES");
        builder.AppendLine(new string('-', 50));
        
        foreach (var feature in notes.Features)
        {
            var category = !string.IsNullOrEmpty(feature.Category) ? $"[{feature.Category}] " : "";
            builder.AppendLine($"• {category}{feature.Title}");
            
            if (feature.Description != feature.Title)
            {
                builder.AppendLine($"  {feature.Description}");
            }
            
            if (!string.IsNullOrEmpty(feature.Link))
            {
                builder.AppendLine($"  Link: {feature.Link}");
            }
            
            builder.AppendLine();
        }
        
        builder.AppendLine(new string('═', 50));
        builder.AppendLine($"Full release notes: {notes.VersionUrl}");
        
        return builder.ToString();
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
}

/// <summary>
/// Response model for VS Code Insiders endpoint
/// </summary>
public class VSCodeInsidersResponse
{
    public required string Date { get; set; }
    public bool HasUpdates { get; set; }
    public string? Summary { get; set; }
    public required List<FeatureResponse> Features { get; set; }
    public string? VersionUrl { get; set; }
}

/// <summary>
/// Feature response model
/// </summary>
public class FeatureResponse
{
    public required string Title { get; set; }
    public required string Description { get; set; }
    public string? Category { get; set; }
    public string? Link { get; set; }
}
