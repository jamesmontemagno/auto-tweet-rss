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
        
        // Register services
        services.AddSingleton<RssFeedService>();
        services.AddSingleton<OAuth1Helper>();
        services.AddSingleton<TwitterApiClient>();
        services.AddSingleton<TweetFormatterService>();
        services.AddSingleton<StateTrackingService>();
        services.AddSingleton<VSCodeReleaseNotesService>();
    })
    .Build();

host.Run();
