using System.CommandLine;
using CliWrap;
using CliWrap.Buffered;
using Polly;
using ShellProgressBar;

namespace SubtitlesConverter;

internal static class Program
{
    public static void Main(string[] args)
    {
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
        pathOption.AddValidator(SgptBot.Program.ValidatePath);
        
        Option<string> fileOption = new("--file", "Path to subtitles file")
        {
            IsRequired = true
        };
        fileOption.AddAlias("-f");
        fileOption.AddValidator(SgptBot.Program.ValidatePath);
        
        Option<string> outputOption = new("--output", "Path to output file")
        {
            IsRequired = true
        };
        outputOption.AddAlias("-o");
        
        RootCommand rootCommand = new("Subtitles converter");
        rootCommand.AddOption(pathOption);
        rootCommand.AddOption(gpt3KeyOption);
        rootCommand.AddOption(fileOption);
        rootCommand.AddOption(outputOption);

        rootCommand.SetHandler(RunCommand, pathOption, gpt3KeyOption, fileOption, outputOption);
            
        // Parse the command line arguments
        rootCommand.Invoke(args);
    }

    private static void RunCommand(string sgptPath, string gptKey, string filePath, string outputPath)
    {
        List<string> chunks = SplitTextIntoChunks(filePath);

        string result = MyApiCaller.CallApi(chunks, sgptPath, gptKey);
        
        // Write the result to the specified output file
        File.WriteAllText(outputPath, result);
    }
    private static List<string> SplitTextIntoChunks(string filePath)
    {
        // Read the entire text file into a string
        string text = File.ReadAllText(filePath);

        // Remove all new line characters and extra spaces from the text
        text = text.Replace("\n", " ").Replace("\r", "").Replace("  ", " ");
        text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // Split the text into chunks of 570 characters
        List<string> chunks = new();
        int startIndex = 0;
        while (startIndex < text.Length)
        {
            int remainingLength = text.Length - startIndex;
            int length = Math.Min(570, remainingLength);
            string chunk = text.Substring(startIndex, length);

            // Check if the last character of the chunk is a whitespace
            if (chunk[length - 1] != ' ' && startIndex + length < text.Length)
            {
                // Find the index of the last whitespace in the chunk
                int lastWhitespaceIndex = chunk.LastIndexOf(' ');
                if (lastWhitespaceIndex >= 0)
                {
                    // Adjust the length to the index of the last whitespace in the chunk
                    length = lastWhitespaceIndex + 1;
                    chunk = text.Substring(startIndex, length);
                }
            }

            chunk = chunk.TrimEnd();

            // Check if the last character of the chunk is not a period
            if (chunk[^1] != '.')
            {
                // Append a period to the chunk
                chunk += '.';
            }

            chunks.Add(chunk);
            startIndex += length;
        }

        return chunks;
    }
}

public class MyApiCaller {
    private static readonly Policy RetryPolicy = Policy
        .Handle<Exception>()
        .WaitAndRetry(
            retryCount: 3, 
            sleepDurationProvider: retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt))
        );

    public static string CallApi(List<string> inputList, string path, string gptKey)
    {
        string result = "";

        ProgressBarOptions options = new()
        {
            ProgressCharacter = '─',
            ProgressBarOnBottom = true
        };

        using ProgressBar bar = new(inputList.Count, "Receive answers from GPT-3", options);
        foreach (string input in inputList) {
            Task<string>? apiResponse = RetryPolicy.Execute(async () => await CallRemoteApi(input, path, gptKey));

            if (apiResponse == null)
            {
                continue;
            }

            string responseResult = apiResponse.Result;
            result += responseResult;
            
            bar.Tick();
        }
        
        return result;
    }

    private static async Task<string> CallRemoteApi(string input, string path, string gptKey) {
        BufferedCommandResult result = await Cli.Wrap(path)
            .WithArguments($"--key \"{gptKey}\" --promt \"Turn this into the normal text and translate it to simple English:\n{input}\"")
            .ExecuteBufferedAsync();

        return result.StandardOutput;
    }
}