using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using AutoTweetRss.Services;

var host = new HostBuilder()
    .ConfigureFunctionsWebApplication()
    .ConfigureServices(services =>
    {
        // Register HTTP client
        services.AddHttpClient();
        
        // Register services
        services.AddSingleton<RssFeedService>();
        services.AddSingleton<OAuth1Helper>();
        services.AddSingleton<TwitterApiClient>();
        services.AddSingleton<TweetFormatterService>();
        services.AddSingleton<StateTrackingService>();
    })
    .Build();

host.Run();
