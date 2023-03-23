using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using CliFx;
using CliFx.Attributes;
using CliFx.Infrastructure;
using CliWrap;
using CliWrap.Buffered;
using CliWrap.EventStream;
using OpenAI_API;
using OpenAI_API.Chat;
using Spectre.Console;

namespace SubtitlesExtractorAndRewriter;

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
    
    [CommandOption("rewrite-subtitles", 'r', Description = "Rewrite and paraphrase subtitles?", IsRequired = false)]
    public bool RewriteSubtitles { get; init; } = false;
    
    [CommandOption("translate-subtitles", 't', Description = "Translate non English subtitles?", IsRequired = false)]
    public bool TranslateSubtitles { get; init; } = false;

    [CommandOption("output-dir", 'o', Description = "Path to output dir", IsRequired = false)]
    public DirectoryInfo OutputPath { get; init; } = new("/home/maskedball/Downloads");
    
    [CommandOption("preset", Description = "Preset for paraphrasing.")]
    public ParaphrasePreset Preset { get; set; } = ParaphrasePreset.Intermediate;
    
    [CommandOption("import-to-lingq", 'i', Description = "Try import into Lingq.com as lesson?", IsRequired = false)]
    public bool ImportLesson { get; init; } = false;
    
    private string GetPromptForPreset()
    {
        return Preset switch
        {
            ParaphrasePreset.Simple => "Rewrite this text using simpler English words and grammar while preserving the meaning.",
            ParaphrasePreset.Slang => "Rewrite this text using informal language and slang while keeping the original meaning.",
            ParaphrasePreset.Formal => "Rewrite this text using formal language and professional tone while maintaining the original meaning.",
            ParaphrasePreset.Intermediate => "Rewrite this text using only B1 English vocabulary while preserving the meaning. Aim for a language level that would be easily understood by a B1 English learner.",
            ParaphrasePreset.Summarize => "Summarize (in English) this text by condensing the main ideas into a shorter version while maintaining the essential meaning.",
            _ => throw new ArgumentOutOfRangeException()
        };
    }
    
    // ReSharper disable once CognitiveComplexity
    public async ValueTask ExecuteAsync(IConsole console)
    {
        string openAiKey = Environment.GetEnvironmentVariable("OPENAI_KEY");
        string lingqCookie = Environment.GetEnvironmentVariable("LINGQ_COOKIE");

        const string requirementsMsg = "Checking requirements...";
        List<string> checkResults = null;
        await AnsiConsole.Status()
            .StartAsync(requirementsMsg, async _ =>
            {
                checkResults = await CheckConditionsAsync(openAiKey,
                    lingqCookie,
                    RewriteSubtitles,
                    ImportLesson);

                AnsiConsole.MarkupLine(checkResults.Count != 0
                    ? $"[grey]LOG:[/] {requirementsMsg}[red]fail: {String.Join(", ", checkResults)}[/]"
                    : $"[grey]LOG:[/] {requirementsMsg}[green]OK[/]");
            });
        if (checkResults.Count != 0)
        {
            Environment.Exit(1);
        }

        string tempDir = null;
        const string tempDirMsg = "Creating temp dir...";
        AnsiConsole.Status()
            .Start(tempDirMsg, _ =>
            {
                tempDir = CreateTempDir();
                AnsiConsole.MarkupLine(String.IsNullOrWhiteSpace(tempDir)
                    ? $"[grey]LOG:[/] {tempDirMsg}[red]fail[/]"
                    : $"[grey]LOG:[/] {tempDirMsg}[green]OK[/]");
            });
        if (String.IsNullOrWhiteSpace(tempDir))
        {
            Environment.Exit(1);
        }

        // Register a handler to delete the temp directory when the application exits
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Directory.Delete(tempDir, true);

        string youtubeToolPath = null;
        const string resourcesMsg = "Saving embedded resource into file...";
        await AnsiConsole.Status()
            .StartAsync(resourcesMsg, async _ =>
            {
                youtubeToolPath = await SaveEmbeddedResourceIntoFile("SubtitlesExtractorAndRewriter.Resources.yt-dlp_linux");
                AnsiConsole.MarkupLine(File.Exists(youtubeToolPath) == false
                    ? $"[grey]LOG:[/] {resourcesMsg}[red]fail[/]"
                    : $"[grey]LOG:[/] {resourcesMsg}[green]OK[/]");
            });
        if (File.Exists(youtubeToolPath) == false)
        {
            Environment.Exit(1);
        }

        const string permissionsMsg = "Grant execute permissions...";
        bool grantingPermissionResult = false;
        await AnsiConsole.Status()
            .StartAsync(permissionsMsg, async _ =>
            {
                // Grant execute permissions to the executable
                grantingPermissionResult = await GrantExecutePermissionsAsync(youtubeToolPath);
                AnsiConsole.MarkupLine(grantingPermissionResult == false
                    ? $"[grey]LOG:[/] {permissionsMsg}[red]fail[/]"
                    : $"[grey]LOG:[/] {permissionsMsg}[green]OK[/]");
            });
        if (grantingPermissionResult == false)
        {
            Environment.Exit(1);
        }

        string audioInputPath = null;
        const string getAudioMsg = "Getting audio from youtube...";
        await AnsiConsole.Status()
            .StartAsync(getAudioMsg, async _ =>
            {
                audioInputPath = await GetAudioInputPath(tempDir, youtubeToolPath, YoutubeLink, console);
                AnsiConsole.MarkupLine(File.Exists(audioInputPath) == false
                    ? $"[grey]LOG:[/] {getAudioMsg}[red]fail[/]"
                    : $"[grey]LOG:[/] {getAudioMsg}[green]OK[/]");
            });
        if (File.Exists(audioInputPath) == false)
        {
            Environment.Exit(1);
        }

        // split if needed
        if (EndTime != 0.0)
        {
            const string status = "Splitting audio...";
            bool splitResult = false;
            await AnsiConsole.Status()
                .StartAsync(status, async _ =>
                {
                    splitResult = await SplitAudio(audioInputPath, StartTime, EndTime);
                    AnsiConsole.MarkupLine(splitResult == false
                        ? $"[grey]LOG:[/] {status}[red]fail[/]"
                        : $"[grey]LOG:[/] {status}[green]OK[/]");
                });
            if (splitResult == false)
            {
                Environment.Exit(1);
            }
        }
        
        string outputDir = Path.GetDirectoryName(audioInputPath);
        if (outputDir == null)
        {
            Environment.Exit(1);
        }

        string[] files = Directory.GetFiles(outputDir);
        foreach (string file in files)
        {
            string status = $"Getting transcript for file '{file}'...";
            bool getTranscriptResult = false;
            await AnsiConsole.Status()
                .StartAsync(status, async _ =>
                {
                    getTranscriptResult = await GetTranscript(file, outputDir);
                    AnsiConsole.MarkupLine(getTranscriptResult == false
                        ? $"[grey]LOG:[/] {status}[red]fail[/]"
                        : $"[grey]LOG:[/] {status}[green]OK[/]");
                });
            if (getTranscriptResult == false)
            {
                Environment.Exit(1);
            }
        }

        string[] textFiles = Directory.GetFiles(outputDir, "*.txt", SearchOption.AllDirectories);

        string concatenatedText = "";
        foreach (string txtFile in textFiles)
        {
            concatenatedText += await File.ReadAllTextAsync(txtFile);
        }

        if (RewriteSubtitles == false)
        {
            await ImportLessonIntoLingqAccount(youtubeToolPath, lingqCookie, concatenatedText);
            WriteAllFilesToOutputDir(outputDir);
            return;
        }

        List<string> chunks = Library.SplitTextIntoChunks(concatenatedText, 3000);

        OpenAIAPI api = new(openAiKey);

        concatenatedText = "";
        string promptForPreset = GetPromptForPreset();

        const string askingGpt = "Asking ChatGPT...";
        bool errorDuringTalking = false;
        await AnsiConsole.Status()
            .StartAsync(askingGpt, async _ => 
            {
                try
                {
                    foreach (string chunk in chunks)
                    {
                        Conversation chat = api.Chat.CreateConversation();

                        chat.AppendSystemMessage(
                            "You are a helpful and advanced language model, GPT-3.5. Please paraphrase the following text using B1 English vocabulary that is easily understood by a B1 or B2 English learner. Keep the meaning of the text intact.");
                        chat.AppendUserInput(promptForPreset);
                        chat.AppendUserInput(chunk);

                        string response = await chat.GetResponseFromChatbot();
                        concatenatedText += response;
                    }
                    
                    AnsiConsole.MarkupLine($"[grey]LOG:[/] {askingGpt}[green]OK[/]");
                }
                catch (Exception)
                {
                    errorDuringTalking = true;
                    AnsiConsole.MarkupLine($"[grey]LOG:[/] {askingGpt}[red]fail[/]");
                }
            });
        if (errorDuringTalking)
        {
            Environment.Exit(1);
        }

        await ImportLessonIntoLingqAccount(youtubeToolPath, lingqCookie, concatenatedText);
        await File.WriteAllTextAsync(Path.Combine(outputDir, "simplified-output.txt"), concatenatedText);

        WriteAllFilesToOutputDir(outputDir);
    }

    public static async Task<bool> GrantExecutePermissionsAsync(string youtubeToolPath)
    {
        try
        {
            // Grant execute permissions to the executable
            await Cli.Wrap("chmod")
                .WithArguments($"u+x \"{youtubeToolPath}\"")
                .ExecuteAsync();

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string CreateTempDir()
    {
        try
        {
            string tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(tempDir);
            return tempDir;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task ImportLessonIntoLingqAccount(string youtubeToolPath, string cookie, string text)
    {
        if (ImportLesson)
        {
            const string msg = "Importing lesson into lingq account...";

            await AnsiConsole.Status()
                .StartAsync(msg, async _ => 
                {
                    BufferedCommandResult titleResult = await Cli
                        .Wrap("/bin/bash")
                        .WithArguments($"-c \"{youtubeToolPath} --get-title '{YoutubeLink}'\"")
                        .ExecuteBufferedAsync();
                    string title = titleResult.StandardOutput;
                    if (String.IsNullOrWhiteSpace(title) == false)
                    {
                        HttpStatusCode statusCode = await ImportLessonIntoLingq(cookie, title, text);
                        AnsiConsole.MarkupLine(statusCode == HttpStatusCode.Created
                            ? $"[grey]LOG:[/] {msg}[green]OK[/]"
                            : $"[grey]LOG:[/] {msg}[red]fail[/]");
                    }
                });
        }
    }

    private static async Task<HttpStatusCode> ImportLessonIntoLingq(string cookie, string title, string text)
    {
        HttpClientHandler clientHandler = new() {UseCookies = true};
        HttpClient client = new(clientHandler);

        var content = new
        {
            title,
            text,
            tags = Array.Empty<string>()
        };

        string json = JsonSerializer.Serialize(content);

        HttpRequestMessage request = new()
        {
            Method = HttpMethod.Post,
            RequestUri = new Uri("https://www.lingq.com/api/v2/en/lessons/"),
            Headers = {{"cookie", cookie}, {"accept", "application/json"}, {"authority", "www.lingq.com"},},
            Content = new StringContent(json, Encoding.UTF8, "application/json")
            {
                Headers = {ContentType = new MediaTypeHeaderValue("application/json")}
            }
        };
        using HttpResponseMessage response = await client.SendAsync(request);
        return response.StatusCode;
    }

    private static async Task<string> SaveEmbeddedResourceIntoFile(string resourceName)
    {
        Assembly assembly = Assembly.GetExecutingAssembly();
        await using Stream resourceStream = assembly.GetManifestResourceStream(resourceName);

        if (resourceStream == null)
        {
            throw new ArgumentException($"Resource not found: {resourceName}");
        }

        string tempFilePath = Path.Combine(Path.GetTempPath(), resourceName.Split('.').Last());

        await using FileStream fileStream = File.Create(tempFilePath);
        await resourceStream.CopyToAsync(fileStream);

        return tempFilePath;
    }

    private async Task<List<string>> CheckConditionsAsync(string openAiKey,
        string lingqCookie, bool rewriteSubtitles,
        bool importLesson)
    {
        List<string> errors = new();

        if (String.IsNullOrWhiteSpace(lingqCookie) && importLesson)
        {
            errors.Add("LINGQ_COOKIE env variable not set");
        }

        if (String.IsNullOrWhiteSpace(openAiKey) && rewriteSubtitles)
        {
            errors.Add("OPENAI_KEY env variable not set");
        }

        if (await IsBinaryExists("whisper") == false)
        {
            errors.Add("Whisper tool not found");
        }

        if (await IsBinaryExists("mp3splt") == false)
        {
            errors.Add("mp3splt tool not found");
        }

        if (OutputPath.Exists == false)
        {
            errors.Add("Output path not exists");
        }

        return errors;
    }

    private async Task<bool> GetTranscript(string file, string outputDir)
    {
        try
        {
            string language = await GetLanguage(file);
            string arguments =
                $"-c \"whisper '{file}' --output_format txt --output_dir '{outputDir}' {(language.Equals("English") == false && TranslateSubtitles ? $"--language {language} --task translate" : "")}\"";

            await Cli.Wrap("/bin/bash")
                .WithArguments(arguments)
                .ExecuteBufferedAsync();
            
            return true;
        }
        catch (Exception)
        {
            return false;
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
                    case StandardOutputCommandEvent stdOut:
                    {
                        if (stdOut.Text.Contains("Detected language"))
                        {
                            cts.Cancel();
                            language = stdOut.Text.Split(":")[1].Trim();
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

    private static async Task<bool> SplitAudio(string audioPath, double start, double end)
    {
        try
        {
            await Cli.Wrap("/bin/bash")
                .WithArguments($"-c \"mp3splt {audioPath} {start.ToString("F2")} {end.ToString("F2")}\"")
                .ExecuteBufferedAsync();

            File.Delete(audioPath);

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static async Task<string> GetAudioInputPath(string tempDir, string toolPath, string link, IConsole console)
    {
        try
        {
            string audioInputPath = Path.Combine(tempDir, "input.mp3");

            await Cli
                .Wrap("/bin/bash")
                .WithArguments($"-c \"{toolPath} -x --audio-format mp3 '{link}' -o '{audioInputPath}'\"")
                .ExecuteBufferedAsync();
            
            return audioInputPath;
        }
        catch (Exception)
        {
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