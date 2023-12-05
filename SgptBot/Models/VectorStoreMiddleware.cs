using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using SgptBot.Shared.Models;

namespace SgptBot.Models;

public class VectorStoreMiddleware : IVectorStoreMiddleware
{
    private readonly HttpClient _client;
    private readonly string _api;
    private readonly int _maxTokensPerLine;
    private readonly int _maxTokensPerParagraph;
    private readonly int _overlapTokens;
    private readonly IRedisCacheService _cacheService;
    private readonly ILogger<VectorStoreMiddleware> _logger;
    
    public VectorStoreMiddleware(HttpClient httpClient, string api, int maxTokensPerLine, int maxTokensPerParagraph,
        int overlapTokens, ILogger<VectorStoreMiddleware> logger, IRedisCacheService cacheService)
    {
        _client = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _maxTokensPerLine = maxTokensPerLine;
        _maxTokensPerParagraph = maxTokensPerParagraph;
        _overlapTokens = overlapTokens;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _cacheService = cacheService;
    }

    public async Task<string[]> RecallMemoryFromVectorContext(StoreUser user, string prompt)
    {
        MemorySearchDto searchDto = new()
        {
            Key = user.ApiKey,
            Prompt = prompt,
            UserId = user.Id.ToString(),
            MemoryIds = user.WorkingMemory.Select(item => item.MemoryId).ToArray(),
        };

        string cacheKey = GetCacheKey(searchDto);

        try
        {
            string? cachedResponseAsync = await _cacheService.GetCachedResponseAsync(cacheKey);
            
            if (cachedResponseAsync != null)
            {
                string[]? results = JsonConvert.DeserializeObject<string[]>(cachedResponseAsync);
                _logger.LogInformation("Restore data from cache");
                return results ?? [];
            }
            
            _logger.LogInformation("Starting memory recall for user {UserId} with prompt: {Prompt}", user.Id, prompt);
            string jsonRepresentation = JsonConvert.SerializeObject(searchDto);
        
            StringContent content = new(jsonRepresentation, Encoding.UTF8, "application/json");
            HttpResponseMessage response = await _client.PostAsync($"{_api}/SearchInMemory", content);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Memory recall failed with status code: {StatusCode} for user {UserId}", response.StatusCode, user.Id);
                return [];
            }
        
            string responseStr = await response.Content.ReadAsStringAsync();
            
            if (String.IsNullOrWhiteSpace(responseStr) == false)
            {
                // store to cache
                _logger.LogInformation("Save data to cache");
                await _cacheService.SaveResponseInCacheAsync(cacheKey, responseStr, TimeSpan.FromDays(1));
            }
            
            string[]? memoryResults = JsonConvert.DeserializeObject<string[]>(responseStr);
            _logger.LogInformation("Memory recall succeeded with {Count} results for user {UserId}", memoryResults?.Length, user.Id);
            return memoryResults ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An exception occurred during memory recall for user {UserId}", user.Id);
            return [];
        }
    }

    private static string GenerateSha256Hash(string input)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));

        StringBuilder builder = new StringBuilder();
        foreach (byte t in bytes)
        {
            builder.Append(t.ToString("x2"));
        }
        return builder.ToString();
    }

    private static string GetCacheKey(MemorySearchDto searchDto) => 
        GenerateSha256Hash(searchDto.Key + searchDto.Prompt + searchDto.UserId);

    public async Task<VectorMemoryItem?> Memorize(StoreUser user, string? memories, string? fileName)
    {
        try
        {
            if (String.IsNullOrWhiteSpace(memories))
            {
                _logger.LogWarning("Attempt to memorize an empty memory string.");
                return null;
            }
        
            if (String.IsNullOrWhiteSpace(fileName))
            {
                _logger.LogWarning("Attempt to memorize with an empty file name.");
                return null;
            }

            string[] memoryIds = user.MemoryStorage.Select(item => item.MemoryId).ToArray();
            if (memoryIds.Contains(fileName))
            {
                string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
                string extension = Path.GetExtension(fileName);
                string guid = Guid.NewGuid().ToString();
                fileName = $"{fileNameWithoutExtension}_{guid}{extension}";
            }

            MemoryInputDto memoryInput = new()
            {
                Key = user.ApiKey,
                MemoryId = fileName,
                UserId = user.Id.ToString(),
                Memory = memories,
                MaxTokensPerLine = _maxTokensPerLine,
                MaxTokensPerParagraph = _maxTokensPerParagraph,
                OverlapTokens = _overlapTokens
            };
            
            string jsonRepresentation = JsonConvert.SerializeObject(memoryInput);
            StringContent content = new(jsonRepresentation, Encoding.UTF8, "application/json");
            
            HttpResponseMessage response = await _client.PostAsync($"{_api}/AddMemory", content);

            if (!response.IsSuccessStatusCode) return null;
            string responseStr = await response.Content.ReadAsStringAsync();
            VectorMemoryItem? memoryItem = JsonConvert.DeserializeObject<VectorMemoryItem>(responseStr);
            _logger.LogInformation("Memory stored successfully with Memory ID: {MemoryId}", memoryInput.MemoryId);
            return memoryItem ?? null;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error occurred while sending memory to the API.");
            return null;
        }
    }
}