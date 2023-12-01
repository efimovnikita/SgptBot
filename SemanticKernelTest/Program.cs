using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Configuration;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Connectors.Memory.Chroma;
using Microsoft.SemanticKernel.Memory;
#pragma warning disable CS0618 // Type or member is obsolete

namespace SemanticKernelTest;

[SuppressMessage("ReSharper", "UnusedParameter.Local")]
internal static class Program
{
    private const string EmbeddingsModel = "text-embedding-ada-002";
    private const string ChromaDbEndpoint = "http://localhost:8500";

    private static IConfiguration? _config;
    private static IKernel? _kernel;
    private static ChromaMemoryStore? _chromaStore;

    private static async Task Main(string[] args)
    {
        _config = CreateConfig();
        _kernel = CreateKernel();
        
        const string question = "When did Taylor Swift release 'Anti-Hero'?";
        
        const string collectionName = "Songs";
        
        if (_chromaStore == null)
        {
            throw new Exception("Memory store uninitialized");
        }
        
        /*await _kernel.Memory.SaveInformationAsync(
            collection: collectionName,
            text: """
                  Song name: Never Gonna Give You Up
                  Artist: Rick Astley
                  Release date: 27 July 1987
                  """,
            id: "nevergonnagiveyouup_ricksastley"
        );
        await _kernel.Memory.SaveInformationAsync(
            collection: collectionName,
            text: """
                  Song name: Anti-Hero
                  Artist: Taylor Swift
                  Release date: October 24, 2022
                  """,
            id: "antihero_taylorswift"
        );*/

        bool doesCollectionExistAsync = await _chromaStore.DoesCollectionExistAsync(collectionName);
        if (doesCollectionExistAsync == false)
        {
            Console.WriteLine("The collection doesn't exists.");
            return;
        }

        IAsyncEnumerable<MemoryQueryResult> docs = _kernel.Memory.SearchAsync(collectionName, query: question, limit: 5);
        MemoryQueryResult[] memoryQueryResults = docs.ToBlockingEnumerable().ToArray();

        int count = memoryQueryResults.Length;
        Console.WriteLine($"Memory query result found: {count}");

        if (count > 0)
        {
            Console.WriteLine("Chroma DB results: ");
            foreach ((int i, MemoryQueryResult queryResult) in memoryQueryResults.Select((result, i) => (i, result)))
            {
                string text = queryResult.Metadata.Text;
                Console.WriteLine($"Result N{i + 1}:\n{text}");
            }
        }
    }
    
    private static IConfiguration CreateConfig() => new ConfigurationBuilder()
        .AddEnvironmentVariables().
        Build();

    private static IKernel CreateKernel()
    {
        string apiKey = _config?["OpenAIKey"] ?? throw new Exception("OpenAIKey configuration required.");
        _chromaStore = new ChromaMemoryStore(ChromaDbEndpoint);

        return new KernelBuilder()
            .WithOpenAITextEmbeddingGenerationService(EmbeddingsModel, apiKey)
            .WithMemoryStorage(_chromaStore)
            .Build();
    }
}