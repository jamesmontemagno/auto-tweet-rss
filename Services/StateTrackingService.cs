using System.Text;
using System.Text.Json;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Logging;

namespace AutoTweetRss.Services;

public class StateTrackingService
{
    private const string StateFileName = "last-processed-id.txt";
    private static readonly JsonSerializerOptions StateJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
    
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

    public async Task<string?> GetLastProcessedIdAsync(string? stateFileName = null)
    {
        var fileName = stateFileName ?? StateFileName;
        
        try
        {
            await _containerClient.CreateIfNotExistsAsync();
            
            var blobClient = _containerClient.GetBlobClient(fileName);
            
            if (!await blobClient.ExistsAsync())
            {
                _logger.LogInformation("No previous state found for {FileName}", fileName);
                return null;
            }
            
            var response = await blobClient.DownloadContentAsync();
            var lastId = response.Value.Content.ToString();
            
            _logger.LogInformation("Last processed ID: {LastId} from {FileName}", lastId, fileName);
            return lastId;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading last processed ID from {FileName}", fileName);
            return null;
        }
    }

    public async Task<T?> GetStateAsync<T>(string stateFileName, Func<string, T?>? legacyStateFactory = null)
        where T : class
    {
        try
        {
            await _containerClient.CreateIfNotExistsAsync();

            var blobClient = _containerClient.GetBlobClient(stateFileName);
            if (!await blobClient.ExistsAsync())
            {
                _logger.LogInformation("No previous state found for {FileName}", stateFileName);
                return null;
            }

            var response = await blobClient.DownloadContentAsync();
            var rawState = response.Value.Content.ToString();

            if (string.IsNullOrWhiteSpace(rawState))
            {
                _logger.LogInformation("State file {FileName} is empty", stateFileName);
                return null;
            }

            try
            {
                var state = JsonSerializer.Deserialize<T>(rawState, StateJsonOptions);
                if (state != null)
                {
                    _logger.LogInformation("Loaded structured state from {FileName}", stateFileName);
                    return state;
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "State file {FileName} is not valid JSON. Attempting legacy parsing.", stateFileName);
            }

            if (legacyStateFactory == null)
            {
                return null;
            }

            var legacyState = legacyStateFactory(rawState);
            if (legacyState != null)
            {
                _logger.LogInformation("Loaded legacy state from {FileName}", stateFileName);
            }

            return legacyState;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading structured state from {FileName}", stateFileName);
            return null;
        }
    }

    public async Task SetLastProcessedIdAsync(string id, string? stateFileName = null)
    {
        var fileName = stateFileName ?? StateFileName;
        
        try
        {
            await _containerClient.CreateIfNotExistsAsync();
            
            var blobClient = _containerClient.GetBlobClient(fileName);
            
            using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(id));
            await blobClient.UploadAsync(stream, overwrite: true);
            
            _logger.LogInformation("Updated last processed ID to: {Id} in {FileName}", id, fileName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving last processed ID to {FileName}", fileName);
            throw;
        }
    }

    public async Task SetStateAsync<T>(T state, string stateFileName)
        where T : class
    {
        try
        {
            await _containerClient.CreateIfNotExistsAsync();

            var blobClient = _containerClient.GetBlobClient(stateFileName);
            var json = JsonSerializer.Serialize(state, StateJsonOptions);

            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
            await blobClient.UploadAsync(stream, overwrite: true);

            _logger.LogInformation("Updated structured state in {FileName}", stateFileName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving structured state to {FileName}", stateFileName);
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
