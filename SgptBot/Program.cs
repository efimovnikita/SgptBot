using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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
        
        string? youtubeApi = Environment.GetEnvironmentVariable("TFYAPI");
        if (String.IsNullOrEmpty(youtubeApi))
        {
            throw new ArgumentNullException(nameof(youtubeApi), "Environment variable TFYAPI is not set.");
        }

        string? geminiApi = Environment.GetEnvironmentVariable("GEMINIAPI");
        if (String.IsNullOrEmpty(geminiApi))
        {
            throw new ArgumentException(nameof(geminiApi), "Environment variable GEMINIAPI is not set.");
        }

        string? vectorStoreApi = Environment.GetEnvironmentVariable("VECTORSTOREAPI");
        if (String.IsNullOrWhiteSpace(vectorStoreApi))
        {
            throw new ArgumentNullException(nameof(vectorStoreApi), "Environment variable VECTORSTOREAPI is not set.");
        }

        string? maxTokensPerLineStr = Environment.GetEnvironmentVariable("MAX_TOKENS_PER_LINE");
        if (String.IsNullOrWhiteSpace(maxTokensPerLineStr))
        {
            throw new ArgumentNullException(nameof(maxTokensPerLineStr), "Environment variable MAX_TOKENS_PER_LINE is not set.");
        }

        string? maxTokensPerParagraphStr = Environment.GetEnvironmentVariable("MAX_TOKENS_PER_PARAGRAPH");
        if (String.IsNullOrWhiteSpace(maxTokensPerParagraphStr))
        {
            throw new ArgumentNullException(nameof(maxTokensPerParagraphStr), "Environment variable MAX_TOKENS_PER_PARAGRAPH is not set.");
        }

        string? overlapTokensStr = Environment.GetEnvironmentVariable("OVERLAP_TOKENS");
        if (String.IsNullOrWhiteSpace(overlapTokensStr))
        {
            throw new ArgumentNullException(nameof(overlapTokensStr), "Environment variable OVERLAP_TOKENS is not set.");
        }

        int maxTokensPerLine = Int32.Parse(maxTokensPerLineStr);
        int maxTokensPerParagraph = Int32.Parse(maxTokensPerParagraphStr);
        int overlapTokens = Int32.Parse(overlapTokensStr);

        string? redisServer = Environment.GetEnvironmentVariable("REDIS_SERVER");
        if (String.IsNullOrWhiteSpace(redisServer))
        {
            throw new ArgumentNullException(nameof(redisServer), "Environment variable REDIS_SERVER is not set.");
        }

        services.AddHttpClient("telegram_bot_client")
            .AddTypedClient<ITelegramBotClient>((httpClient, _) =>
            {
                TelegramBotClientOptions options = new(token);
                return new TelegramBotClient(options, httpClient);
            });
        
        services.AddSingleton(new ApplicationSettings(Int32.Parse(adminId), ttsApi));
        services.AddSingleton<IUserRepository>(_ => new UserRepository(dbAdmin, dbPassword, dbHost, dbPort, dbDatabase));
        
        services.AddSingleton<IYoutubeTextProcessor>(_ =>
        {
            HttpClientHandler httpClientHandler = new()
            {
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            };
            HttpClient httpClient = new(httpClientHandler);
            httpClient.Timeout = TimeSpan.FromMinutes(5);
            return new YoutubeTextProcessorMiddleware(httpClient, youtubeApi);
        });

        services.AddSingleton<IGeminiProvider>(_ =>
        {
            HttpClientHandler httpClientHandler = new()
            {
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            };
            HttpClient httpClient = new(httpClientHandler);
            httpClient.Timeout = TimeSpan.FromMinutes(2);
            return new GeminiProvider(httpClient, geminiApi);
        });
        
        services.AddSingleton<IRedisCacheService>(_ => new RedisCacheService(redisServer));

        services.AddSingleton<IVectorStoreMiddleware>(serviceProvider =>
        {
            HttpClient httpClient = new();
            httpClient.Timeout = TimeSpan.FromMinutes(5);
            ILogger<VectorStoreMiddleware> logger = serviceProvider.GetRequiredService<ILogger<VectorStoreMiddleware>>();
            IRedisCacheService redisCacheService = serviceProvider.GetRequiredService<IRedisCacheService>();
            return new VectorStoreMiddleware(httpClient, vectorStoreApi, maxTokensPerLine, maxTokensPerParagraph, overlapTokens, logger, redisCacheService);
        });

        services.AddSingleton<ISummarizationProvider>(serviceProvider =>
        {
            ILogger<SummarizationProvider> logger = serviceProvider.GetRequiredService<ILogger<SummarizationProvider>>();
            return new SummarizationProvider(maxTokensPerLine, maxTokensPerParagraph, overlapTokens, logger);
        });
        
        services.AddScoped<UpdateHandler>();
        services.AddScoped<ReceiverService>();
        services.AddHostedService<PollingService>();
    })
    .Build();

await host.RunAsync();