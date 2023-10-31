using Microsoft.Extensions.Logging;
using OpenAI_API;
using OpenAI_API.Chat;
using SgptBot.Models;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using Message = Telegram.Bot.Types.Message;
using Model = OpenAI_API.Models.Model;

namespace SgptBot.Services;

public class UpdateHandler : IUpdateHandler
{
    private readonly ITelegramBotClient _botClient;
    private readonly ILogger<UpdateHandler> _logger;
    private readonly ApplicationSettings _appSettings;
    private readonly UserRepository _userRepository;

    public UpdateHandler(ITelegramBotClient botClient, ILogger<UpdateHandler> logger, ApplicationSettings appSettings,
        UserRepository userRepository)
    {
        _botClient = botClient;
        _logger = logger;
        _appSettings = appSettings;
        _userRepository = userRepository;
    }

    public async Task HandleUpdateAsync(ITelegramBotClient _, Update update, CancellationToken cancellationToken)
    {
        var handler = update switch
        {
            { Message: { } message }                       => BotOnMessageReceived(message, cancellationToken),
            { EditedMessage: { } message }                 => BotOnMessageReceived(message, cancellationToken),
            _                                              => UnknownUpdateHandlerAsync(update, cancellationToken)
        };

        await handler;
    }

    private async Task BotOnMessageReceived(Message message, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Receive message type: {MessageType}", message.Type);
        if (message.Text is not { } messageText)
            return;

        var action = messageText.Split(' ')[0] switch
        {
            "/usage"           => UsageCommand(_botClient, message, cancellationToken),
            "/key"             => SetKeyCommand(_botClient, message, cancellationToken),
            "/reset"           => ResetConversationCommand(_botClient, message, cancellationToken),
            "/info"            => InfoCommand(_botClient, message, cancellationToken),
            "/model"           => ModelCommand(_botClient, message, cancellationToken),
            "/context"         => ContextCommand(_botClient, message, cancellationToken),
            "/reset_context"   => ResetContextCommand(_botClient, message, cancellationToken),
            _                  => TalkToModelCommand(_botClient, message, cancellationToken)
        };
        Message sentMessage = await action;
        _logger.LogInformation("The message was sent with id: {SentMessageId}", sentMessage.MessageId);
    }

    private async Task<Message> ResetContextCommand(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
    {
        var storeUser = GetStoreUser(message);
        if (storeUser == null)
        {
            return await botClient.SendTextMessageAsync(message.Chat.Id, "Error getting the user from the store.",
                cancellationToken: cancellationToken);
        }
        
        RemoveAllSystemMessages(storeUser);
        _userRepository.UpdateUser(storeUser);

        return await botClient.SendTextMessageAsync(message.Chat.Id,
            "Context prompt was deleted.",
            cancellationToken: cancellationToken);
    }

    private async Task<Message> ContextCommand(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
    {
        var storeUser = GetStoreUser(message);
        if (storeUser == null)
        {
            return await botClient.SendTextMessageAsync(message.Chat.Id, "Error getting the user from the store.",
                cancellationToken: cancellationToken);
        }
        
        var strings = message.Text!.Split(' ');
        if (strings.Length < 2)
        {
            return await botClient.SendTextMessageAsync(message.Chat.Id,
                "After the '/context' command you must input the context (system) prompt. Try again.",
                cancellationToken: cancellationToken);
        }

        var contextPrompt = String.Join(' ', strings.Skip(1));
        if (String.IsNullOrWhiteSpace(contextPrompt))
        {
            return await botClient.SendTextMessageAsync(message.Chat.Id,
                "After the '/context' command you must input the context (system) prompt. Try again.",
                cancellationToken: cancellationToken);
        }

        RemoveAllSystemMessages(storeUser);

        var newSystemMessage = new SgptBot.Models.Message(Role.System, contextPrompt);
        storeUser.Conversation.Insert(0, newSystemMessage);

        _userRepository.UpdateUser(storeUser);
        
        return await botClient.SendTextMessageAsync(
            message.Chat.Id, 
            "Context prompt was set.",
            cancellationToken: cancellationToken);
    }

    private static void RemoveAllSystemMessages(StoreUser storeUser)
    {
        var systemMessages = storeUser.Conversation.Where(msg => msg.Role == Role.System).ToArray();
        foreach (var systemMessage in systemMessages)
        {
            storeUser.Conversation.Remove(systemMessage);
        }
    }

    private async Task<Message> ModelCommand(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
    {
        var storeUser = GetStoreUser(message);
        if (storeUser == null)
        {
            return await botClient.SendTextMessageAsync(message.Chat.Id, "Error getting the user from the store.",
                cancellationToken: cancellationToken);
        }
        
        var strings = message.Text!.Split(' ');
        if (strings.Length < 2)
        {
            return await botClient.SendTextMessageAsync(message.Chat.Id,
                "After '/model' command you must input the model name.\nModel name must be either: 'gpt3.5' or 'gpt4'.\nTry again.",
                cancellationToken: cancellationToken);
        }

        var modelName = strings[1];
        if (String.IsNullOrWhiteSpace(modelName))
        {
            return await botClient.SendTextMessageAsync(message.Chat.Id,
                "After '/model' command you must input the model name.\nModel name must be either: 'gpt3.5' or 'gpt4'.\nTry again.",
                cancellationToken: cancellationToken);
        }
        
        if (modelName.ToLower().Equals("gpt3.5") == false && modelName.ToLower().Equals("gpt4") == false)
        {
            return await botClient.SendTextMessageAsync(message.Chat.Id,
                "After '/model' command you must input the model name.\nModel name must be either: 'gpt3.5' or 'gpt4'.\nTry again.",
                cancellationToken: cancellationToken);
        }

        var selectedModel = modelName.ToLower() switch
        {
            "gpt3.5" => Models.Model.Gpt3,
            "gpt4" => Models.Model.Gpt4,
            _ => Models.Model.Gpt3
        };

        storeUser.Model = selectedModel;
        _userRepository.UpdateUser(storeUser);

        return await botClient.SendTextMessageAsync(
            message.Chat.Id, 
            $"Model '{selectedModel}' was set.",
            cancellationToken: cancellationToken);
    }

    private async Task<Message> InfoCommand(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
    {
        var storeUser = GetStoreUser(message);
        if (storeUser == null)
        {
            return await botClient.SendTextMessageAsync(message.Chat.Id, "Error getting the user from the store.",
                cancellationToken: cancellationToken);
        }

        return await botClient.SendTextMessageAsync(message.Chat.Id,
            $"First name: {storeUser.FirstName}\n" +
            $"Last name: {storeUser.LastName}\n" +
            $"Username: {storeUser.UserName}\n" +
            $"OpenAI API key: {storeUser.ApiKey}\n" +
            $"Model: {storeUser.Model}\n" +
            $"Context prompt: {storeUser.Conversation.FirstOrDefault(msg => msg.Role == Role.System)?.Msg ?? ""}",
            cancellationToken: cancellationToken);
    }

    private async Task<Message> ResetConversationCommand(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
    {
        var storeUser = GetStoreUser(message);
        if (storeUser == null)
        {
            return await botClient.SendTextMessageAsync(message.Chat.Id, "Error getting the user from the store.",
                cancellationToken: cancellationToken);
        }

        var messages = storeUser.Conversation.Where(m => m.Role != Role.System).ToArray();
        foreach (var msg in messages)
        {
            var removeStatus = storeUser.Conversation.Remove(msg);
            if (removeStatus == false)
            {
                return await botClient.SendTextMessageAsync(message.Chat.Id, "Error while removing the conversation message.",
                    cancellationToken: cancellationToken);
            }
        }
        
        _userRepository.UpdateUser(storeUser);
        
        return await botClient.SendTextMessageAsync(message.Chat.Id, "Current conversation was reset.",
            cancellationToken: cancellationToken);
    }

    private async Task<Message> SetKeyCommand(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
    {
        var storeUser = GetStoreUser(message);
        if (storeUser == null)
        {
            return await botClient.SendTextMessageAsync(message.Chat.Id, "Error getting the user from the store.",
                cancellationToken: cancellationToken);
        }

        var strings = message.Text!.Split(' ');
        if (strings.Length < 2)
        {
            return await botClient.SendTextMessageAsync(message.Chat.Id, "After '/key' command you must input your openAI API key. Try again.",
                cancellationToken: cancellationToken);
        }

        var apiKey = strings[1];
        if (String.IsNullOrWhiteSpace(apiKey))
        {
            return await botClient.SendTextMessageAsync(message.Chat.Id, "After '/key' command you must input your openAI API key. Try again.",
                cancellationToken: cancellationToken);
        }

        storeUser.ApiKey = apiKey;
        _userRepository.UpdateUser(storeUser);

        return await botClient.SendTextMessageAsync(message.Chat.Id, "OpenAI API key was set.",
            cancellationToken: cancellationToken);
    }

    private async Task<Message> TalkToModelCommand(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
    {
        var storeUser = GetStoreUser(message);
        if (storeUser == null)
        {
            return await botClient.SendTextMessageAsync(message.Chat.Id, "Error getting the user from the store.",
                cancellationToken: cancellationToken);
        }
        
        if (String.IsNullOrWhiteSpace(storeUser.ApiKey))
        {
            return await botClient.SendTextMessageAsync(message.Chat.Id, "Your api key is not set. Use '/key' command and set key.",
                cancellationToken: cancellationToken);
        }

        if (storeUser is { IsBlocked: true, IsAdministrator: false })
        {
            return await botClient.SendTextMessageAsync(message.Chat.Id, "You are blocked. Wait for some time and try again.",
                cancellationToken: cancellationToken);
        }

        OpenAIAPI api = new OpenAIAPI(storeUser.ApiKey);
        
        var chatMessages = new List<ChatMessage>();

        var systemMessage = storeUser.Conversation.FirstOrDefault(m => m.Role == Role.System);
        if (systemMessage != null && String.IsNullOrWhiteSpace(systemMessage.Msg) == false)
        {
            chatMessages.Add(new ChatMessage(ChatMessageRole.System, systemMessage.Msg));
        }

        foreach (var msg in storeUser.Conversation.Where(m => m.Role != Role.System))
        {
            chatMessages.Add(new ChatMessage(msg.Role == Role.Ai ? ChatMessageRole.Assistant : ChatMessageRole.User, msg.Msg));
        }
        
        chatMessages.Add(new ChatMessage(ChatMessageRole.User, message.Text));

        var request = new ChatRequest
        {
            Model = storeUser.Model == Models.Model.Gpt3 ? Model.ChatGPTTurbo : Model.GPT4,
            Messages = chatMessages.ToArray()
        };

        ChatResult? result;
        try
        {
            result = await api.Chat.CreateChatCompletionAsync(request);
        }
        catch (Exception e)
        {
            return await botClient.SendTextMessageAsync(message.Chat.Id, e.Message,
                cancellationToken: cancellationToken);
        }

        var response = result.Choices[0].Message.Content;
        if (String.IsNullOrWhiteSpace(response))
        {
            return await botClient.SendTextMessageAsync(message.Chat.Id, "Response from model is empty. Try again.",
                cancellationToken: cancellationToken);
        }
        
        storeUser.Conversation.Add(new Models.Message(Role.User, message.Text!));
        storeUser.Conversation.Add(new Models.Message(Role.Ai, response));

        _userRepository.UpdateUser(storeUser);
        
        return await botClient.SendTextMessageAsync(message.Chat.Id, response,
            parseMode: ParseMode.Markdown, cancellationToken: cancellationToken);
    }

    private StoreUser? GetStoreUser(Message message)
    {
        var user = message.From;
        if (user == null)
        {
            return null;
        }

        var storeUser = _userRepository.GetUserOrCreate(user.Id, user.FirstName, user.LastName ?? "", user.Username ?? "",
            user.Id.Equals(_appSettings.AdminId));
        
        return storeUser;
    }

    private async Task<Message> UsageCommand(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
    {
        var storeUser = GetStoreUser(message);
        if (storeUser == null)
        {
            return await botClient.SendTextMessageAsync(message.Chat.Id, "Error getting the user from the store",
                cancellationToken: cancellationToken);
        }

        string usage = "Usage:\n" +
                       "/key - set an OpenAI API key\n" +
                       "/model - choose the GPT model to work with\n" +
                       "/context - set the context message\n" +
                       "/reset_context - reset the context message\n" +
                       "/history - view the conversation history\n" +
                       "/reset - reset the current conversation\n" +
                       "/usage - view the command list\n" +
                       "/info - show current settings\n" +
                       "/about - about this bot";
        
        if (storeUser.IsAdministrator)
        {
            usage = usage + Environment.NewLine + "---\n" +
                    "/allow - allow user\n" +
                    "/deny - deny user\n" +
                    "/users - show active users";
        }

        return await botClient.SendTextMessageAsync(
            chatId: message.Chat.Id,
            text: usage,
            replyMarkup: new ReplyKeyboardRemove(),
            cancellationToken: cancellationToken);
    }
    
    private Task UnknownUpdateHandlerAsync(Update update, CancellationToken _)
    {
        _logger.LogInformation("Unknown update type: {UpdateType}", update.Type);
        return Task.CompletedTask;
    }

    public async Task HandlePollingErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
    {
        var errorMessage = exception switch
        {
            ApiRequestException apiRequestException => $"Telegram API Error:\n[{apiRequestException.ErrorCode}]\n{apiRequestException.Message}",
            _ => exception.ToString()
        };

        _logger.LogInformation("HandleError: {ErrorMessage}", errorMessage);

        // Cooldown in case of network connection error
        if (exception is RequestException)
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
    }
}