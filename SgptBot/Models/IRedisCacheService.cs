namespace SgptBot.Models;

public interface IRedisCacheService
{
    Task<string?> GetCachedResponseAsync(string key);
    Task SaveResponseInCacheAsync(string key, string response, TimeSpan expiration);
}