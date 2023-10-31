using System.Security.Cryptography;
using System.Text;
using LiteDB;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SgptBot.Models;
using SgptBot.Services;
using Telegram.Bot;

IHost host = Host.CreateDefaultBuilder(args)
    .ConfigureServices((context, services) =>
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

public class ApplicationSettings 
{
    public long AdminId { get; }
    public string DbConnectionString { get; }
    public string DbPassword { get; }

    public ApplicationSettings(long adminId, string dbConnectionString, string dbPassword)
    {
        AdminId = adminId;
        DbConnectionString = dbConnectionString;
        DbPassword = dbPassword;
    }
}

public class UserRepository
{
    private readonly string _name;
    private readonly string _folder;
    private readonly string _password;

    public UserRepository(string name, string folder, string password)
    {
        _name = name;
        _folder = folder;
        _password = password;
    }

    public StoreUser? GetUserOrCreate(long id, string firstName, string lastName, string userName, bool isAdministrator)
    {
        using var db = new LiteDatabase(
            new ConnectionString($"Filename={Path.Combine(_folder, _name)};Password={GetSha256Hash(_password)}")
            {
                Connection = ConnectionType.Direct,
            });
        var users = db.GetCollection<StoreUser>("Users");

        var user = users.FindById(id);
        if (user != null) return user;
        user = new StoreUser(id, firstName, lastName, userName, isAdministrator);
        users.Insert(user);

        return user;
    }

    public bool UpdateUser(StoreUser updateUser)
    {
        using var db = new LiteDatabase(
            new ConnectionString($"Filename={Path.Combine(_folder, _name)};Password={GetSha256Hash(_password)}")
            {
                Connection = ConnectionType.Direct,
            });
        var users = db.GetCollection<StoreUser>("Users");

        return users.Update(updateUser);
    }
    
    public StoreUser[] GetAllUsers()
    {
        using var db = new LiteDatabase(
            new ConnectionString($"Filename={Path.Combine(_folder, _name)};Password={GetSha256Hash(_password)}")
            {
                Connection = ConnectionType.Direct,
            });
        var users = db.GetCollection<StoreUser>("Users");

        return users.FindAll().ToArray();
    }

    private static string GetSha256Hash(string inputString)
    {
        using (SHA256 sha256Hash = SHA256.Create())
        {
            byte[] bytes = sha256Hash.ComputeHash(Encoding.UTF8.GetBytes(inputString));

            StringBuilder builder = new StringBuilder();
            for (int i = 0; i < bytes.Length; i++)
            {
                builder.Append(bytes[i].ToString("x2"));
            }

            return builder.ToString();
        }
    }
}
