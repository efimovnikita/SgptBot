using System.Diagnostics;
using System.Text.Json;
using Serilog;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using File = System.IO.File;

namespace SgptBot;

internal class Bot
{
    private static ILogger _logger = Log.Logger;
    private string Path { get; }
    private List<long> Ids { get; }
    private string GptKey { get; }

    private List<User> Users { get; set; } = new();

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
        Console.WriteLine($"Error. Something went wrong:\n{exception}");
        _logger.Error("Error. Something went wrong:\n{Message}", exception.Message);
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

        int messageId = message.MessageId;

        if (Ids.Count != 0 && Ids.Contains(chatId) == false)
        {
            await client.SendTextMessageAsync(chatId,
                "You don't have privileges to use this bot.",
                replyToMessageId: messageId,
                cancellationToken: token);
            _logger.Information(
                "User {Name}, with ID {Id}, with message \'{Message}\' don't have privileges to use this bot",
                message.Chat.Username, chatId, text);
            return;
        }

        string cleanedText = text.Replace("\"", "").Replace("\'", "");

        User? user = Users.FirstOrDefault(user => user.Id == chatId);
        if (user == null)
        {
            user = new User(chatId);
            Users.Add(user);
        }

        const string resetContextMsg = "I reset the context of our conversation.";
        if (cleanedText == "/reset")
        {
            user.Messages.Clear();
            await client.SendTextMessageAsync(chatId, resetContextMsg, replyToMessageId: messageId, cancellationToken: token);
            return;
        }
            
        user.AddMessage(UserRole.user.ToString(), cleanedText);
            
        string promptJson = JsonSerializer.Serialize(user.Messages);
            
        // Save the JSON string to a file
        string path = $"{chatId}_message.json";
        await File.WriteAllTextAsync(path, promptJson, token);
            
        string arguments = $"{Path} -k \"{GptKey}\" -p \"{path}\"";
        ProcessStartInfo start = new()
        {
            FileName = "python3",
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true
        };

        try
        {
            using Process process = Process.Start(start)!;
            using StreamReader reader = process.StandardOutput;
            string result = await reader.ReadToEndAsync();
            string trimmedResult = result.Trim();
                
            user.AddMessage(UserRole.assistant.ToString(), trimmedResult);

            await client.SendTextMessageAsync(chatId, trimmedResult, replyToMessageId: messageId, cancellationToken: token);

            if (user.Messages.Where(msg => msg.role == UserRole.user.ToString()).ToList().Count > 25)
            {
                user.Messages.Clear();
                await client.SendTextMessageAsync(chatId, resetContextMsg, replyToMessageId: messageId, cancellationToken: token);
            }
        }
        catch (Exception ex)
        {
            await client.SendTextMessageAsync(chatId, "Error. Try to paraphrase your request.", replyToMessageId: messageId,
                cancellationToken: token);
            _logger.Error("CliError\nError:\n{Error}\nMessage:\n{Message}", ex.Message, text);
        }
    }
}