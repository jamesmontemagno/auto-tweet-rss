using System.Net;
using System.Text.Json;
using System.IO;
using AutoTweetRss.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutoTweetRss.Functions;

public class GitHubChangelogCopilotLookupFunction
{
    private readonly ILogger<GitHubChangelogCopilotLookupFunction> _logger;
    private readonly GitHubChangelogFeedService _feedService;

    public GitHubChangelogCopilotLookupFunction(
        ILogger<GitHubChangelogCopilotLookupFunction> logger,
        GitHubChangelogFeedService feedService)
    {
        _logger = logger;
        _feedService = feedService;
    }

    [Function("GitHubChangelogCopilotLookup")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "github-changelog/copilot")] HttpRequestData req,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("GitHubChangelogCopilotLookup called at {Time}", DateTime.UtcNow);

        var response = req.CreateResponse();

        try
        {
            var body = await new StreamReader(req.Body).ReadToEndAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(body))
            {
                response.StatusCode = HttpStatusCode.BadRequest;
                await response.WriteStringAsync("Missing request body.");
                return response;
            }

            var request = JsonSerializer.Deserialize<GitHubChangelogLookupRequest>(body, JsonOptions);
            if (request == null || string.IsNullOrWhiteSpace(request.Url))
            {
                response.StatusCode = HttpStatusCode.BadRequest;
                await response.WriteStringAsync("Missing required field: url.");
                return response;
            }

            if (!Uri.TryCreate(request.Url, UriKind.Absolute, out _))
            {
                response.StatusCode = HttpStatusCode.BadRequest;
                await response.WriteStringAsync("Invalid url. Provide an absolute URL.");
                return response;
            }

            var description = await _feedService.FindCopilotDescriptionForUrlAsync(request.Url, cancellationToken);

            response.StatusCode = HttpStatusCode.OK;
            response.Headers.Add("Content-Type", "text/plain; charset=utf-8");
            await response.WriteStringAsync(description ?? string.Empty, cancellationToken);
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in GitHubChangelogCopilotLookup");
            response.StatusCode = HttpStatusCode.InternalServerError;
            await response.WriteStringAsync("An error occurred while processing your request.");
            return response;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
}

public class GitHubChangelogLookupRequest
{
    public string? Url { get; set; }
}
