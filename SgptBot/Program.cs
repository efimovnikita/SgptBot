using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SgptBot.Models;
using SgptBot.Services;
using Telegram.Bot;

IHost host = Host.CreateDefaultBuilder(args)
    .ConfigureServices((_, services) =>
    {
        var token = Environment.GetEnvironmentVariable("TOKEN");
        if(string.IsNullOrEmpty(token))
        {
            throw new ArgumentNullException("TOKEN", "Environment variable TOKEN is not set.");
        }

        var adminId = Environment.GetEnvironmentVariable("ADMIN");
        if (adminId == null)
        {
            throw new ArgumentNullException("ADMIN", "Environment variable ADMIN is not set.");
        }
        
        var dbFolder = Environment.GetEnvironmentVariable("DB");
        if (dbFolder == null)
        {
            throw new ArgumentNullException("DB", "Environment variable DB is not set.");
        }

        var dbPassword = Environment.GetEnvironmentVariable("PASSWORD");
        if (dbPassword == null)
        {
            throw new ArgumentNullException("PASSWORD", "Environment variable PASSWORD is not set.");
        }

        services.AddHttpClient("telegram_bot_client")
            .AddTypedClient<ITelegramBotClient>((httpClient, sp) =>
            {
                TelegramBotClientOptions options = new TelegramBotClientOptions(token);
                return new TelegramBotClient(options, httpClient);
            });
        
        services.AddSingleton(new ApplicationSettings(Int32.Parse(adminId), dbFolder, dbPassword));
        services.AddSingleton(new UserRepository("store", dbFolder, dbPassword));

        services.AddScoped<UpdateHandler>();
        services.AddScoped<ReceiverService>();
        services.AddHostedService<PollingService>();
    })
    .Build();

await host.RunAsync();