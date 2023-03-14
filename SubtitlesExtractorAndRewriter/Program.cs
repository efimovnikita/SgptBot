using System.Diagnostics.CodeAnalysis;
using CliFx;
using CliFx.Attributes;
using CliFx.Infrastructure;
using CliWrap;
using CliWrap.Buffered;
using CliWrap.EventStream;
using OpenAI_API;
using OpenAI_API.Chat;

namespace SubtitlesExtractorAndRewriter;

internal static class Program
{
    public static async Task<int> Main()
    {
        return await new CliApplicationBuilder()
            .AddCommandsFromThisAssembly()
            .Build()
            .RunAsync();
    }
}

[Command(Description = "Download subtitles from youtube videos")]
[SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
public class DefaultCommand : ICommand
{
    [CommandOption("link", 'l', Description = "YouTube link", IsRequired = true)]
    public string YoutubeLink { get; init; }

    [CommandOption("start-time", 's', Description = "Start time", IsRequired = false)]
    public double StartTime { get; init; } = 0;

    [CommandOption("end-time", 'e', Description = "End time", IsRequired = false)]
    public double EndTime { get; init; } = 0;
    
    [CommandOption("tool-path", 'p', Description = "Path to yt-dlp tool", IsRequired = true)]
    public FileInfo ToolPath { get; init; }

    [CommandOption("rewrite-subtitles", 'r', Description = "Rewrite and paraphrase subtitles?", IsRequired = false)]
    public bool RewriteSubtitles { get; init; } = false;
    
    [CommandOption("translate-subtitles", 't', Description = "Translate non English subtitles?", IsRequired = false)]
    public bool TranslateSubtitles { get; init; } = false;
    
    [CommandOption("output-dir", 'o', Description = "Path to output dir", IsRequired = true)]
    public DirectoryInfo OutputPath { get; init; }
    
    public async ValueTask ExecuteAsync(IConsole console)
    {
        string key = Environment.GetEnvironmentVariable("OPENAI_KEY");
        if (await CheckConditionsAsync(key, console) == false)
        {
            return;
        }
        
        string tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);

        // Register a handler to delete the temp directory when the application exits
        AppDomain.CurrentDomain.ProcessExit += (sender, e) => Directory.Delete(tempDir, true);

        string audioInputPath = await GetAudioInputPath(tempDir, ToolPath.FullName, YoutubeLink, console);
        if (File.Exists(audioInputPath) == false)
        {
            await console.Output.WriteLineAsync("Something went wrong during audio download operation");
            return;
        }

        // split if needed
        if (EndTime != 0.0)
        {
            await SplitAudio(audioInputPath, StartTime, EndTime, console);
        }
        
        string outputDir = Path.GetDirectoryName(audioInputPath);
        if (outputDir == null)
        {
            await console.Output.WriteLineAsync("Error getting audio input file path");
            return;
        }

        string[] files = Directory.GetFiles(outputDir);
        foreach (string file in files)
        {
            await GetTranscript(file, outputDir, console);
        }

        string[] textFiles = Directory.GetFiles(outputDir, "*.txt", SearchOption.AllDirectories);

        string concatenatedText = "";
        foreach (string txtFile in textFiles)
        {
            concatenatedText += await File.ReadAllTextAsync(txtFile);
        }

        if (RewriteSubtitles == false)
        {
            WriteAllFilesToOutputDir(outputDir);
            return;
        }

        List<string> chunks = SplitTextIntoChunks(concatenatedText);

        OpenAIAPI api = new(key);

        concatenatedText = "";
        foreach (string chunk in chunks)
        {
            Conversation chat = api.Chat.CreateConversation();

            chat.AppendSystemMessage(
                "I want you to act as an English translator. Translate the source text into English only if needed.");
            chat.AppendUserInput(
                "Rewrite this in more simple words and grammar. Try to preserve as many source text as possible. Just replace some difficult words. Keep the meaning same:");
            chat.AppendUserInput(chunk);

            string response = await chat.GetResponseFromChatbot();
            await console.Output.WriteLineAsync(response);
            concatenatedText += response;
        }

        await File.WriteAllTextAsync(Path.Combine(outputDir, "simplified-output.txt"), concatenatedText);

        WriteAllFilesToOutputDir(outputDir);
    }
    
    private async Task<bool> CheckConditionsAsync(string key, IConsole console)
    {
        if (String.IsNullOrWhiteSpace(key))
        {
            await console.Output.WriteLineAsync("OPENAI_KEY env variable not set");
            return false;
        }

        if (await IsBinaryExists("whisper") == false || await IsBinaryExists("mp3splt") == false)
        {
            await console.Output.WriteLineAsync("Whisper tool OR mp3splt tool not found");
            return false;
        }

        if (ToolPath.Exists == false)
        {
            await console.Output.WriteLineAsync("yt-dlp tool not found");
            return false;
        }

        if (OutputPath.Exists == false)
        {
            await console.Output.WriteLineAsync("Output path not exists");
            return false;
        }

        return true;
    }

    private async Task GetTranscript(string file, string outputDir, IConsole console)
    {
        string language = await GetLanguage(file, console);
        string arguments =
            $"-c \"whisper '{file}' --output_format txt --output_dir '{outputDir}' {(language.Equals("English") == false && TranslateSubtitles ? $"--language {language} --task translate" : "")}\"";

        Command cmd = Cli.Wrap("/bin/bash")
            .WithArguments(arguments);
        await foreach (CommandEvent cmdEvent in cmd.ListenAsync())
            switch (cmdEvent)
            {
                case StartedCommandEvent:
                    await console.Output.WriteLineAsync($"Process started; arguments: {arguments}");
                    break;
                case StandardOutputCommandEvent stdOut:
                    await console.Output.WriteLineAsync(stdOut.Text);
                    break;
                case StandardErrorCommandEvent stdErr:
                    await console.Output.WriteLineAsync(stdErr.Text);
                    break;
            }
    }

    private void WriteAllFilesToOutputDir(string outputDir)
    {
        string[] files = Directory.GetFiles(outputDir);
        foreach (string file in files)
        {
            File.Copy(file, Path.Combine(OutputPath.FullName, Path.GetFileName(file)),
                true);
        }
    }

    private static async Task<string> GetLanguage(string file, IConsole console)
    {
        string language = "English";

        try
        {
            using CancellationTokenSource cts = new();
            Command cmd = Cli
                .Wrap("/bin/bash")
                .WithArguments($"-c \"whisper '{file}' --output_format txt\"");

            await foreach (CommandEvent cmdEvent in cmd.ListenAsync(cts.Token))
            {
                switch (cmdEvent)
                {
                    case StartedCommandEvent:
                    {
                        await console.Output.WriteLineAsync("Detecting language");
                        break;
                    }
                    case StandardOutputCommandEvent stdOut:
                    {
                        if (stdOut.Text.Contains("Detected language"))
                        {
                            cts.Cancel();
                            language = stdOut.Text.Split(":")[1].Trim();
                            await console.Output.WriteLineAsync($"Language found: {language}");
                            return language;
                        }
                        break;
                    }
                }
            }
        }
        catch (Exception)
        {
            return language;
        }

        return language;
    }

    // ReSharper disable once CognitiveComplexity
    private static List<string> SplitTextIntoChunks(string text)
    {
        const int chunkSize = 3000;
        List<string> chunks = new();
        int start = 0;
        int end = 0;

        while (end < text.Length)
        {
            end = start + chunkSize;

            if (end >= text.Length)
            {
                chunks.Add(text.Substring(start));
                break;
            }

            int splitIndex = text.LastIndexOf('.', end);

            if (splitIndex == -1 || splitIndex < start)
            {
                splitIndex = text.IndexOf(',', end);
            }

            if (splitIndex == -1 || splitIndex < start)
            {
                chunks.Add(text.Substring(start, chunkSize));
                start += chunkSize;
            }
            else
            {
                chunks.Add(text.Substring(start, splitIndex - start + 1));
                start = splitIndex + 1;
            }
        }

        return chunks;
    }    
    private static async Task SplitAudio(string audioInputPath, double start, double end, IConsole console)
    {
        Command cmd = Cli
            .Wrap("/bin/bash")
            .WithArguments($"-c \"mp3splt {audioInputPath} {start.ToString("F2")} {end.ToString("F2")}\"");
        
        await foreach (CommandEvent cmdEvent in cmd.ListenAsync())
        {
            switch (cmdEvent)
            {
                case StartedCommandEvent:
                    await console.Output.WriteLineAsync($"Start audio split");
                    break;
                case StandardOutputCommandEvent stdOut:
                    await console.Output.WriteLineAsync(stdOut.Text);
                    break;
                case StandardErrorCommandEvent stdErr:
                    await console.Output.WriteLineAsync(stdErr.Text);
                    break;
            }
        }

        File.Delete(audioInputPath);
    }

    private static async Task<string> GetAudioInputPath(string tempDir, string toolPath, string link, IConsole console)
    {
        try
        {
            string audioInputPath = Path.Combine(tempDir, "input.mp3");

            Command cmd = Cli
                .Wrap("/bin/bash")
                .WithArguments($"-c \"{toolPath} -x --audio-format mp3 '{link}' -o '{audioInputPath}'\"");
            
            await foreach (CommandEvent cmdEvent in cmd.ListenAsync())
            {
                switch (cmdEvent)
                {
                    case StartedCommandEvent:
                        await console.Output.WriteLineAsync($"Start download audio");
                        break;
                    case StandardOutputCommandEvent stdOut:
                        await console.Output.WriteLineAsync(stdOut.Text);
                        break;
                    case StandardErrorCommandEvent stdErr:
                        await console.Output.WriteLineAsync(stdErr.Text);
                        break;
                }
            }
            
            return audioInputPath;
        }
        catch (Exception e)
        {
            await console.Output.WriteLineAsync(e.ToString());
            return "";
        }
    }

    private static async Task<bool> IsBinaryExists(string binaryName)
    {
        BufferedCommandResult cmd = await Cli
            .Wrap("/bin/bash")
            .WithArguments($"-c \"which {binaryName}\"")
            .ExecuteBufferedAsync();
        return cmd.ExitCode == 0;
    }
}