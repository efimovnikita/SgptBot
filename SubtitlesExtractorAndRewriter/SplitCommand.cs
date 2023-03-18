using System.Text;
using CliFx;
using CliFx.Attributes;
using CliFx.Infrastructure;
using Spectre.Console;

namespace SubtitlesExtractorAndRewriter;

[Command("split", Description = "Splits text file into chunks")]
public class SplitCommand : ICommand
{
    [CommandParameter(0, Description = "Path to the file that needs to be split into chunks")]
    public FileInfo Path { get; init; }
    
    [CommandOption("chunk-size", Description = "Chunk size", IsRequired = false)]
    public int ChunkSize { get; init; } = 3000;

    public async ValueTask ExecuteAsync(IConsole console)
    {
        if (Path.Exists == false)
        {
            await console.Output.WriteLineAsync("File not found");
            Environment.Exit(1);
        }

        List<string> chunks = Library.SplitTextIntoChunks(await File.ReadAllTextAsync(Path.FullName), ChunkSize);
        if (chunks.Count > 0)
        {
            await console.Output.WriteLineAsync();
        }
        
        StringBuilder sb = new();
        int chunksCount = chunks.Count;
        for (int i = 0; i < chunksCount; i++)
        {
            string chunk = chunks[i];
            Rule rule = new($"Chunk {i + 1}");
            rule.RuleStyle("green");
            AnsiConsole.Write(rule);
            await console.Output.WriteLineAsync(chunk);
            await console.Output.WriteLineAsync();

            sb.AppendLine(chunk);
            sb.AppendLine();
            sb.AppendLine();
        }

        await File.WriteAllTextAsync(
            System.IO.Path.Combine(Path.DirectoryName!,
                $"{System.IO.Path.GetFileNameWithoutExtension(Path.FullName)}_chunks.txt"),
            sb.ToString().TrimEnd());
    }
}