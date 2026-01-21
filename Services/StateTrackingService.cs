using Azure.Storage.Blobs;
using Microsoft.Extensions.Logging;

namespace AutoTweetRss.Services;

public class StateTrackingService
{
    private const string StateFileName = "last-processed-id.txt";
    
    private readonly ILogger<StateTrackingService> _logger;
    private readonly BlobContainerClient _containerClient;

    public StateTrackingService(ILogger<StateTrackingService> logger)
    {
        _logger = logger;
        
        var connectionString = Environment.GetEnvironmentVariable("AZURE_STORAGE_CONNECTION_STRING")
            ?? throw new InvalidOperationException("AZURE_STORAGE_CONNECTION_STRING not configured");
        var containerName = Environment.GetEnvironmentVariable("STATE_CONTAINER_NAME") ?? "release-state";
        
        var blobServiceClient = new BlobServiceClient(connectionString);
        _containerClient = blobServiceClient.GetBlobContainerClient(containerName);
    }

    public async Task<string?> GetLastProcessedIdAsync()
    {
        try
        {
            await _containerClient.CreateIfNotExistsAsync();
            
            var blobClient = _containerClient.GetBlobClient(StateFileName);
            
            if (!await blobClient.ExistsAsync())
            {
                _logger.LogInformation("No previous state found");
                return null;
            }
            
            var response = await blobClient.DownloadContentAsync();
            var lastId = response.Value.Content.ToString();
            
            _logger.LogInformation("Last processed ID: {LastId}", lastId);
            return lastId;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading last processed ID");
            return null;
        }
    }

    public async Task SetLastProcessedIdAsync(string id)
    {
        try
        {
            await _containerClient.CreateIfNotExistsAsync();
            
            var blobClient = _containerClient.GetBlobClient(StateFileName);
            
            using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(id));
            await blobClient.UploadAsync(stream, overwrite: true);
            
            _logger.LogInformation("Updated last processed ID to: {Id}", id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving last processed ID");
            throw;
        }
    }

    public bool IsNewEntry(string entryId, string? lastProcessedId)
    {
        if (string.IsNullOrEmpty(lastProcessedId))
        {
            return true;
        }
        
        // Entry IDs are in format: tag:github.com,2008:Repository/585860664/v0.0.388
        // Compare the version parts
        return !string.Equals(entryId, lastProcessedId, StringComparison.OrdinalIgnoreCase);
    }
}
