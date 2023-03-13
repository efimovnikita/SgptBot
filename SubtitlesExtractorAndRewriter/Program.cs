using System.CommandLine;
using System.CommandLine.NamingConventionBinder;
using CliWrap;
using CliWrap.Buffered;
using CliWrap.EventStream;
using OpenAI_API;
using OpenAI_API.Chat;
using Command = CliWrap.Command;

namespace SubtitlesExtractorAndRewriter;

internal static class Program
{
    private static void Main(string[] args)
    {
        Option<string> linkOption = new(
            new[] {"-l", "--link"},
            "Youtube video link")
        {
            IsRequired = true
        };

        Option<double> startOption = new(
            new[] {"-s", "--start"},
            "Start time in seconds");

        Option<double> endOption = new(
            new[] {"-e", "--end"},
            "End time in seconds");

        Option<string> toolPathOption = new(
            new[] {"-p", "--tool-path"},
            "Path to yt-dlp tool")
        {
            IsRequired = true
        };

        Option<bool> rewriteSubtitlesOption = new(
            new[] {"-r", "--rewrite-subtitles"},
            () => false,
            "Rewrite subtitles");

        RootCommand rootCommand = new()
        {
            linkOption,
            startOption,
            endOption,
            toolPathOption,
            rewriteSubtitlesOption,
        };

        rootCommand.Handler = CommandHandler.Create<string, double, double, string, bool>(HandleRootCommand);

        rootCommand.Invoke(args);
    }

    private static async Task HandleRootCommand(string link, double start, double end, string toolPath,
        bool rewriteSubtitles)
    {
        #region Checks
        
        string key = Environment.GetEnvironmentVariable("OPENAI_KEY");
        if (String.IsNullOrWhiteSpace(key))
        {
            Console.WriteLine("OPENAI_KEY env variable not set");
            return;
        }
        
        if (await IsBinaryExists("whisper") == false || await IsBinaryExists("mp3splt") == false)
        {
            Console.WriteLine("Whisper tool OR mp3splt tool not found");
            return;
        }

        if (File.Exists(toolPath) == false)
        {
            Console.WriteLine("yt-dlp tool not found");
            return;
        }

        #endregion

        string tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);

        // Register a handler to delete the temp directory when the application exits
        AppDomain.CurrentDomain.ProcessExit += (sender, e) => Directory.Delete(tempDir, true);

        string audioInputPath = await GetAudioInputPath(tempDir, toolPath, link);
        if (File.Exists(audioInputPath) == false)
        {
            Console.WriteLine("Something went wrong during audio download operation");
            return;
        }

        // split if needed
        if (end != 0.0)
        {
            await SplitAudio(audioInputPath, start, end);
        }
        
        string outputDir = Path.GetDirectoryName(audioInputPath);
        string[] files = Directory.GetFiles(outputDir);
        foreach (string file in files)
        {
            // detect language
            string language = await GetLanguage(file);
            string arguments = $"-c \"whisper '{file}' --output_format txt --output_dir '{outputDir}'\"";
            
            // get transcript
            if (language.Equals("English") == false)
            {
                arguments = arguments.Substring(0, arguments.Length - 1);
                arguments += $" --language {language} --task translate\"";
            }
            
            Command cmd = Cli.Wrap("/bin/bash")
                .WithArguments(arguments);
            await foreach (CommandEvent cmdEvent in cmd.ListenAsync())
            {
                switch (cmdEvent)
                {
                    case StartedCommandEvent:
                        Console.WriteLine($"Process started; arguments: {arguments}");
                        break;
                    case StandardOutputCommandEvent stdOut:
                        Console.WriteLine(stdOut.Text);
                        break;
                    case StandardErrorCommandEvent stdErr:
                        Console.WriteLine(stdErr.Text);
                        break;
                }
            }
        }

        string[] textFiles = Directory.GetFiles(outputDir, "*.txt", SearchOption.AllDirectories);

        string concatenatedText = "";
        foreach (string txtFile in textFiles)
        {
            concatenatedText += await File.ReadAllTextAsync(txtFile);
        }

        if (rewriteSubtitles == false) return;

        List<string> chunks = SplitTextIntoChunks(concatenatedText);

        OpenAIAPI api = new(key);
        
        concatenatedText = "";
        foreach (string chunk in chunks)
        {
            Conversation chat = api.Chat.CreateConversation();
            
            chat.AppendSystemMessage("I want you to act as an English translator. Translate the source text into English only if needed.");
            chat.AppendUserInput("Rewrite this in more simple words and grammar. Try to preserve as many source text as possible. Just replace some difficult words. Keep the meaning same:");
            chat.AppendUserInput(chunk);

            string response = await chat.GetResponseFromChatbot();
            Console.WriteLine(response);
            concatenatedText += response;
        }
        
        await File.WriteAllTextAsync(Path.Combine(outputDir, "simplified-output.txt"), concatenatedText);
        
        files = Directory.GetFiles(outputDir);
        foreach (string file in files)
        {
            File.Copy(file, Path.Combine("/home/maskedball/Downloads", Path.GetFileName(file)),
                true);
        }
    }

    private static async Task<string> GetLanguage(string file)
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
                        Console.WriteLine("Detecting language");
                        break;
                    }
                    case StandardOutputCommandEvent stdOut:
                    {
                        if (stdOut.Text.Contains("Detected language"))
                        {
                            cts.Cancel();
                            language = stdOut.Text.Split(":")[1].Trim();
                            Console.WriteLine($"Language found: {language}");
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

    private static async Task SplitAudio(string audioInputPath, double start, double end)
    {
        Command cmd = Cli
            .Wrap("/bin/bash")
            .WithArguments($"-c \"mp3splt {audioInputPath} {start.ToString("F2")} {end.ToString("F2")}\"");
        
        await foreach (CommandEvent cmdEvent in cmd.ListenAsync())
        {
            switch (cmdEvent)
            {
                case StartedCommandEvent:
                    Console.WriteLine($"Start audio split");
                    break;
                case StandardOutputCommandEvent stdOut:
                    Console.WriteLine(stdOut.Text);
                    break;
                case StandardErrorCommandEvent stdErr:
                    Console.WriteLine(stdErr.Text);
                    break;
            }
        }

        File.Delete(audioInputPath);
    }

    private static async Task<string> GetAudioInputPath(string tempDir, string toolPath, string link)
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
                        Console.WriteLine($"Start download audio");
                        break;
                    case StandardOutputCommandEvent stdOut:
                        Console.WriteLine(stdOut.Text);
                        break;
                    case StandardErrorCommandEvent stdErr:
                        Console.WriteLine(stdErr.Text);
                        break;
                }
            }
            
            return audioInputPath;
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
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