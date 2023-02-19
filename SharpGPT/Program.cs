using System.CommandLine;
using OpenAI_API;
using OpenAI_API.Completions;
using OpenAI_API.Models;

namespace SharpGPT;

internal static class Program
{
    private static void Main(string[] args)
    {
        Option<string> promtOption = new("--promt", "Promt for GPT-3")
        {
            IsRequired = true
        };
        promtOption.AddAlias("-p");
        
        Option<string> keyOption = new("--key", "API KEY for GPT-3")
        {
            IsRequired = true
        };
        keyOption.AddAlias("-k");
        
        RootCommand rootCommand = new("Tool for communicating with GPT-3");
        rootCommand.AddOption(promtOption);
        rootCommand.AddOption(keyOption);

        rootCommand.SetHandler(RunCommand, promtOption, keyOption);
            
        // Parse the command line arguments
        rootCommand.Invoke(args);
    }

    private static async Task RunCommand(string prompt, string key)
    {
        try
        {
            OpenAIAPI api = new(key);

            CompletionResult result = await api.Completions.CreateCompletionAsync(
                prompt,
                Model.DavinciText,
                max_tokens: 2048);

            if (result == null || String.IsNullOrEmpty(result.ToString()))
            {
                throw new Exception("An error occurred while running the command.");
            }

            Console.WriteLine(result.ToString());
        }
        catch (Exception ex)
        {
            Console.WriteLine($"An error occurred: {ex.Message}");
            Environment.ExitCode = 1;
        }
    }
}