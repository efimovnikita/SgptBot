using System.CommandLine;
using System.CommandLine.NamingConventionBinder;
using System.Diagnostics;
using OpenAI_API;
using OpenAI_API.Chat;

namespace SubtitlesExtractorAndRewriter;

static class Program
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
        
        if (IsBinaryExists("whisper") == false || IsBinaryExists("mp3splt") == false)
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

        string audioInputPath = GetAudioInputPath(tempDir, toolPath, link);
        if (File.Exists(audioInputPath) == false)
        {
            Console.WriteLine("Something went wrong during audio download operation");
            return;
        }

        // split if needed
        if (end != 0.0)
        {
            SplitAudio(audioInputPath, start, end);
        }

        // get transcripts
        string outputDir = Path.GetDirectoryName(audioInputPath);
        string[] files = Directory.GetFiles(outputDir);
        foreach (string file in files)
        {
            ProcessStartInfo startInfo = new()
            {
                FileName = "/bin/bash",
                Arguments =
                    $"-c \"whisper '{file}' --output_format txt --output_dir '{outputDir}'\"",
                RedirectStandardOutput = false
            };
            Process process = Process.Start(startInfo);
            process!.WaitForExit();
        }

        string[] textFiles = Directory.GetFiles(outputDir, "*.txt", SearchOption.AllDirectories);

        string concatenatedText = "";
        foreach (string txtFile in textFiles)
        {
            concatenatedText += File.ReadAllText(txtFile);
        }

        if (rewriteSubtitles == false) return;

        List<string> chunks = SplitTextIntoChunks(concatenatedText);

        OpenAIAPI api = new(key); // shorthand
        
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
        
        File.WriteAllText(Path.Combine(outputDir, "simplified-output.txt"), concatenatedText);
        
        files = Directory.GetFiles(outputDir);
        foreach (string file in files)
        {
            File.Copy(file, Path.Combine("/home/maskedball/Downloads", Path.GetFileName(file)),
                true);
        }
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

    private static void SplitAudio(string audioInputPath, double start, double end)
    {
        // need to split file
        ProcessStartInfo startInfo = new()
        {
            FileName = "/bin/bash",
            Arguments =
                $"-c \"mp3splt {audioInputPath} {start.ToString("F2")} {end.ToString("F2")}\"",
            RedirectStandardOutput = false
        };
        Process process = Process.Start(startInfo);
        process!.WaitForExit();

        // delete source audio file
        File.Delete(audioInputPath);
    }

    private static string GetAudioInputPath(string tempDir, string toolPath, string link)
    {
        try
        {
            string audioInputPath = Path.Combine(tempDir, "input.mp3");
            ProcessStartInfo startInfo = new()
            {
                FileName = "/bin/bash",
                Arguments =
                    $"-c \"{toolPath} -x --audio-format mp3 '{link}' -o '{audioInputPath}'\"",
                RedirectStandardOutput = false
            };
            Process process = Process.Start(startInfo);
            process!.WaitForExit();
            return audioInputPath;
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            return "";
        }
    }

    private static bool IsBinaryExists(string binaryName)
    {
        // Use the 'which' command to search for the binary in the system's $PATH
        ProcessStartInfo startInfo = new()
        {
            FileName = "/bin/bash",
            Arguments = $"-c \"which {binaryName}\"",
            RedirectStandardOutput = true
        };
        Process process = Process.Start(startInfo);
        if (process == null)
        {
            return false;
        }

        process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        // If the process exited with a 0 exit code, the binary exists
        return process.ExitCode == 0;
    }
}