using System.Text;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using SgptBot.Shared.Models;

namespace SgptBot.Models;

public class VectorStoreMiddleware : IVectorStoreMiddleware
{
    private readonly HttpClient _client;
    private readonly string _api;
    private readonly ILogger<VectorStoreMiddleware> _logger;
    
    public VectorStoreMiddleware(HttpClient httpClient, string api, ILogger<VectorStoreMiddleware> logger)
    {
        _client = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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

        try
        {
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
                MaxTokensPerParagraph = 300
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