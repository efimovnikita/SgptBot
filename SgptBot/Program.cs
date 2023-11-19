using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SgptBot.Models;
using SgptBot.Services;
using Telegram.Bot;

IHost host = Host.CreateDefaultBuilder(args)
    .ConfigureServices((_, services) =>
    {
        string? token = Environment.GetEnvironmentVariable("TOKEN");
        if(String.IsNullOrEmpty(token))
        {
            throw new ArgumentNullException(nameof(token), "Environment variable TOKEN is not set.");
        }

        string? adminId = Environment.GetEnvironmentVariable("ADMIN");
        if (String.IsNullOrEmpty(adminId))
        {
            throw new ArgumentNullException(nameof(adminId), "Environment variable ADMIN is not set.");
        }
        
        string? dbAdmin = Environment.GetEnvironmentVariable("DBADMIN");
        if (String.IsNullOrEmpty(dbAdmin))
        {
            throw new ArgumentNullException(nameof(dbAdmin), "Environment variable DBADMIN is not set.");
        }

        string? dbPassword = Environment.GetEnvironmentVariable("DBPASSWORD");
        if (String.IsNullOrEmpty(dbPassword))
        {
            throw new ArgumentNullException(nameof(dbPassword), "Environment variable DBPASSWORD is not set.");
        }
        
        string? dbHost = Environment.GetEnvironmentVariable("DBHOST");
        if (String.IsNullOrEmpty(dbHost))
        {
            throw new ArgumentNullException(nameof(dbHost), "Environment variable DBHOST is not set.");
        }
        
        string? dbPort = Environment.GetEnvironmentVariable("DBPORT");
        if (String.IsNullOrEmpty(dbPort))
        {
            throw new ArgumentNullException(nameof(dbPort), "Environment variable DBPORT is not set.");
        }
        
        string? dbDatabase = Environment.GetEnvironmentVariable("DBDATABASE");
        if (String.IsNullOrEmpty(dbDatabase))
        {
            throw new ArgumentNullException(nameof(dbDatabase), "Environment variable DBDATABASE is not set.");
        }

        string? ttsApi = Environment.GetEnvironmentVariable("TTS");
        if (String.IsNullOrEmpty(ttsApi))
        {
            throw new ArgumentNullException(nameof(ttsApi), "Environment variable TTS is not set.");
        }

        services.AddHttpClient("telegram_bot_client")
            .AddTypedClient<ITelegramBotClient>((httpClient, _) =>
            {
                TelegramBotClientOptions options = new(token);
                return new TelegramBotClient(options, httpClient);
            });
        
        services.AddSingleton(new ApplicationSettings(Int32.Parse(adminId), ttsApi));
        services.AddSingleton<IUserRepository>(_ =>
        {
            return new UserRepository(dbAdmin, dbPassword, dbHost, dbPort, dbDatabase);
        });
        
        services.AddScoped<UpdateHandler>();
        services.AddScoped<ReceiverService>();
        services.AddHostedService<PollingService>();
    })
    .Build();

await host.RunAsync();