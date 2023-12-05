using StackExchange.Redis;

namespace SgptBot.Models;

public class RedisCacheService : IRedisCacheService
{
    private readonly IDatabase _database;

    public RedisCacheService(string connectionString)
    {
        ConnectionMultiplexer redis = ConnectionMultiplexer.Connect(connectionString);
        _database = redis.GetDatabase();
    }

    public async Task<string?> GetCachedResponseAsync(string key)
    {
        RedisValue cachedResponse = await _database.StringGetAsync(key);
        return cachedResponse.HasValue ? cachedResponse.ToString() : null;
    }

    public async Task SaveResponseInCacheAsync(string key, string response, TimeSpan expiration)
    {
        await _database.StringSetAsync(key, response, expiration);
    }
}