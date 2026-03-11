using Azure.Storage.Blobs;
using Microsoft.Extensions.Logging;

namespace AutoTweetRss.Services;

/// <summary>
/// Service for caching VS Code release notes summaries in Azure Blob Storage
/// </summary>
public class VSCodeSummaryCacheService
{
    private readonly ILogger<VSCodeSummaryCacheService> _logger;
    private readonly BlobContainerClient _containerClient;

    public VSCodeSummaryCacheService(ILogger<VSCodeSummaryCacheService> logger)
    {
        _logger = logger;
        
        var connectionString = Environment.GetEnvironmentVariable("AZURE_STORAGE_CONNECTION_STRING")
            ?? throw new InvalidOperationException("AZURE_STORAGE_CONNECTION_STRING not configured");
        var containerName = Environment.GetEnvironmentVariable("VSCODE_CACHE_CONTAINER_NAME") ?? "vscode-cache";
        
        var blobServiceClient = new BlobServiceClient(connectionString);
        _containerClient = blobServiceClient.GetBlobContainerClient(containerName);
    }

    /// <summary>
    /// Gets a cached summary for a specific date and format
    /// </summary>
    /// <param name="date">The release date</param>
    /// <param name="format">The format identifier (e.g., "text", "json")</param>
    /// <returns>Cached summary if found, null otherwise</returns>
    public async Task<string?> GetCachedSummaryAsync(DateTime date, string format = "default")
    {
        var fileName = GetCacheFileName(date, format);
        
        try
        {
            await _containerClient.CreateIfNotExistsAsync();
            
            var blobClient = _containerClient.GetBlobClient(fileName);
            
            if (!await blobClient.ExistsAsync())
            {
                _logger.LogInformation("No cached summary found for {Date} with format {Format}", 
                    date.ToString("yyyy-MM-dd"), format);
                return null;
            }
            
            var response = await blobClient.DownloadContentAsync();
            var cachedSummary = response.Value.Content.ToString();
            
            _logger.LogInformation("Retrieved cached summary for {Date} with format {Format}", 
                date.ToString("yyyy-MM-dd"), format);
            return cachedSummary;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading cached summary for {Date} with format {Format}", 
                date.ToString("yyyy-MM-dd"), format);
            return null;
        }
    }

    /// <summary>
    /// Saves a summary to the cache
    /// </summary>
    /// <param name="date">The release date</param>
    /// <param name="summary">The summary to cache</param>
    /// <param name="format">The format identifier (e.g., "text", "json")</param>
    public async Task SetCachedSummaryAsync(DateTime date, string summary, string format = "default")
    {
        var fileName = GetCacheFileName(date, format);
        
        try
        {
            await _containerClient.CreateIfNotExistsAsync();
            
            var blobClient = _containerClient.GetBlobClient(fileName);
            
            using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(summary));
            await blobClient.UploadAsync(stream, overwrite: true);
            
            _logger.LogInformation("Cached summary for {Date} with format {Format}", 
                date.ToString("yyyy-MM-dd"), format);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error caching summary for {Date} with format {Format}", 
                date.ToString("yyyy-MM-dd"), format);
            // Don't throw - caching is not critical
        }
    }

    /// <summary>
    /// Generates a cache file name based on date and format
    /// </summary>
    private static string GetCacheFileName(DateTime date, string format)
    {
        var datePart = date.ToString("yyyy-MM-dd");
        var sanitizedFormat = string.IsNullOrWhiteSpace(format) ? "default" : format.ToLowerInvariant();
        return $"summary-{datePart}-{sanitizedFormat}.txt";
    }
}
