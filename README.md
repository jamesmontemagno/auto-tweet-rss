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
- **AI-powered threaded posts**: Uses Microsoft.Extensions.AI with Azure OpenAI to generate concise, emoji-enhanced threads with top highlights in the first post, grouped follow-up posts, and the release link in the final post
- **Deterministic fallback**: When AI is unavailable, threads are built from HTML-parsed release notes so posting always succeeds
- Posts to Twitter/X using OAuth 1.0a authentication (with reply-chain thread support)
- Cross-posts VS Code automation to Bluesky (with AT Protocol reply thread support)
- Tracks state in Azure Blob Storage to prevent duplicate posts (separate state for CLI and SDK)
- Respects platform character limits (280 for X, 300 for Bluesky) per post in the thread

## Thread Formats

Each stream now publishes a **thread** (reply chain) instead of a single post. AI is used to rank highlights and group follow-up posts; a deterministic fallback ensures posting succeeds when AI is unavailable.

### Thread Structure (all streams)

```
Post 1 (first post):
🚀 <Release title>
<N> new additions 🧵

✨ Top highlight 1
⚡ Top highlight 2
🐛 Top highlight 3

See thread below 👇

Post 2..N (follow-up posts, per-group highlights):
✨ Feature 4
✨ Feature 5
⚡ Feature 6

Post last:
https://github.com/.../releases/tag/v1.2.3

#GitHubCopilotCLI
```

### Copilot CLI

- First post: release header, addition count, top 3 highlights, thread lead-in
- Follow-up posts: remaining grouped highlights
- Last post: release URL + `#GitHubCopilotCLI`

### Copilot SDK

- Same structure as CLI, with `#GitHubCopilotSDK`

### VS Code Insiders Daily

- First post: date header, feature count, top highlights
- Follow-up posts: remaining feature groups
- Last post: Insiders update URL + `#vscode`

### VS Code Insiders Weekly Recap

- First post: date range header, total feature count, top highlights
- Follow-up posts: remaining feature groups
- Last post: release notes URL + `#vscode`

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Azure Functions Core Tools v4](https://docs.microsoft.com/azure/azure-functions/functions-run-local)
- [Azurite](https://docs.microsoft.com/azure/storage/common/storage-use-azurite) (for local development) or an Azure Storage account
- Twitter Developer Account with OAuth 1.0a credentials
- Bluesky account + App Password (only required if you want VS Code cross-posting)

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

   "TWITTER_VSCODE_API_KEY": "<your-vscode-consumer-key>",
   "TWITTER_VSCODE_API_SECRET": "<your-vscode-consumer-secret>",
   "TWITTER_VSCODE_ACCESS_TOKEN": "<your-vscode-access-token>",
   "TWITTER_VSCODE_ACCESS_TOKEN_SECRET": "<your-vscode-access-token-secret>",

   "BLUESKY_HANDLE": "<your-handle.bsky.social>",
   "BLUESKY_APP_PASSWORD": "<your-app-password>",
    
    "AZURE_STORAGE_CONNECTION_STRING": "UseDevelopmentStorage=true",
    "STATE_CONTAINER_NAME": "release-state",
    
    "RSS_FEED_URL": "https://github.com/github/copilot-cli/releases.atom",
    
    "AI_ENDPOINT": "<your-azure-openai-endpoint>",
    "AI_API_KEY": "<your-azure-openai-api-key>",
    "AI_MODEL": "gpt-5-nano",
    "ENABLE_AI_SUMMARIES": "false",
    "AI_THREAD_PLAN_TIMEOUT_SECONDS": "240"
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
| `TWITTER_VSCODE_API_KEY` | Twitter OAuth 1.0a Consumer Key for VS Code updates | Yes (for VS Code tweets) |
| `TWITTER_VSCODE_API_SECRET` | Twitter OAuth 1.0a Consumer Secret for VS Code updates | Yes (for VS Code tweets) |
| `TWITTER_VSCODE_ACCESS_TOKEN` | Twitter OAuth 1.0a Access Token for VS Code updates | Yes (for VS Code tweets) |
| `TWITTER_VSCODE_ACCESS_TOKEN_SECRET` | Twitter OAuth 1.0a Access Token Secret for VS Code updates | Yes (for VS Code tweets) |
| `BLUESKY_HANDLE` | Bluesky handle (e.g., `yourhandle.bsky.social`) | No (only required for VS Code cross-posting) |
| `BLUESKY_APP_PASSWORD` | Bluesky App Password (Settings → App passwords) | No (only required for VS Code cross-posting) |
| `AZURE_STORAGE_CONNECTION_STRING` | Connection string for state tracking blob storage | Yes |
| `STATE_CONTAINER_NAME` | Blob container name for state file | No (default: `release-state`) |
| `RSS_FEED_URL` | Atom feed URL to monitor | No (default: Copilot CLI releases) |
| `AI_ENDPOINT` | Azure OpenAI endpoint URL (e.g., `https://your-resource.openai.azure.com/`) | No (if not set, falls back to manual extraction) |
| `AI_API_KEY` | Azure OpenAI API key | No (if not set, falls back to manual extraction) |
| `AI_MODEL` | Azure OpenAI deployment model name | No (default: `gpt-5-nano`) |
| `ENABLE_AI_SUMMARIES` | Enable AI-powered thread planning for timer functions | No (default: `false`) |
| `AI_THREAD_PLAN_TIMEOUT_SECONDS` | Timeout (seconds) for AI thread-plan requests before fallback | No (default: `240`) |
| `THREAD_MAX_POSTS` | Maximum number of posts per thread (including first and last) | No (default: `6`, minimum: `2`) |
| `THREAD_TOP_HIGHLIGHTS` | Number of top highlights shown in the first post | No (default: `3`, minimum: `1`) |

### Bluesky App Password

To enable VS Code cross-posting to Bluesky:

1. Sign in to Bluesky
2. Go to **Settings → App passwords**
3. Create a new app password (store it somewhere safe)
4. Set:
   - `BLUESKY_HANDLE` (your handle)
   - `BLUESKY_APP_PASSWORD` (the generated app password)

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
   - `AI_THREAD_PLAN_TIMEOUT_SECONDS`: Optional timeout for AI thread-plan requests (default: `240`)

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

4. **Thread behavior**: 
   - All streams now publish **threads** by default (first post + follow-ups + last post with link).
   - Thread structure is always applied; AI improves the ranking/grouping when configured.
   - Control thread size with `THREAD_MAX_POSTS` (default: `6`) and `THREAD_TOP_HIGHLIGHTS` (default: `3`).

5. **AI Thread Planning**: 
   - Timer functions always run every 15 minutes (automatic)
   - By default, AI thread planning is **disabled** for timer functions (`ENABLE_AI_SUMMARIES=false`)
   - When disabled, timer functions use deterministic HTML extraction for thread content
   - To enable AI thread planning for timer functions, set `ENABLE_AI_SUMMARIES=true` and configure AI endpoint/key

6. **Testing threads** without posting:
   
   Use the test endpoints to preview threads (always uses AI if configured):
   
   ```bash
   # Test CLI release thread
   curl http://localhost:7071/api/test-summary/cli
   
   # Test SDK release thread
   curl http://localhost:7071/api/test-summary/sdk
   
   # Test VS Code daily thread (today)
   curl http://localhost:7071/api/test-summary/vscode
   
   # Test CLI weekly recap thread
   curl http://localhost:7071/api/test-weekly-recap
   
   # Test VS Code weekly recap thread
   curl http://localhost:7071/api/test-weekly-recap/vscode
   ```
   
   Responses show numbered posts like `[Post 1/3]`, `[Post 2/3]`, `[Post 3/3]` for easy validation.

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

Preview a formatted thread for the latest CLI or SDK release (always uses AI when configured).

- Route: `GET /api/test-summary/{cli|sdk|vscode}`
- For VS Code: optional `?date=yyyy-MM-dd` query parameter

```bash
curl "http://localhost:7071/api/test-summary/cli"
curl "http://localhost:7071/api/test-summary/sdk"
curl "http://localhost:7071/api/test-summary/vscode?date=2026-02-01"
```

Returns numbered thread preview (e.g., `[Post 1/3]`, `[Post 2/3]`, `[Post 3/3]`) without posting.

**TestWeeklyRecap**

Preview the weekly thread for a given week window (PT).

- Route: `GET /api/test-weekly-recap/{cli|vscode}`
- Params:
   - `date` (optional, format `yyyy-MM-dd`, sets the week end date at 10:00 AM PT)

```bash
curl "http://localhost:7071/api/test-weekly-recap"
curl "http://localhost:7071/api/test-weekly-recap?date=2026-02-01"
curl "http://localhost:7071/api/test-weekly-recap/vscode?date=2026-02-01"
```

Returns numbered thread preview for validation.

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

**VSCodeInsidersChangelogTweet**

- Schedule: `0 */30 * * * *` (every 30 minutes, polls for today's release notes)
- Source: Raw markdown from `https://raw.githubusercontent.com/microsoft/vscode-docs/.../release-notes/v1_*.md`
- Posts: Twitter/X (VS Code account) and Bluesky (if configured)

**VSCodeWeeklyRecap**

- Schedule: `0 0 18,19 * * 6` (Saturday; posts at 10 AM PT)
- Source: Raw markdown from `https://raw.githubusercontent.com/microsoft/vscode-docs/.../release-notes/v1_*.md`
- Posts: Twitter/X (VS Code account) and Bluesky (if configured)

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
│   ├── VSCodeInsidersChangelogTweetFunction.cs # Timer trigger for VS Code insiders changelog
│   ├── VSCodeWeeklyRecapFunction.cs # Timer trigger for VS Code weekly recap (Saturday)
│   └── TestSummaryFunction.cs     # HTTP endpoint for testing AI summaries
└── Services/
    ├── RssFeedService.cs          # Fetches and filters RSS feeds
    ├── OAuth1Helper.cs            # HMAC-SHA1 signature for Twitter
    ├── TwitterApiClient.cs        # Direct HTTP calls to Twitter API v2
   ├── VSCodeTwitterApiClient.cs  # Twitter client for VS Code updates account
   ├── BlueskyApiClient.cs        # Bluesky (AT Protocol) client
   ├── VSCodeSocialMediaPublisher.cs # Publishes VS Code posts to all configured platforms
   ├── ISocialMediaClient.cs      # Abstraction for social media clients
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

### Thread Generation (all streams)

Each stream now generates and posts a **thread** (reply chain):

1. **First post**: Release/update header, feature/addition count, top N highlights (emoji-prefixed), and a "See thread below 👇" lead-in. Post is capped at the platform limit (280 chars for X, 300 for Bluesky).
2. **Follow-up posts**: Remaining highlights grouped into posts of ≤4 items each.
3. **Last post**: The release/update URL and the stream's hashtag.

**AI path** (when `ENABLE_AI_SUMMARIES=true` and AI endpoint configured): Azure OpenAI ranks and groups highlights into `topHighlights` (first post) and `threadPosts` (follow-up posts) via a structured JSON response from `PlanThreadAsync`.

**Deterministic fallback** (no AI): HTML list items are extracted, the first `THREAD_TOP_HIGHLIGHTS` become the first-post highlights, and the remainder are grouped into follow-up posts automatically.

### ReleaseNotifier Function (Copilot CLI)

1. **Timer Trigger**: Runs every 15 minutes (`0 */15 * * * *`)
2. **Fetch Feed**: Downloads and parses the CLI Atom feed from https://github.com/github/copilot-cli/releases.atom
3. **Filter**: Removes entries with pre-release patterns (`-0`, `-1`, etc.) or "Pre-release" in content
4. **Check State**: Compares against last processed entry ID stored in blob storage (`last-processed-id.txt`)
5. **Thread Plan** (if `ENABLE_AI_SUMMARIES=true`): Calls Azure OpenAI for AI-generated thread plan; falls back to HTML extraction
6. **Format**: Builds a thread: first post with top highlights, follow-up posts with grouped features, last post with URL + `#GitHubCopilotCLI`
7. **Post**: Posts each tweet in sequence as a reply chain via Twitter API v2
8. **Update State**: Saves the processed entry ID to prevent duplicates

### SdkReleaseNotifier Function (Copilot SDK)

Same as CLI but targets https://github.com/github/copilot-sdk/releases.atom, uses `sdk` feed type for AI, and appends `#GitHubCopilotSDK`.

### TestSummary Function (HTTP Endpoint)

1. **HTTP Request**: GET `/api/test-summary/{cli|sdk|vscode}`
2. **Fetch Feed**: Downloads and parses the appropriate Atom feed (or VS Code notes)
3. **Get Latest**: Retrieves the most recent stable release
4. **Thread Plan** (always enabled for test): Uses Azure OpenAI to generate thread plan
5. **Format**: Builds thread format
6. **Return**: Returns numbered thread preview (e.g., `[Post 1/3]`) without posting

## License

MIT
