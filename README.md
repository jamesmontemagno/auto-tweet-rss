# Auto Tweet RSS

An Azure Function that monitors GitHub Copilot releases RSS feeds and automatically tweets about new stable releases.

## Table of Contents

- [Features](#features)
- [Tweet Formats](#tweet-formats)
- [Prerequisites](#prerequisites)
- [Configuration](#configuration)
- [Local Development](#local-development)
- [Functions and Endpoints](#functions-and-endpoints)
- [Project Structure](#project-structure)
- [Deployment to Azure](#deployment-to-azure)
- [How It Works](#how-it-works)
- [License](#license)

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
    "AI_MODEL": "gpt-5-nano",
    "ENABLE_AI_SUMMARIES": "false"
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
| `AI_MODEL` | Azure OpenAI deployment model name | No (default: `gpt-5-nano`) |
| `ENABLE_AI_SUMMARIES` | Enable AI-powered summaries for timer functions | No (default: `false`) |

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

4. **AI Summaries**: 
   - Timer functions always run every 15 minutes (automatic)
   - By default, AI summaries are **disabled** for timer functions (`ENABLE_AI_SUMMARIES=false`)
   - When disabled, timer functions use classic HTML parsing
   - To enable AI summaries for timer functions, set `ENABLE_AI_SUMMARIES=true` and configure AI endpoint/key

5. **Testing the AI summary** without tweeting:
   
   Use the test endpoint to preview AI-generated summaries (always uses AI if configured):
   
   ```bash
   # Test CLI release summary
   curl http://localhost:7071/api/test-summary/cli
   
   # Test SDK release summary
   curl http://localhost:7071/api/test-summary/sdk
   ```
   
   This fetches the latest release and generates the tweet format without posting to Twitter.

## Functions and Endpoints

### Authorization Notes

- Local dev (`func start`) uses `http://localhost:7071/api/...` and does not require a key by default.
- Deployed Function Apps require a function key unless you change the auth level. Use `?code=<function-key>` or set `x-functions-key`.

### HTTP Endpoints (local examples)

**CliReleaseSummary**

Generate an AI summary paragraph for a specific Copilot CLI version.

- Route: `GET /api/cli-summary`
- Params:
   - `version` (required)
   - `maxLength` (optional, default: 700)
   - `format` (optional: `json` or `text`, default: `json`)

```bash
curl "http://localhost:7071/api/cli-summary?version=v1.7.0&maxLength=500&format=json"
```

**TestSummary**

Preview a formatted tweet for the latest CLI or SDK release (always uses AI when configured).

- Route: `GET /api/test-summary/{cli|sdk}`

```bash
curl "http://localhost:7071/api/test-summary/cli"
curl "http://localhost:7071/api/test-summary/sdk"
```

**TestWeeklyRecap**

Preview the weekly CLI recap tweet for a given week window (PT).

- Route: `GET /api/test-weekly-recap`
- Params:
   - `date` (optional, format `yyyy-MM-dd`, sets the week end date at 10:00 AM PT)

```bash
curl "http://localhost:7071/api/test-weekly-recap"
curl "http://localhost:7071/api/test-weekly-recap?date=2026-02-01"
```

**VSCodeInsiders**

Get VS Code Insiders release notes with optional AI summary.

- Route: `GET /api/vscode-insiders`
- Params:
   - `date` (optional: `yyyy-MM-dd`, `full`, `this week`, or `this-week`)
   - `format` (optional: `json` or `text`, default: `json`)
   - `forceRefresh` (optional: `true` or `false`)
   - `aionly` (optional: `true` or `false`)
   - `newline` (optional: `br`, `lf`, `crlf`, or `literal`)

```bash
curl "http://localhost:7071/api/vscode-insiders?date=this-week&format=json"
curl "http://localhost:7071/api/vscode-insiders?date=full&format=text"
```

**GitHubChangelogCopilotLookup**

Look up a GitHub Blog changelog entry by URL and return the description if it has a Copilot label.

- Route: `POST /api/github-changelog/copilot`
- Body:
   - `url` (required, absolute URL)

```bash
curl -X POST "http://localhost:7071/api/github-changelog/copilot" \
   -H "Content-Type: application/json" \
   -d "{\"url\":\"https://github.blog/changelog/2025-01-01-example/\"}"
```

### Timer Functions

**ReleaseNotifier**

- Schedule: `0 */15 * * * *` (every 15 minutes)
- Feed: `RSS_FEED_URL` (default Copilot CLI releases)

**SdkReleaseNotifier**

- Schedule: `0 */15 * * * *` (every 15 minutes)
- Feed: fixed `https://github.com/github/copilot-sdk/releases.atom`

**WeeklyCliRecap**

- Schedule: `0 0 17,18 * * 6` (runs Saturday; posts at 10 AM PT)
- Feed: fixed `https://github.com/github/copilot-cli/releases.atom`

## Project Structure

```
auto-tweet-rss/
├── AutoTweetRss.csproj           # .NET 10 project file
├── Program.cs                     # Dependency injection setup
├── host.json                      # Azure Functions host configuration
├── local.settings.json            # Local environment variables (git-ignored)
├── Functions/
│   ├── ReleaseNotifierFunction.cs # Timer trigger for Copilot CLI (every 15 min)
│   ├── SdkReleaseNotifierFunction.cs # Timer trigger for Copilot SDK (every 15 min)
│   └── TestSummaryFunction.cs     # HTTP endpoint for testing AI summaries
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
5. **AI Summary** (if `ENABLE_AI_SUMMARIES=true`): Sends release content to Azure OpenAI for intelligent summarization with emojis, otherwise uses classic HTML parsing
6. **Format**: Creates tweet with AI-generated summary or manual extraction, URL, and hashtag `#GitHubCopilotCLI`
7. **Post**: Sends to Twitter API v2 with OAuth 1.0a signature
8. **Update State**: Saves the processed entry ID to prevent duplicates

### SdkReleaseNotifier Function (Copilot SDK)

1. **Timer Trigger**: Runs every 15 minutes (`0 */15 * * * *`)
2. **Fetch Feed**: Downloads and parses the SDK Atom feed from https://github.com/github/copilot-sdk/releases.atom
3. **Filter**: Removes entries with preview releases (`-preview.X`) and Go submodule releases (`go/vX.X.X`)
4. **Check State**: Compares against last processed entry ID stored in blob storage (`sdk-last-processed-id.txt`)
5. **AI Summary** (if `ENABLE_AI_SUMMARIES=true`): Sends release content to Azure OpenAI for intelligent summarization with emojis, otherwise uses classic HTML parsing
6. **Format**: Creates tweet with AI-generated summary or manual extraction, and hashtag `#GitHubCopilotSDK`
7. **Post**: Sends to Twitter API v2 with OAuth 1.0a signature
8. **Update State**: Saves the processed entry ID to prevent duplicates

### TestSummary Function (HTTP Endpoint)

1. **HTTP Request**: GET `/api/test-summary/{cli|sdk}`
2. **Fetch Feed**: Downloads and parses the appropriate Atom feed
3. **Get Latest**: Retrieves the most recent stable release
4. **AI Summary** (always enabled): Uses Azure OpenAI to generate summary regardless of `ENABLE_AI_SUMMARIES` setting
5. **Format**: Creates tweet format with AI-generated summary
6. **Return**: Returns formatted tweet preview without posting to Twitter

## License

MIT
