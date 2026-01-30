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
    /// - date: Specific date in yyyy-MM-dd format (defaults to today)
    /// - format: Response format - "json" or "text" (defaults to "json")
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
            
            if (!string.IsNullOrEmpty(dateParam))
            {
                if (!DateTime.TryParseExact(dateParam, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out targetDate))
                {
                    response.StatusCode = HttpStatusCode.BadRequest;
                    await response.WriteStringAsync($"Invalid date format: {dateParam}. Use yyyy-MM-dd format.");
                    return response;
                }
            }
            else
            {
                targetDate = DateTime.UtcNow.Date;
            }

            _logger.LogInformation("Fetching VS Code Insiders release notes for date: {Date}", targetDate.ToString("yyyy-MM-dd"));

            // Fetch release notes for the date
            var releaseNotes = await _releaseNotesService.GetReleaseNotesForDateAsync(targetDate);

            if (releaseNotes == null || releaseNotes.Features.Count == 0)
            {
                _logger.LogInformation("No release notes found for date: {Date}", targetDate.ToString("yyyy-MM-dd"));
                
                // Return empty response as per requirements
                response.StatusCode = HttpStatusCode.OK;
                response.Headers.Add("Content-Type", "application/json");
                
                var emptyResult = new VSCodeInsidersResponse
                {
                    Date = targetDate.ToString("yyyy-MM-dd"),
                    HasUpdates = false,
                    Summary = null,
                    Features = [],
                    VersionUrl = null
                };
                
                await response.WriteStringAsync(JsonSerializer.Serialize(emptyResult, JsonOptions));
                return response;
            }

            // Generate AI-powered summary
            var summary = await _releaseNotesService.GenerateSummaryAsync(releaseNotes);

            // Determine response format
            var format = GetQueryParameter(req, "format")?.ToLowerInvariant() ?? "json";

            if (format == "text")
            {
                response.StatusCode = HttpStatusCode.OK;
                response.Headers.Add("Content-Type", "text/plain; charset=utf-8");
                
                var textOutput = BuildTextResponse(releaseNotes, summary);
                await response.WriteStringAsync(textOutput);
            }
            else
            {
                response.StatusCode = HttpStatusCode.OK;
                response.Headers.Add("Content-Type", "application/json");
                
                var jsonResult = new VSCodeInsidersResponse
                {
                    Date = targetDate.ToString("yyyy-MM-dd"),
                    HasUpdates = true,
                    Summary = summary,
                    Features = releaseNotes.Features.Select(f => new FeatureResponse
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
