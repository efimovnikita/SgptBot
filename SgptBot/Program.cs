using System.CommandLine;
using System.CommandLine.Parsing;
using System.Text.RegularExpressions;

namespace SgptBot;

public static class Program
{
    private static void Main(string[] args)
    {
        Option<string> keyOption = new("--key", "Telegram API key")
        {
            IsRequired = true
        };
        keyOption.AddAlias("-k");

        Option<string> gpt3KeyOption = new("--gptkey", "GPT-3 API KEY")
        {
            IsRequired = true
        };
        gpt3KeyOption.AddAlias("-g");

        Option<string> pathOption = new("--path", "Path to sgpt exec")
        {
            IsRequired = true
        };
        pathOption.AddAlias("-p");

        Option<List<long>> idsOption = new(
            "--ids",
            "A list of allowed ids")
        {
            AllowMultipleArgumentsPerToken = true,
            IsRequired = false
        };

        RootCommand rootCommand = new("Telegram interface for SGPT");
        rootCommand.AddOption(keyOption);
        rootCommand.AddOption(pathOption);
        rootCommand.AddOption(idsOption);
        rootCommand.AddOption(gpt3KeyOption);

        rootCommand.SetHandler(RunCommand, keyOption, pathOption, idsOption, gpt3KeyOption);
            
        // Parse the command line arguments
        rootCommand.Invoke(args);
    }

    public static void ValidateGptKey(OptionResult result)
    {
        string? value = result.GetValueOrDefault<string>();
        if (String.IsNullOrEmpty(value))
        {
            result.ErrorMessage = "Key is required.";
        }

        Regex regex = new(@"^[\w\d-]+-[A-Za-z0-9-_]{32}$");
        bool isMatch = regex.IsMatch(value!);
        if (isMatch == false)
        {
            result.ErrorMessage = "Invalid key format";
        }
    }

    public static void ValidatePath(OptionResult result)
    {
        string? value = result.GetValueOrDefault<string>();
        if (String.IsNullOrEmpty(value))
        {
            result.ErrorMessage = "Path is required.";
        }

        if (new FileInfo(value!).Exists == false)
        {
            result.ErrorMessage = "Path should be exists";
        }
    }

    private static void RunCommand(string key, string path, List<long> ids, string gptKey)
    {
        Bot _ = new(key, path, ids, gptKey);
    }
}