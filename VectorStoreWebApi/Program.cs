using System.Diagnostics.CodeAnalysis;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Connectors.Memory.Chroma;
using Microsoft.SemanticKernel.Memory;
using Microsoft.SemanticKernel.Text;

#pragma warning disable CS0618 // Type or member is obsolete

namespace VectorStoreWebApi;

public class Program
{
    private const string EmbeddingsModel = "text-embedding-ada-002";

    private static IConfiguration? _config;
    private static IKernel? _kernel;
    private static ChromaMemoryStore? _chromaStore;

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

        WebApplication app = builder.Build();

        // Configure the HTTP request pipeline.
        if (app.Environment.IsDevelopment())
        {
            app.UseSwagger();
            app.UseSwaggerUI();
        }

        app.UseHttpsRedirection();

        app.UseAuthorization();

        app.MapGet("/SearchInMemory", async (string key, string prompt, string userId, HttpContext _) =>
            {
                try
                {
                    _kernel = CreateKernel(key);

                    bool doesCollectionExistAsync = await _chromaStore.DoesCollectionExistAsync(userId);
                    if (doesCollectionExistAsync == false)
                    {
                        return Results.Ok(Array.Empty<string>());
                    }
                    
                    IAsyncEnumerable<MemoryQueryResult> docs =
                        _kernel.Memory.SearchAsync(collection: userId, query: prompt, limit: 3, minRelevanceScore: 0.7D);
                    MemoryQueryResult[] memoryQueryResults = docs.ToBlockingEnumerable().ToArray();

                    if (memoryQueryResults.Length == 0)
                    {
                        return Results.Ok(Array.Empty<string>());
                    }

                    return Results.Ok(memoryQueryResults.Select(result => result.Metadata.Text).ToArray());
                }
                catch (Exception ex)
                {
                    return Results.Problem(detail: $"An unexpected error occurred: {ex}",
                        statusCode: StatusCodes.Status500InternalServerError);
                }
            });
        
        app.MapGet("/SearchInSpecificMemory",
            async (string key, string prompt, string memoryId, string userId, HttpContext _) =>
        {
            try
            {
                _kernel = CreateKernel(key);

                bool doesCollectionExistAsync = await _chromaStore.DoesCollectionExistAsync(userId);
                if (doesCollectionExistAsync == false)
                {
                    return Results.Ok(Array.Empty<string>());
                }
                    
                IAsyncEnumerable<MemoryQueryResult> docs =
                    _kernel.Memory.SearchAsync(collection: userId, query: prompt, limit: 4);
                MemoryQueryResult[] memoryQueryResults = docs.ToBlockingEnumerable().ToArray();

                if (memoryQueryResults.Length == 0)
                {
                    return Results.Ok(Array.Empty<string>());
                }

                MemoryQueryResult[] queryResults = memoryQueryResults.Where(result => result.Metadata.Id == memoryId).ToArray();

                return Results.Ok(queryResults.Select(result => result.Metadata.Text).ToArray());
            }
            catch (Exception ex)
            {
                return Results.Problem(detail: $"An unexpected error occurred: {ex}",
                    statusCode: StatusCodes.Status500InternalServerError);
            }
        });
        
        app.MapPost("/AddMemory", async (MemoryInputDto input, HttpContext context) =>
        {
            try
            {
                _kernel = CreateKernel(input.Key);

                List<string> lines = TextChunker.SplitPlainTextLines(input.Memory, 128);
                string[] paragraphs = TextChunker.SplitPlainTextParagraphs(
                    lines,
                    200).ToArray();

                Dictionary<string,List<string>> result = new() {{input.MemoryId, []}};
                foreach (string paragraph in paragraphs)
                {
                    string id = await _kernel.Memory.SaveInformationAsync(
                        collection: input.UserId,
                        text: paragraph,
                        description: input.MemoryId,
                        id: Guid.NewGuid().ToString());
                    result[input.MemoryId].Add(id);
                }

                return Results.Ok(result);
            }
            catch (Exception ex)
            {
                return Results.Problem(detail: $"An unexpected error occurred: {ex}",
                    statusCode: StatusCodes.Status500InternalServerError);
            }
        });
        
        app.MapDelete("/DeleteAllMemory", async (string userId, HttpContext _) =>
        {
            try
            {
                bool doesCollectionExistAsync = await _chromaStore.DoesCollectionExistAsync(userId);
                if (doesCollectionExistAsync == false)
                {
                    return Results.Ok();
                }

                await _chromaStore.DeleteCollectionAsync(userId);

                return Results.Ok();
            }
            catch (Exception ex)
            {
                return Results.Problem(detail: $"An unexpected error occurred: {ex}",
                    statusCode: StatusCodes.Status500InternalServerError);
            }
        });
        
        app.MapDelete("/DeleteMemoryById", async (string userId, string memoryId, HttpContext _) =>
        {
            try
            {
                bool doesCollectionExistAsync = await _chromaStore.DoesCollectionExistAsync(userId);
                if (doesCollectionExistAsync == false)
                {
                    return Results.Ok();
                }
                
                await _chromaStore.RemoveAsync(userId, memoryId);

                return Results.Ok();
            }
            catch (Exception ex)
            {
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

public class MemoryInputDto
{
    public string Key { get; set; }
    public string Memory { get; set; }
    public string MemoryId { get; set; }
    public string UserId { get; set; }
}
