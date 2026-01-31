using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using AutoTweetRss.Services;

var host = new HostBuilder()
    .ConfigureFunctionsWebApplication()
    .ConfigureServices(services =>
    {
        // Register HTTP client
        services.AddHttpClient();
        
        // Register AI summarizer service if configured
        var aiEndpoint = Environment.GetEnvironmentVariable("AI_ENDPOINT");
        var aiApiKey = Environment.GetEnvironmentVariable("AI_API_KEY");
        var aiModel = Environment.GetEnvironmentVariable("AI_MODEL") ?? "gpt-5-nano";
        
        if (!string.IsNullOrEmpty(aiEndpoint) && !string.IsNullOrEmpty(aiApiKey))
        {
            services.AddSingleton(sp =>
            {
                var logger = sp.GetRequiredService<ILogger<ReleaseSummarizerService>>();
                return new ReleaseSummarizerService(logger, aiEndpoint, aiApiKey, aiModel);
            });
        }

        var enableDiscord = Environment.GetEnvironmentVariable("ENABLE_DISCORD_POSTS") ?? "false";
        if (!string.IsNullOrWhiteSpace(enableDiscord))
        {
            if (!bool.TryParse(enableDiscord, out var enableDiscordPosts))
            {
                throw new InvalidOperationException("ENABLE_DISCORD_POSTS must be 'true' or 'false'");
            }

            if (enableDiscordPosts)
            {
                var discordWebhookUrl = Environment.GetEnvironmentVariable("DISCORD_WEBHOOK_URL");
                if (string.IsNullOrWhiteSpace(discordWebhookUrl))
                {
                    throw new InvalidOperationException("DISCORD_WEBHOOK_URL not configured");
                }
            }
        }
        
        // Register services
        services.AddSingleton<RssFeedService>();
        services.AddSingleton<OAuth1Helper>();
        services.AddSingleton<TwitterApiClient>();
        services.AddSingleton<DiscordWebhookClient>();
        services.AddSingleton<TweetFormatterService>();
        services.AddSingleton<StateTrackingService>();
        services.AddSingleton<VSCodeReleaseNotesService>();
    })
    .Build();

host.Run();
