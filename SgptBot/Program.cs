using System.CommandLine;
using System.CommandLine.Parsing;
using System.Text.RegularExpressions;
using CliWrap;
using CliWrap.Buffered;
using Serilog;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

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
        keyOption.AddValidator(ValidateTelegramApiKey);

        Option<string> gpt3KeyOption = new("--gptkey", "GPT-3 API KEY")
        {
            IsRequired = true
        };
        gpt3KeyOption.AddAlias("-g");
        gpt3KeyOption.AddValidator(ValidateGptKey);

        Option<string> pathOption = new("--path", "Path to sgpt exec")
        {
            IsRequired = true
        };
        pathOption.AddAlias("-p");
        pathOption.AddValidator(ValidatePath);

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

    private static void ValidateTelegramApiKey(OptionResult result)
    {
        string? value = result.GetValueOrDefault<string>();
        if (String.IsNullOrEmpty(value))
        {
            result.ErrorMessage = "Key is required.";
        }

        Regex regex = new(@"^\d+:[\w\d_-]+$");
        bool isMatch = regex.IsMatch(value!);
        if (isMatch == false)
        {
            result.ErrorMessage = "Invalid key format";
        }
    }

    private static void ValidatePath(OptionResult result)
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
internal class Bot
    {
        private static ILogger _logger = Log.Logger;
        private string Path { get; }
        private List<long> Ids { get; }
        private string GptKey { get; }

        public Bot(string key, string path, List<long> ids, string gptKey)
        {
            _logger = new LoggerConfiguration()
                .WriteTo.File("log.txt", rollingInterval: RollingInterval.Day)
                .CreateLogger();

            Path = path;
            Ids = ids;
            GptKey = gptKey;
            TelegramBotClient client = new(key);
            client.StartReceiving(UpdateHandler, PollingErrorHandler);
            
            Console.ReadLine();
        }
        private static Task PollingErrorHandler(ITelegramBotClient client, Exception exception, CancellationToken token)
        {
            _logger.Error("Error. Something went wrong");
            return Task.CompletedTask;
        }

        private async Task UpdateHandler(ITelegramBotClient client, Update update, CancellationToken token)
        {
            Message? message = update.Message;
            if (message == null)
            {
                return;
            }

            if (message.Type != MessageType.Text) // working only with text
            {
                return;
            }

            long chatId = message.Chat.Id; // user ID
            _logger.Information("ID: {Id}, Name: {Name}", chatId, message.Chat.Username);
            
            string? text = message.Text;
            if (String.IsNullOrEmpty(text))
            {
                return;
            }
            
            if (Ids.Count != 0 && Ids.Contains(chatId) == false)
            {
                await client.SendTextMessageAsync(chatId, "You don't have privileges to use this bot.",
                    cancellationToken: token);
                _logger.Information(
                    "User {Name}, with ID {Id}, with message \'{Message}\' don't have privileges to use this bot",
                    message.Chat.Username, chatId, text);
                return;
            }

            string cleanedText = text.Replace("\"", "").Replace("\'", "");

            BufferedCommandResult result = await Cli.Wrap(Path)
                .WithArguments($"--key \"{GptKey}\" --promt \"{cleanedText}\"")
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync();
            if (result.ExitCode != 0)
            {
                await client.SendTextMessageAsync(chatId, "Error. Try to paraphrase your request.",
                    cancellationToken: token);
                _logger.Error("CliError. ExitCode != 0. Message: {Message}", text);
                return;
            }

            await client.SendTextMessageAsync(chatId, result.StandardOutput, cancellationToken: token);
        }
    }