using System.Diagnostics.CodeAnalysis;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Connectors.Memory.Chroma;
using Microsoft.SemanticKernel.Memory;
using Microsoft.SemanticKernel.Text;
using SgptBot.Shared.Models;

#pragma warning disable CS0618 // Type or member is obsolete

namespace VectorStoreWebApi;

public class Program
{
    private const string EmbeddingsModel = "text-embedding-ada-002";

    private static IConfiguration? _config;
    private static IKernel? _kernel;
    private static ChromaMemoryStore? _chromaStore;
    private static int _returnLimit;
    private static double _minRelScore;

    private static IConfiguration CreateConfig() => new ConfigurationBuilder()
        .AddEnvironmentVariables().
        Build();

    private static IKernel CreateKernel(string apiKey)
    {
        return new KernelBuilder()
            .WithOpenAITextEmbeddingGenerationService(EmbeddingsModel, apiKey)
            .WithMemoryStorage(_chromaStore!)
            .Build();
    }
    
    [SuppressMessage("ReSharper", "CognitiveComplexity")]
    public static async Task Main(string[] args)
    {
        _config = CreateConfig();
        string endpoint = _config["CHROMADBENDPOINT"] ?? throw new Exception("CHROMADBENDPOINT env var required.");
        string returnLimitStr = _config["RETURNLIMIT"] ?? throw new Exception("RETURNLIMIT env var required.");
        string minRelScoreStr = _config["MINRELEVANCESCORE"] ?? throw new Exception("MINRELEVANCESCORE env var required.");

        bool limitParseResult = Int32.TryParse(returnLimitStr, out _returnLimit);
        if (limitParseResult == false)
        {
            throw new Exception("RETURNLIMIT env var parse fail.");
        }

        bool minRelScoreParseResult = Double.TryParse(minRelScoreStr, out _minRelScore);
        if (minRelScoreParseResult == false)
        {
            throw new Exception("MINRELEVANCESCORE env var parse fail.");
        }

        _chromaStore = new ChromaMemoryStore(endpoint);
        
        // ensure that chromadb running
        using HttpClient client = new();
        HttpResponseMessage response = await client.GetAsync(endpoint + "/api/v1");
        response.EnsureSuccessStatusCode();
        
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

        // Add services to the container.
        builder.Services.AddAuthorization();
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen();
        builder.Services.AddCors();
        builder.Services.AddHealthChecks();

        WebApplication app = builder.Build();

        // Configure the HTTP request pipeline.
        if (app.Environment.IsDevelopment())
        {
            app.UseSwagger();
            app.UseSwaggerUI();
        }

        app.UseHttpsRedirection();

        app.UseAuthorization();

        app.MapHealthChecks("/heartbeat");
        
        app.MapPost("/SearchInMemory", async (MemorySearchDto input, HttpContext _, ILogger<Program> logger) =>
        {
            logger.LogInformation("SearchInMemory invoked with user ID: {UserId} and prompt: {Prompt}", input.UserId,
                input.Prompt);

            try
            {
                if (new[] {input.Key, input.Prompt, input.UserId}.All(s =>
                        String.IsNullOrWhiteSpace(s) == false) == false)
                {
                    logger.LogError("SearchInMemory validation failed");
                    return Results.Problem(detail: "'Key', 'Prompt', 'UserId' properties must be set.", 
                        statusCode: StatusCodes.Status400BadRequest);
                }
                
                _kernel = CreateKernel(input.Key);

                logger.LogDebug("Checking if collection exists for user ID: {UserId}", input.UserId);

                bool doesCollectionExistAsync = await _chromaStore.DoesCollectionExistAsync(input.UserId);
                if (doesCollectionExistAsync == false)
                {
                    logger.LogWarning("Collection does not exist for user ID: {UserId}", input.UserId);
                    return Results.Ok(Array.Empty<string>());
                }

                IAsyncEnumerable<MemoryQueryResult> docs =
                    _kernel.Memory.SearchAsync(collection: input.UserId, query: input.Prompt, limit: _returnLimit,
                        minRelevanceScore: _minRelScore);
                MemoryQueryResult[] memoryQueryResults = docs.ToBlockingEnumerable().ToArray();

                if (input.MemoryIds.Length > 0)
                {
                    memoryQueryResults = memoryQueryResults
                        .Where(result => input.MemoryIds.Contains(result.Metadata.Description)).ToArray();
                }

                if (memoryQueryResults.Length == 0)
                {
                    logger.LogInformation("No results found for user ID: {UserId} with prompt: {Prompt}", input.UserId,
                        input.Prompt);
                    return Results.Ok(Array.Empty<string>());
                }

                logger.LogInformation("Found {Count} results for user ID: {UserId} with prompt: {Prompt}",
                    memoryQueryResults.Length, input.UserId, input.Prompt);
                return Results.Ok(memoryQueryResults.Select(result => result.Metadata.Text).ToArray());
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "An unexpected error occurred during memory search for user ID: {UserId}",
                    input.UserId);
                return Results.Problem(detail: $"An unexpected error occurred: {ex}",
                    statusCode: StatusCodes.Status500InternalServerError);
            }
        });

        app.MapPost("/AddMemory", async (MemoryInputDto input, HttpContext _, ILogger<Program> logger) =>
        {
            logger.LogInformation("AddMemory invoked with Memory ID: {MemoryId} for User ID: {UserId}", input.MemoryId,
                input.UserId);

            try
            {
                if (new[] {input.Key, input.Memory, input.MemoryId, input.UserId}.All(s =>
                        String.IsNullOrWhiteSpace(s) == false) == false)
                {
                    logger.LogError("AddMemory validation failed");
                    return Results.Problem(detail: "All input properties must be set.", 
                        statusCode: StatusCodes.Status400BadRequest);
                }
                
                _kernel = CreateKernel(input.Key);

                List<string> lines = TextChunker.SplitPlainTextLines(text: input.Memory, maxTokensPerLine: input.MaxTokensPerLine);
                string[] paragraphs = TextChunker.SplitPlainTextParagraphs(
                    lines: lines,
                    overlapTokens: input.OverlapTokens,
                    maxTokensPerParagraph: input.MaxTokensPerParagraph).ToArray();

                logger.LogDebug("Split memory text into {ParagraphCount} paragraphs.", paragraphs.Length);

                VectorMemoryItem memoryItem = new() { MemoryId = input.MemoryId };
                foreach (string paragraph in paragraphs)
                {
                    logger.LogDebug("Saving a paragraph with Memory ID: {MemoryId}", input.MemoryId);

                    string id = await _kernel.Memory.SaveInformationAsync(
                        collection: input.UserId,
                        text: paragraph,
                        description: input.MemoryId,
                        id: Guid.NewGuid().ToString());
                    
                    memoryItem.ChunkIds.Add(id);
                }

                logger.LogInformation("Memory added successfully with Memory ID: {MemoryId} for User ID: {UserId}",
                    input.MemoryId, input.UserId);

                return Results.Ok(memoryItem);
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "An unexpected error occurred while adding memory for User ID: {UserId} with Memory ID: {MemoryId}",
                    input.UserId, input.MemoryId);

                return Results.Problem(detail: $"An unexpected error occurred: {ex}",
                    statusCode: StatusCodes.Status500InternalServerError);
            }
        });

        app.MapDelete("/DeleteAllMemory", async (string userId, HttpContext _, ILogger<Program> logger) =>
        {
            logger.LogInformation("DeleteAllMemory invoked for User ID: {UserId}", userId);

            try
            {
                if (String.IsNullOrWhiteSpace(userId))
                {
                    logger.LogError("DeleteAllMemory validation failed");
                    return Results.Problem(detail: "'userId' input property must be set.", 
                        statusCode: StatusCodes.Status400BadRequest);
                }
                
                logger.LogDebug("Checking existence of collection for User ID: {UserId}", userId);

                bool doesCollectionExistAsync = await _chromaStore.DoesCollectionExistAsync(userId);
                if (doesCollectionExistAsync == false)
                {
                    logger.LogWarning("No collection to delete for User ID: {UserId}, collection does not exist.",
                        userId);

                    return Results.Ok();
                }

                logger.LogDebug("Deleting collection for User ID: {UserId}", userId);

                await _chromaStore.DeleteCollectionAsync(userId);

                logger.LogInformation("Successfully deleted all memory for User ID: {UserId}", userId);

                return Results.Ok();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "An unexpected error occurred while deleting all memory for User ID: {UserId}",
                    userId);

                return Results.Problem(detail: $"An unexpected error occurred: {ex}",
                    statusCode: StatusCodes.Status500InternalServerError);
            }
        });

        app.MapPost("/DeleteMemoryById", async (MemoryDeleteDto input, HttpContext _, ILogger<Program> logger) =>
        {
            logger.LogInformation("DeleteMemoryById invoked for User ID: {UserId}", input.UserId);

            try
            {
                if (String.IsNullOrWhiteSpace(input.UserId))
                {
                    logger.LogError("DeleteMemoryById validation failed");
                    return Results.Problem(detail: "'userId' input property must be set.", 
                        statusCode: StatusCodes.Status400BadRequest);
                }
                
                if (input.IdListToDelete.Length == 0)
                {
                    logger.LogError("DeleteMemoryById validation failed");
                    return Results.Problem(detail: "'IdListToDelete' array property is empty.", 
                        statusCode: StatusCodes.Status400BadRequest);
                }
                
                logger.LogDebug("Checking if collection exists for User ID: {UserId}", input.UserId);

                bool doesCollectionExistAsync = await _chromaStore.DoesCollectionExistAsync(input.UserId);
                if (doesCollectionExistAsync == false)
                {
                    logger.LogWarning("Cannot delete memory, collection does not exist for User ID: {UserId}",
                        input.UserId);

                    return Results.Ok();
                }

                logger.LogInformation("Deleting {MemoryCount} memory items by ID for User ID: {UserId}",
                    input.IdListToDelete.Length, input.UserId);

                foreach (string id in input.IdListToDelete)
                {
                    logger.LogDebug("Deleting memory with ID: {MemoryId} for User ID: {UserId}", id, input.UserId);

                    await _chromaStore.RemoveAsync(input.UserId, id);
                }

                logger.LogInformation("All specified memories deleted successfully for User ID: {UserId}",
                    input.UserId);
                return Results.Ok();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "An unexpected error occurred while deleting memories for User ID: {UserId}",
                    input.UserId);

                return Results.Problem(detail: $"An unexpected error occurred: {ex}",
                    statusCode: StatusCodes.Status500InternalServerError);
            }
        });
        
        app.UseCors(policyBuilder =>
            policyBuilder.AllowAnyOrigin()
                .AllowAnyMethod()
                .AllowAnyHeader());

        await app.RunAsync();
    }
}