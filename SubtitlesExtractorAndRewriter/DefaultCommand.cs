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
    
    [CommandOption("lingq-import", Description = "Try import into Lingq.com as lesson?", IsRequired = false)]
    public bool ImportLesson { get; init; } = false;
    
    private string GetPromptForPreset()
    {
        return Preset switch
        {
            ParaphrasePreset.Simple => "Rewrite this text using simpler English words and grammar while preserving the meaning.",
            ParaphrasePreset.Slang => "Rewrite this text using informal language and slang while keeping the original meaning.",
            ParaphrasePreset.Formal => "Rewrite this text using formal language and professional tone while maintaining the original meaning.",
            ParaphrasePreset.Intermediate => "Rewrite this text using only B1 English vocabulary while preserving the meaning. Aim for a language level that would be easily understood by a B1 English learner.",
            _ => throw new ArgumentOutOfRangeException()
        };
    }
    
    public async ValueTask ExecuteAsync(IConsole console)
    {
        string openAiKey = Environment.GetEnvironmentVariable("OPENAI_KEY");
        string lingqCookie = Environment.GetEnvironmentVariable("LINGQ_COOKIE");

        if (await CheckConditionsAsync(openAiKey, lingqCookie, RewriteSubtitles, ImportLesson, console) == false)
        {
            Environment.Exit(1);
        }
        
        string tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);

        // Register a handler to delete the temp directory when the application exits
        AppDomain.CurrentDomain.ProcessExit += (sender, e) => Directory.Delete(tempDir, true);

        string youtubeToolPath = await SaveEmbeddedResourceIntoFile("SubtitlesExtractorAndRewriter.Resources.yt-dlp_linux");
        if (File.Exists(youtubeToolPath) == false)
        {
            await console.Output.WriteAsync("Youtube download tool not found");
            Environment.Exit(1);
        }
        
        // Grant execute permissions to the executable
        await Cli.Wrap("chmod")
            .WithArguments($"u+x \"{youtubeToolPath}\"")
            .ExecuteAsync();

        string audioInputPath = await GetAudioInputPath(tempDir, youtubeToolPath, YoutubeLink, console);
        if (File.Exists(audioInputPath) == false)
        {
            await console.Output.WriteLineAsync("Something went wrong during audio download operation");
            Environment.Exit(1);
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
            Environment.Exit(1);
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
            await ImportLessonIntoLingqAccount(youtubeToolPath, lingqCookie, concatenatedText, console);
            WriteAllFilesToOutputDir(outputDir);
            return;
        }

        List<string> chunks = Library.SplitTextIntoChunks(concatenatedText, 3000);

        OpenAIAPI api = new(lingqCookie);

        concatenatedText = "";
        string promptForPreset = GetPromptForPreset();
        await console.Output.WriteLineAsync($"Preset prompt is: '{promptForPreset}'");
        
        foreach (string chunk in chunks)
        {
            Conversation chat = api.Chat.CreateConversation();

            chat.AppendSystemMessage(
                "You are a helpful and advanced language model, GPT-3.5. Please paraphrase the following text using B1 English vocabulary that is easily understood by a B1 or B2 English learner. Keep the meaning of the text intact.");
            chat.AppendUserInput(promptForPreset);
            chat.AppendUserInput(chunk);

            string response = await chat.GetResponseFromChatbot();
            await console.Output.WriteLineAsync(response);
            concatenatedText += response;
        }

        await ImportLessonIntoLingqAccount(youtubeToolPath, lingqCookie, concatenatedText, console);
        await File.WriteAllTextAsync(Path.Combine(outputDir, "simplified-output.txt"), concatenatedText);

        WriteAllFilesToOutputDir(outputDir);
    }

    private async Task ImportLessonIntoLingqAccount(string youtubeToolPath, string cookie, string text,
        IConsole console)
    {
        if (ImportLesson)
        {
            BufferedCommandResult titleResult = await Cli
                .Wrap("/bin/bash")
                .WithArguments($"-c \"{youtubeToolPath} --get-title '{YoutubeLink}'\"")
                .ExecuteBufferedAsync();
            string title = titleResult.StandardOutput;
            if (String.IsNullOrWhiteSpace(title) == false)
            {
                HttpStatusCode statusCode = await ImportLessonIntoLingq(cookie, title, text);
                await console.Output.WriteLineAsync(statusCode != HttpStatusCode.Created
                    ? $"Lesson import was unsuccessful: {statusCode}"
                    : "Lesson import was successful!");
            }
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

    private async Task<bool> CheckConditionsAsync(string openAiKey,
        string lingqCookie, bool rewriteSubtitles,
        bool importLesson, IConsole console)
    {
        if (String.IsNullOrWhiteSpace(lingqCookie) && importLesson)
        {
            await console.Output.WriteLineAsync("LINGQ_COOKIE env variable not set");
            return false;
        }
        
        if (String.IsNullOrWhiteSpace(openAiKey) && rewriteSubtitles)
        {
            await console.Output.WriteLineAsync("OPENAI_KEY env variable not set");
            return false;
        }

        if (await IsBinaryExists("whisper") == false || await IsBinaryExists("mp3splt") == false)
        {
            await console.Output.WriteLineAsync("Whisper tool OR mp3splt tool not found");
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