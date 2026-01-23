# Auto Tweet RSS

An Azure Function that monitors GitHub Copilot releases RSS feeds and automatically tweets about new stable releases.

## Features

- Monitors both GitHub Copilot CLI and Copilot SDK Atom feeds
- Polls feeds every 15 minutes
- Filters out pre-release versions and submodule releases
- **AI-powered summaries**: Uses Microsoft.Extensions.AI with Azure OpenAI to generate concise, emoji-enhanced summaries of release notes
- Formats tweets with emoji-enhanced bullet points for features
- Posts to Twitter/X using OAuth 1.0a authentication
- Tracks state in Azure Blob Storage to prevent duplicate tweets (separate state for CLI and SDK)
- Respects Twitter's 280 character limit with smart truncation

## Tweet Formats

### Copilot CLI

```
🚀 Copilot CLI v0.0.388 released!

✨ Add /review command for code reviews
⚡ Improved response time
🐛 Fixed memory leak

https://github.com/github/copilot-cli/releases/tag/v0.0.388

#GitHubCopilotCLI
```

### Copilot SDK

```
🚀 Copilot SDK v0.1.16 released!

✨ Adding FAQ section to the README
⚡ Make the .NET library NativeAOT compatible
🐛 Fix code formatting

https://github.com/github/copilot-sdk/releases/tag/v0.1.16

#GitHubCopilotSDK
```

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Azure Functions Core Tools v4](https://docs.microsoft.com/azure/azure-functions/functions-run-local)
- [Azurite](https://docs.microsoft.com/azure/storage/common/storage-use-azurite) (for local development) or an Azure Storage account
- Twitter Developer Account with OAuth 1.0a credentials

## Configuration

### local.settings.json

Create a `local.settings.json` file in the project root (this file is git-ignored):

```json
{
  "IsEncrypted": false,
  "Values": {
    "AzureWebJobsStorage": "UseDevelopmentStorage=true",
    "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated",
    
    "TWITTER_API_KEY": "<your-consumer-key>",
    "TWITTER_API_SECRET": "<your-consumer-secret>",
    "TWITTER_ACCESS_TOKEN": "<your-access-token>",
    "TWITTER_ACCESS_TOKEN_SECRET": "<your-access-token-secret>",
    
    "AZURE_STORAGE_CONNECTION_STRING": "UseDevelopmentStorage=true",
    "STATE_CONTAINER_NAME": "release-state",
    
    "RSS_FEED_URL": "https://github.com/github/copilot-cli/releases.atom",
    
    "AI_ENDPOINT": "<your-azure-openai-endpoint>",
    "AI_API_KEY": "<your-azure-openai-api-key>",
    "AI_MODEL": "gpt-4o-nano"
  }
}
```

### Environment Variables

| Variable | Description | Required |
|----------|-------------|----------|
| `AzureWebJobsStorage` | Azure Storage connection for Functions runtime | Yes |
| `FUNCTIONS_WORKER_RUNTIME` | Must be `dotnet-isolated` | Yes |
| `TWITTER_API_KEY` | Twitter OAuth 1.0a Consumer Key (API Key) | Yes |
| `TWITTER_API_SECRET` | Twitter OAuth 1.0a Consumer Secret (API Secret) | Yes |
| `TWITTER_ACCESS_TOKEN` | Twitter OAuth 1.0a Access Token | Yes |
| `TWITTER_ACCESS_TOKEN_SECRET` | Twitter OAuth 1.0a Access Token Secret | Yes |
| `AZURE_STORAGE_CONNECTION_STRING` | Connection string for state tracking blob storage | Yes |
| `STATE_CONTAINER_NAME` | Blob container name for state file | No (default: `release-state`) |
| `RSS_FEED_URL` | Atom feed URL to monitor | No (default: Copilot CLI releases) |
| `AI_ENDPOINT` | Azure OpenAI endpoint URL (e.g., `https://your-resource.openai.azure.com/`) | No (if not set, falls back to manual extraction) |
| `AI_API_KEY` | Azure OpenAI API key | No (if not set, falls back to manual extraction) |
| `AI_MODEL` | Azure OpenAI deployment model name | No (default: `gpt-4o-nano`) |

### Getting Twitter OAuth 1.0a Credentials

1. Go to [Twitter Developer Portal](https://developer.twitter.com/en/portal/dashboard)
2. Create a new App or use an existing one
3. Navigate to "Keys and tokens"
4. Generate/copy:
   - API Key → `TWITTER_API_KEY`
   - API Secret → `TWITTER_API_SECRET`
   - Access Token → `TWITTER_ACCESS_TOKEN`
   - Access Token Secret → `TWITTER_ACCESS_TOKEN_SECRET`
5. Ensure your app has **Read and Write** permissions

### Setting up Azure OpenAI (Optional but Recommended)

To enable AI-powered summaries:

1. Go to [Azure Portal](https://portal.azure.com/)
2. Create an **Azure OpenAI** resource
3. Deploy a model (recommended: `gpt-4o-nano` for cost-effectiveness, or `gpt-4o`, `gpt-4o-mini`)
4. Get your endpoint and API key from the resource's "Keys and Endpoint" section
5. Configure the environment variables:
   - `AI_ENDPOINT`: Your Azure OpenAI endpoint URL
   - `AI_API_KEY`: Your Azure OpenAI API key
   - `AI_MODEL`: Your deployment name (e.g., `gpt-4o-nano`)

**Note**: If AI configuration is not provided, the system will fall back to manual HTML parsing and extraction of release notes.

## Local Development

1. **Start Azurite** (Azure Storage emulator):
   ```bash
   azurite --silent --location ./azurite --debug ./azurite/debug.log
   ```

2. **Fill in credentials** in `local.settings.json`

3. **Run the function**:
   ```bash
   func start
   ```
   
   Or press F5 in VS Code with the Azure Functions extension.

4. The timer trigger runs every 15 minutes. To test immediately, you can trigger it manually via the Azure Functions Core Tools admin endpoint.

## Project Structure

```
auto-tweet-rss/
├── AutoTweetRss.csproj           # .NET 10 project file
├── Program.cs                     # Dependency injection setup
├── host.json                      # Azure Functions host configuration
├── local.settings.json            # Local environment variables (git-ignored)
├── Functions/
│   ├── ReleaseNotifierFunction.cs # Timer trigger for Copilot CLI (every 15 min)
│   └── SdkReleaseNotifierFunction.cs # Timer trigger for Copilot SDK (every 15 min)
└── Services/
    ├── RssFeedService.cs          # Fetches and filters RSS feeds
    ├── OAuth1Helper.cs            # HMAC-SHA1 signature for Twitter
    ├── TwitterApiClient.cs        # Direct HTTP calls to Twitter API v2
    ├── TweetFormatterService.cs   # Formats tweets for both CLI and SDK
    ├── ReleaseSummarizerService.cs # AI-powered release note summarization
    └── StateTrackingService.cs    # Blob storage for last processed IDs
```

## Deployment to Azure

1. Create an Azure Function App (Linux, .NET 10 Isolated)
2. Create an Azure Storage Account
3. Configure Application Settings with the environment variables above
4. Deploy using:
   ```bash
   func azure functionapp publish <your-function-app-name>
   ```

## How It Works

### ReleaseNotifier Function (Copilot CLI)

1. **Timer Trigger**: Runs every 15 minutes (`0 */15 * * * *`)
2. **Fetch Feed**: Downloads and parses the CLI Atom feed from https://github.com/github/copilot-cli/releases.atom
3. **Filter**: Removes entries with pre-release patterns (`-0`, `-1`, etc.) or "Pre-release" in content
4. **Check State**: Compares against last processed entry ID stored in blob storage (`last-processed-id.txt`)
5. **AI Summary** (if configured): Sends release content to Azure OpenAI for intelligent summarization with emojis
6. **Format**: Creates tweet with AI-generated summary or fallback to manual extraction, URL, and hashtag `#GitHubCopilotCLI`
7. **Post**: Sends to Twitter API v2 with OAuth 1.0a signature
8. **Update State**: Saves the processed entry ID to prevent duplicates

### SdkReleaseNotifier Function (Copilot SDK)

1. **Timer Trigger**: Runs every 15 minutes (`0 */15 * * * *`)
2. **Fetch Feed**: Downloads and parses the SDK Atom feed from https://github.com/github/copilot-sdk/releases.atom
3. **Filter**: Removes entries with preview releases (`-preview.X`) and Go submodule releases (`go/vX.X.X`)
4. **Check State**: Compares against last processed entry ID stored in blob storage (`sdk-last-processed-id.txt`)
5. **AI Summary** (if configured): Sends release content to Azure OpenAI for intelligent summarization with emojis
6. **Format**: Creates tweet with AI-generated summary or fallback to manual extraction, and hashtag `#GitHubCopilotSDK`
7. **Post**: Sends to Twitter API v2 with OAuth 1.0a signature
8. **Update State**: Saves the processed entry ID to prevent duplicates

## License

MIT
