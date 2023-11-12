using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OpenAiNg;
using OpenAiNg.Audio;
using OpenAiNg.Chat;
using OpenAiNg.Images;
using SgptBot.Models;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using File = Telegram.Bot.Types.File;
using Message = Telegram.Bot.Types.Message;

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

    public async Task HandleUpdateAsync(ITelegramBotClient client, Update update, CancellationToken cancellationToken)
    {
        var handler = update switch
        {
            { Message: { } message }                       => BotOnMessageReceived(message, client, cancellationToken),
            { EditedMessage: { } message }                 => BotOnMessageReceived(message, client, cancellationToken),
            { CallbackQuery: { } callbackQuery }           => BotOnCallbackQueryReceived(callbackQuery, client, cancellationToken),
            _                                              => UnknownUpdateHandlerAsync(update, cancellationToken)
        };

        await handler;
    }
    
    private async Task<Message> BotOnCallbackQueryReceived(CallbackQuery callbackQuery, ITelegramBotClient botClient,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Received inline keyboard callback from: {CallbackQueryId}", callbackQuery.Id);

        Message message = callbackQuery.Message!;
        
        var storeUser = GetStoreUser(callbackQuery.From);
        if (storeUser == null)
        {
            return await botClient.SendTextMessageAsync(message.Chat.Id, "Error getting the user from the store.",
                cancellationToken: cancellationToken);
        }
        
        string? data = callbackQuery.Data;
        if (String.IsNullOrWhiteSpace(data))
        {
            return await botClient.SendTextMessageAsync(message.Chat.Id, "Callback query data is empty.",
                cancellationToken: cancellationToken);
        }
        
        var strings = data.Split(' ');
        
        await _botClient.AnswerCallbackQueryAsync(
            callbackQueryId: callbackQuery.Id,
            text: $"Received {callbackQuery.Data}",
            cancellationToken: cancellationToken);
        
        return await SetSelectedModel(botClient, message.Chat.Id, strings, storeUser, cancellationToken);
    }

    private async Task BotOnMessageReceived(Message message, ITelegramBotClient client,
        CancellationToken cancellationToken)
    {
        MessageType messageType = message.Type;
        _logger.LogInformation("Receive message type: {MessageType}", messageType);
        
        if (messageType == MessageType.Photo)
        {
            await SendPhotoToVisionModel(_botClient, message, cancellationToken);   
            return;
        }
        
        string messageText = message.Text ?? await GetTranscriptionTextFromVoiceMessage(message, client, cancellationToken);
        if (String.IsNullOrEmpty(messageText))
        {
            _logger.LogWarning("[{MethodName}] Message is empty. Return.", nameof(BotOnMessageReceived));
            return;
        }

        Task<Message> action = messageText.Split(' ')[0] switch
        {
            "/usage"                 => UsageCommand(_botClient, message, cancellationToken),
            "/key"                   => SetKeyCommand(_botClient, message, cancellationToken),
            "/reset"                 => ResetConversationCommand(_botClient, message, cancellationToken),
            "/info"                  => InfoCommand(_botClient, message, cancellationToken),
            "/model"                 => ModelCommand(_botClient, message, cancellationToken),
            "/context"               => ContextCommand(_botClient, message, cancellationToken),
            "/reset_context"         => ResetContextCommand(_botClient, message, cancellationToken),
            "/history"               => HistoryCommand(_botClient, message, cancellationToken),
            "/about"                 => AboutCommand(_botClient, message, cancellationToken),
            "/users"                 => UsersCommand(_botClient, message, cancellationToken),
            "/all_users"             => AllUsersCommand(_botClient, message, cancellationToken),
            "/allow"                 => AllowCommand(_botClient, message, cancellationToken),
            "/deny"                  => DenyCommand(_botClient, message, cancellationToken),
            "/toggle_voice"          => ToggleVoiceCommand(_botClient, message, cancellationToken),
            "/toggle_img_quality"    => ToggleImgQualityCommand(_botClient, message, cancellationToken),
            "/toggle_img_style"      => ToggleImgStyleCommand(_botClient, message, cancellationToken),
            "/image"                 => ImageCommand(_botClient, message, cancellationToken),
            _                        => TalkToModelCommand(_botClient, message, messageText, cancellationToken)
        };
        Message sentMessage = await action;
        _logger.LogInformation("The message was sent with id: {SentMessageId}", sentMessage.MessageId);
    }

    // ReSharper disable once UnusedMethodReturnValue.Local
    private async Task<Message> SendPhotoToVisionModel(ITelegramBotClient client, Message message, CancellationToken cancellationToken)
    {
        StoreUser? storeUser = GetStoreUser(message.From);
        if (storeUser == null)
        {
            return await client.SendTextMessageAsync(message.Chat.Id, "Error getting the user from the store.",
                cancellationToken: cancellationToken);
        }
        
        if (String.IsNullOrWhiteSpace(storeUser.ApiKey))
        {
            return await client.SendTextMessageAsync(message.Chat.Id, "Your api key is not set. Use '/key' command and set key.",
                cancellationToken: cancellationToken);
        }

        if (storeUser is { IsBlocked: true, IsAdministrator: false })
        {
            return await client.SendTextMessageAsync(message.Chat.Id, "You are blocked. Wait for some time and try again.",
                cancellationToken: cancellationToken);
        }

        if (message.Photo is not {Length: > 0})
        {
            return await client.SendTextMessageAsync(message.Chat.Id, 
                "Problem while saving a photo.",
                cancellationToken: cancellationToken);
        }

        PhotoSize photoSize = message.Photo[^1];
        
        File file = await client.GetFileAsync(photoSize.FileId, cancellationToken: cancellationToken);

        string path = Path.GetTempPath();
        string fileName = Path.Combine(path, file.FileId + ".jpg");

        if (file.FilePath != null)
        {
            await using FileStream stream = System.IO.File.OpenWrite(fileName);
            await client.DownloadFileAsync(file.FilePath, stream, cancellationToken);
        }
        else
        {
            return await client.SendTextMessageAsync(message.Chat.Id, 
                "Problem while saving a photo.",
                cancellationToken: cancellationToken);
        }

        if (System.IO.File.Exists(fileName) == false)
        {
            return await client.SendTextMessageAsync(message.Chat.Id, 
                "Problem while saving a photo.",
                cancellationToken: cancellationToken);
        }

        string base64Image = ImageToBase64(fileName);
        var payload = new
        {
            model = "gpt-4-vision-preview",
            messages = new[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "text", text = $"{message.Caption}" },
                        new 
                        { 
                            type = "image_url", 
                            image_url = new { url = $"data:image/jpeg;base64,{base64Image}" }
                        }
                    }
                }
            },
            max_tokens = 2500
        };
        
        // Convert payload to JSON string
        string jsonContent = JsonConvert.SerializeObject(payload);

        // Prepare the HTTP client
        HttpClient httpClient = new();
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", storeUser.ApiKey);
        httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        // The URL to call

        // Send the POST request
        HttpResponseMessage response = await httpClient.PostAsync(
            "https://api.openai.com/v1/chat/completions",
            new StringContent(jsonContent, Encoding.UTF8, "application/json"), cancellationToken);

        // Read the response as a string
        string responseString = await response.Content.ReadAsStringAsync(cancellationToken);
        
        JObject parsedJson = JObject.Parse(responseString);
        string content;
        try
        {
            content = (string) parsedJson["choices"]?[0]?["message"]?["content"]!;
        }
        catch (Exception)
        {
            return await client.SendTextMessageAsync(message.Chat.Id, 
                "Error while getting response about the image",
                replyToMessageId:  message.MessageId, 
                cancellationToken: cancellationToken);
        }
        
        if (String.IsNullOrEmpty(content))
        {
            return await client.SendTextMessageAsync(message.Chat.Id, 
                "Error while getting response about the image",
                replyToMessageId:  message.MessageId, 
                cancellationToken: cancellationToken);
        }
        
        if (storeUser.VoiceMode)
        {
            _logger.LogInformation("Voice mode is active.");
            _logger.LogInformation("Response length is: {length}", content.Length);

            string ttsAudioFilePath = await GetTtsAudio(content.Replace(Environment.NewLine, ""), storeUser.ApiKey);
            _logger.LogInformation("Path to tts audio message: {path}", ttsAudioFilePath);
            
            if (String.IsNullOrEmpty(ttsAudioFilePath) == false)
            {
                return await client.SendVoiceAsync(message.Chat.Id,
                    InputFile.FromStream(System.IO.File.OpenRead(ttsAudioFilePath)),
                    replyToMessageId: message.MessageId,
                    cancellationToken: cancellationToken);
            }
        }
            
        return await client.SendTextMessageAsync(message.Chat.Id, content,
            parseMode: ParseMode.Markdown,
            replyToMessageId:  message.MessageId, 
            cancellationToken: cancellationToken);
    }

    private static string ImageToBase64(string imagePath)
    {
        try
        {
            // Read the file as a byte array
            byte[] imageBytes = System.IO.File.ReadAllBytes(imagePath);

            // Convert byte array to base64 string
            string base64String = Convert.ToBase64String(imageBytes);

            return base64String;
        }
        catch (Exception)
        {
            return String.Empty;
        }
    }

    private async Task<Message> AllUsersCommand(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
    {
        StoreUser? storeUser = GetStoreUser(message.From);
        if (storeUser == null)
        {
            return await botClient.SendTextMessageAsync(message.Chat.Id, "Error getting the user from the store.",
                cancellationToken: cancellationToken);
        }
        
        if (storeUser.IsAdministrator == false)
        {
            return await botClient.SendTextMessageAsync(message.Chat.Id, 
                "This command might be executed only by the administrator.",
                cancellationToken: cancellationToken);
        }

        StoreUser[] users = _userRepository.GetAllUsers();
        
        if (users.Any() == false)
        {
            return await botClient.SendTextMessageAsync(message.Chat.Id, 
                "Users not found.",
                cancellationToken: cancellationToken);
        }

        StringBuilder builder = new();
        for (int i = 0; i < users.Length; i++)
        {
            StoreUser user = users[i];
            builder.AppendLine(
                $"{i + 1}) Id: {user.Id}; First name: {user.FirstName}; Last name: {user.LastName}; Username: {user.UserName}; Is blocked: {user.IsBlocked}");
        }
        
        return await botClient.SendTextMessageAsync(message.Chat.Id, 
            builder.ToString(),
            cancellationToken: cancellationToken);
    }

    private async Task<Message> ToggleImgStyleCommand(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
    {
        StoreUser? storeUser = GetStoreUser(message.From);
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

        storeUser.ImgStyle = storeUser.ImgStyle == ImgStyle.Natural ? ImgStyle.Vivid : ImgStyle.Natural;
        _userRepository.UpdateUser(storeUser);

        return await botClient.SendTextMessageAsync(message.Chat.Id, 
            @$"Vivid causes the model to lean towards generating hyper-real and dramatic images. Natural causes the model to produce more natural, less hyper-real looking images.

Current image style is: {storeUser.ImgStyle.ToString().ToLower()}",
            cancellationToken: cancellationToken);
    }

    private async Task<Message> ToggleImgQualityCommand(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
    {
        StoreUser? storeUser = GetStoreUser(message.From);
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

        storeUser.ImgQuality = storeUser.ImgQuality == ImgQuality.Standard ? ImgQuality.Hd : ImgQuality.Standard;
        _userRepository.UpdateUser(storeUser);

        return await botClient.SendTextMessageAsync(message.Chat.Id,
            @$"HD creates images with finer details and greater consistency across the image.

Current image quality is: {storeUser.ImgQuality.ToString().ToLower()}",
            cancellationToken: cancellationToken);
    }

    private async Task<Message> ImageCommand(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
    {
        StoreUser? storeUser = GetStoreUser(message.From);
        if (storeUser == null)
        {
            return await botClient.SendTextMessageAsync(message.Chat.Id, "Error getting the user from the store.",
                cancellationToken: cancellationToken);
        }
        
        string[] strings = message.Text!.Split(' ');
        if (strings.Length < 2)
        {
            return await botClient.SendTextMessageAsync(message.Chat.Id,
                "After the '/image' command you must input the prompt. Try again.",
                cancellationToken: cancellationToken);
        }

        string prompt = String.Join(' ', strings.Skip(1));
        if (String.IsNullOrWhiteSpace(prompt))
        {
            return await botClient.SendTextMessageAsync(message.Chat.Id,
                "After the '/image' command you must input the prompt. Try again.",
                cancellationToken: cancellationToken);
        }

        string? url = await GenerateImage(prompt, storeUser.ApiKey,
            storeUser.ImgStyle, storeUser.ImgQuality);
        if (String.IsNullOrEmpty(url))
        {
            return await botClient.SendTextMessageAsync(message.Chat.Id,
                "Error while generating an image. Try again.",
                cancellationToken: cancellationToken);
        }

        return await botClient.SendTextMessageAsync(message.Chat.Id,
            url,
            replyToMessageId: message.MessageId,
            cancellationToken: cancellationToken);
    }

    private async Task<Message> ToggleVoiceCommand(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
    {
        StoreUser? storeUser = GetStoreUser(message.From);
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

        storeUser.VoiceMode = !storeUser.VoiceMode;
        _userRepository.UpdateUser(storeUser);
        
        return await botClient.SendTextMessageAsync(message.Chat.Id, 
            $"Voice mode is: {(storeUser.VoiceMode ? "On" : "Off")}",
            cancellationToken: cancellationToken);
    }

    private async Task<string> GetTranscriptionTextFromVoiceMessage(Message message, ITelegramBotClient client, CancellationToken cancellationToken)
    {
        Voice? voice = message.Voice;
        if (voice == null)
        {
            return "";
        }

        StoreUser? storeUser = GetStoreUser(message.From);
        if (storeUser == null)
        {
            return "";
        }

        if (String.IsNullOrWhiteSpace(storeUser.ApiKey))
        {
            return "";
        }

        if (storeUser is { IsBlocked: true, IsAdministrator: false })
        {
            return "";
        }

        File file = await client.GetFileAsync(voice.FileId, cancellationToken: cancellationToken);

        string path = Path.GetTempPath();
        string oggFileName = Path.Combine(path, file.FileId + ".ogg");

        if (file.FilePath != null)
        {
            await using FileStream stream = System.IO.File.OpenWrite(oggFileName);
            await client.DownloadFileAsync(file.FilePath, stream, cancellationToken);
        }
        else
        {
            return "";
        }

        if (System.IO.File.Exists(oggFileName) == false)
        {
            return "";
        }

        string responseText = await CreateTranscriptionAsync(storeUser.ApiKey, oggFileName);
        
        return !String.IsNullOrEmpty(responseText) ? responseText : "";
    }

    private async Task<string> CreateTranscriptionAsync(string token, string filePath)
    {
        FileStream fileStream = new(filePath, FileMode.OpenOrCreate);

        try
        {
            OpenAiApi api = new(token);
            
            AudioFile audioFile = new()
            {
                File = fileStream,
                ContentType = "audio/ogg",
                Name = Path.GetFileName(filePath)
            };

            TranscriptionRequest transcriptionRequest = new()
            {
                File = audioFile,
                Model = OpenAiNg.Models.Model.Whisper_1,
                ResponseFormat = "json",
            };

            TranscriptionVerboseJsonResult? result =
                await api.Audio.CreateTranscriptionAsync(transcriptionRequest);
            
            return result != null ? result.Text : "";
        }
        catch (Exception e)
        {
            _logger.LogWarning("[{MethodName}] {Error}", nameof(CreateTranscriptionAsync), e.Message);
            return String.Empty;
        }
        finally
        {
            fileStream.Close();
        }
    }
    
    private async Task<Message> DenyCommand(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
    {
        var storeUser = GetStoreUser(message.From);
        if (storeUser == null)
        {
            return await botClient.SendTextMessageAsync(message.Chat.Id, "Error getting the user from the store.",
                cancellationToken: cancellationToken);
        }
        
        if (storeUser.IsAdministrator == false)
        {
            return await botClient.SendTextMessageAsync(message.Chat.Id, 
                "This command might be executed only by the administrator.",
                cancellationToken: cancellationToken);
        }
        
        var strings = message.Text!.Split(' ');
        if (strings.Length < 2)
        {
            return await botClient.SendTextMessageAsync(message.Chat.Id,
                "After '/deny' command you must input the user id.\nTry again.",
                cancellationToken: cancellationToken);
        }

        var userId = strings[1];
        if (String.IsNullOrWhiteSpace(userId))
        {
            return await botClient.SendTextMessageAsync(message.Chat.Id,
                "After '/deny' command you must input the user id.\nTry again.",
                cancellationToken: cancellationToken);
        }

        var parseResult = Int32.TryParse(userId, out var id);
        if (parseResult == false)
        {
            return await botClient.SendTextMessageAsync(message.Chat.Id,
                "After '/deny' command you must input the user id.\nTry again.",
                cancellationToken: cancellationToken);
        }

        var userById = _userRepository.GetUserById(id);
        if (userById == null)
        {
            return await botClient.SendTextMessageAsync(message.Chat.Id,
                $"Error getting user by id '{id}'. Try again.",
                cancellationToken: cancellationToken);
        }

        userById.IsBlocked = true;
        _userRepository.UpdateUser(userById);
        
        return await botClient.SendTextMessageAsync(message.Chat.Id,
                        $"User with id '{id}' was blocked.",
                        cancellationToken: cancellationToken);
    }

    private async Task<Message> AllowCommand(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
    {
        var storeUser = GetStoreUser(message.From);
        if (storeUser == null)
        {
            return await botClient.SendTextMessageAsync(message.Chat.Id, "Error getting the user from the store.",
                cancellationToken: cancellationToken);
        }
        
        if (storeUser.IsAdministrator == false)
        {
            return await botClient.SendTextMessageAsync(message.Chat.Id, 
                "This command might be executed only by the administrator.",
                cancellationToken: cancellationToken);
        }
        
        var strings = message.Text!.Split(' ');
        if (strings.Length < 2)
        {
            return await botClient.SendTextMessageAsync(message.Chat.Id,
                "After '/allow' command you must input the user id.\nTry again.",
                cancellationToken: cancellationToken);
        }

        var userId = strings[1];
        if (String.IsNullOrWhiteSpace(userId))
        {
            return await botClient.SendTextMessageAsync(message.Chat.Id,
                "After '/allow' command you must input the user id.\nTry again.",
                cancellationToken: cancellationToken);
        }

        var parseResult = Int32.TryParse(userId, out var id);
        if (parseResult == false)
        {
            return await botClient.SendTextMessageAsync(message.Chat.Id,
                "After '/allow' command you must input the user id.\nTry again.",
                cancellationToken: cancellationToken);
        }

        var userById = _userRepository.GetUserById(id);
        if (userById == null)
        {
            return await botClient.SendTextMessageAsync(message.Chat.Id,
                $"Error getting user by id '{id}'. Try again.",
                cancellationToken: cancellationToken);
        }

        userById.IsBlocked = false;
        _userRepository.UpdateUser(userById);
        
        return await botClient.SendTextMessageAsync(message.Chat.Id,
                        $"User with id '{id}' was unblocked.",
                        cancellationToken: cancellationToken);
    }

    private async Task<Message> UsersCommand(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
    {
        var storeUser = GetStoreUser(message.From);
        if (storeUser == null)
        {
            return await botClient.SendTextMessageAsync(message.Chat.Id, "Error getting the user from the store.",
                cancellationToken: cancellationToken);
        }
        
        if (storeUser.IsAdministrator == false)
        {
            return await botClient.SendTextMessageAsync(message.Chat.Id, 
                "This command might be executed only by the administrator.",
                cancellationToken: cancellationToken);
        }

        var users = _userRepository.GetAllUsers();
        var activeUsers = users
            .Where(user => String.IsNullOrWhiteSpace(user.ApiKey) == false)
            .ToArray();
        
        if (activeUsers.Any() == false)
        {
            return await botClient.SendTextMessageAsync(message.Chat.Id, 
                "Active users not found.",
                cancellationToken: cancellationToken);
        }

        var builder = new StringBuilder();
        for (var i = 0; i < activeUsers.Length; i++)
        {
            var user = activeUsers[i];
            builder.AppendLine(
                $"{i + 1}) Id: {user.Id}; First name: {user.FirstName}; Last name: {user.LastName}; Username: {user.UserName}; Is blocked: {user.IsBlocked}");
        }
        
        return await botClient.SendTextMessageAsync(message.Chat.Id, 
            builder.ToString(),
            cancellationToken: cancellationToken);
    }

    private async Task<Message> AboutCommand(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
    {
        return await botClient.SendTextMessageAsync(message.Chat.Id,
            "This bot allows you to talk with OpenAI GPT LLM's.",
            cancellationToken: cancellationToken);
    }

    private async Task<Message> HistoryCommand(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
    {
        return await botClient.SendTextMessageAsync(message.Chat.Id,
            "History feature is not implemented yet.",
            cancellationToken: cancellationToken);
    }

    private async Task<Message> ResetContextCommand(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
    {
        var storeUser = GetStoreUser(message.From);
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
        var storeUser = GetStoreUser(message.From);
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
        var storeUser = GetStoreUser(message.From);
        if (storeUser == null)
        {
            return await botClient.SendTextMessageAsync(message.Chat.Id, "Error getting the user from the store.",
                cancellationToken: cancellationToken);
        }
        
        var strings = message.Text!.Split(' ');
        if (strings.Length < 2)
        {
            // Defining buttons
            InlineKeyboardButton gpt3Button = new("GPT-3.5 Turbo") { CallbackData = "/model gpt3.5"};
            InlineKeyboardButton gpt4Button = new("GPT-4 Turbo") { CallbackData = "/model gpt4"};
     
            InlineKeyboardButton[] row1 = { gpt3Button, gpt4Button };
            
            // Buttons by rows
            InlineKeyboardButton[][] buttons = { row1 };
    
            // Keyboard
            InlineKeyboardMarkup inlineKeyboard = new(buttons);
            
            return await botClient.SendTextMessageAsync(message.Chat.Id,
                @"Select the model that you want to use.

1) GPT-3.5 models can understand and generate natural language or code. Our most capable and cost effective model in the GPT-3.5 family.
2) GPT-4 is a large multimodal model (accepting text inputs and emitting text outputs today, with image inputs coming in the future) that can solve difficult problems with greater accuracy than any of our previous models, thanks to its broader general knowledge and advanced reasoning capabilities.",
                replyMarkup: inlineKeyboard,
                cancellationToken: cancellationToken);
        }

        return await SetSelectedModel(botClient, message.Chat.Id, strings, storeUser, cancellationToken);
    }

    private async Task<Message> SetSelectedModel(ITelegramBotClient botClient, long chatId,
        string[] strings, StoreUser storeUser, CancellationToken cancellationToken)
    {
        var modelName = strings[1];
        if (String.IsNullOrWhiteSpace(modelName))
        {
            return await botClient.SendTextMessageAsync(chatId,
                "After '/model' command you must input the model name.\nModel name must be either: 'gpt3.5' or 'gpt4'.\nTry again.",
                cancellationToken: cancellationToken);
        }
        
        if (modelName.ToLower().Equals("gpt3.5") == false && modelName.ToLower().Equals("gpt4") == false)
        {
            return await botClient.SendTextMessageAsync(chatId,
                "After '/model' command you must input the model name.\nModel name must be either: 'gpt3.5' or 'gpt4'.\nTry again.",
                cancellationToken: cancellationToken);
        }

        var selectedModel = modelName.ToLower() switch
        {
            "gpt3.5" => Model.Gpt3,
            "gpt4" => Model.Gpt4,
            _ => Model.Gpt3
        };

        storeUser.Model = selectedModel;
        _userRepository.UpdateUser(storeUser);

        return await botClient.SendTextMessageAsync(
            chatId, 
            $"Model '{(selectedModel == Model.Gpt3 ? "GPT-3.5 Turbo" : "GPT-4 Turbo")}' was set.",
            cancellationToken: cancellationToken);
    }

    private async Task<Message> InfoCommand(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
    {
        var storeUser = GetStoreUser(message.From);
        if (storeUser == null)
        {
            return await botClient.SendTextMessageAsync(message.Chat.Id, "Error getting the user from the store.",
                cancellationToken: cancellationToken);
        }

        return await botClient.SendTextMessageAsync(message.Chat.Id,
            $"First name: `{storeUser.FirstName}`\n" +
            $"Last name: `{storeUser.LastName}`\n" +
            $"Username: `{storeUser.UserName}`\n" +
            $"OpenAI API key: `{storeUser.ApiKey}`\n" +
            $"Model: `{(storeUser.Model == Model.Gpt3 ? "GPT-3.5 Turbo" : "GPT-4 Turbo")}`\n" +
            $"Voice mode: `{(storeUser.VoiceMode ? "on" : "off")}`\n" +
            $"Image quality: `{storeUser.ImgQuality.ToString().ToLower()}`\n" +
            $"Image style: `{storeUser.ImgStyle.ToString().ToLower()}`\n" +
            $"Context prompt: `{storeUser.Conversation.FirstOrDefault(msg => msg.Role == Role.System)?.Msg ?? ""}`",
            parseMode: ParseMode.Markdown,
            cancellationToken: cancellationToken);
    }

    private async Task<Message> ResetConversationCommand(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
    {
        var storeUser = GetStoreUser(message.From);
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
        var storeUser = GetStoreUser(message.From);
        if (storeUser == null)
        {
            return await botClient.SendTextMessageAsync(message.Chat.Id, "Error getting the user from the store.",
                cancellationToken: cancellationToken);
        }

        var strings = message.Text!.Split(' ');
        if (strings.Length < 2)
        {
            return await botClient.SendTextMessageAsync(message.Chat.Id, "After '/key' command you must input your openAI API key. You can get your key here - https://platform.openai.com/account/api-keys. Try again.",
                cancellationToken: cancellationToken);
        }

        var apiKey = strings[1];
        if (String.IsNullOrWhiteSpace(apiKey))
        {
            return await botClient.SendTextMessageAsync(message.Chat.Id, "After '/key' command you must input your openAI API key. You can get your key here - https://platform.openai.com/account/api-keys. Try again.",
                cancellationToken: cancellationToken);
        }

        storeUser.ApiKey = apiKey;
        _userRepository.UpdateUser(storeUser);

        return await botClient.SendTextMessageAsync(message.Chat.Id, "OpenAI API key was set.",
            cancellationToken: cancellationToken);
    }

    // ReSharper disable once CognitiveComplexity
    private async Task<Message> TalkToModelCommand(ITelegramBotClient botClient, Message message, string messageText,
        CancellationToken cancellationToken)
    {
        StoreUser? storeUser = GetStoreUser(message.From);
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

        OpenAiApi api = new(storeUser.ApiKey);
        
        List<ChatMessage> chatMessages = new();

        Models.Message? systemMessage = storeUser.Conversation.FirstOrDefault(m => m.Role == Role.System);
        if (systemMessage != null && String.IsNullOrWhiteSpace(systemMessage.Msg) == false)
        {
            chatMessages.Add(new ChatMessage(ChatMessageRole.System, systemMessage.Msg));
        }

        foreach (Models.Message msg in storeUser.Conversation.Where(m => m.Role != Role.System))
        {
            chatMessages.Add(new ChatMessage(msg.Role == Role.Ai ? ChatMessageRole.Assistant : ChatMessageRole.User, msg.Msg));
        }
        
        chatMessages.Add(new ChatMessage(ChatMessageRole.User, messageText));

        ChatRequest request = new()
        {
            Model = storeUser.Model == Model.Gpt3 ? OpenAiNg.Models.Model.ChatGPTTurbo1106 : OpenAiNg.Models.Model.GPT4_1106_Preview,
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

        string? response = result.Choices?[0].Message?.Content;
        if (String.IsNullOrWhiteSpace(response))
        {
            return await botClient.SendTextMessageAsync(message.Chat.Id, "Response from model is empty. Try again.",
                cancellationToken: cancellationToken);
        }
        
        _logger.LogInformation("Received response message from model.");
        
        storeUser.Conversation.Add(new Models.Message(Role.User, messageText));
        storeUser.Conversation.Add(new Models.Message(Role.Ai, response));

        _userRepository.UpdateUser(storeUser);

        if (storeUser.VoiceMode)
        {
            _logger.LogInformation("Voice mode is active.");
            _logger.LogInformation("Response length is: {length}", response.Length);

            string ttsAudioFilePath = await GetTtsAudio(response.Replace(Environment.NewLine, ""), storeUser.ApiKey);
            _logger.LogInformation("Path to tts audio message: {path}", ttsAudioFilePath);
            
            if (String.IsNullOrEmpty(ttsAudioFilePath) == false)
            {
                return await botClient.SendVoiceAsync(message.Chat.Id,
                    InputFile.FromStream(System.IO.File.OpenRead(ttsAudioFilePath)),
                    replyToMessageId: message.MessageId,
                    cancellationToken: cancellationToken);
            }
        }
        
        return await botClient.SendTextMessageAsync(message.Chat.Id, response,
            parseMode: ParseMode.Markdown, replyToMessageId:  message.MessageId, 
            cancellationToken: cancellationToken);
    }
    
    private async Task<string?> GenerateImage(string prompt, string token, ImgStyle style,
        ImgQuality quality)
    {
        try
        {
            OpenAiApi api = new(token);

            ImageGenerationRequest request = new(prompt)
            {
                Model = OpenAiNg.Models.Model.Dalle3,
                Quality = quality == ImgQuality.Standard ? ImageQuality.Standard : ImageQuality.Hd,
                Style = style == ImgStyle.Natural ? ImageStyles.Natural : ImageStyles.Vivid,
                NumOfImages = 1,
                Size = ImageSize._1024,
            };

            ImageResult? imageResult = await api.ImageGenerations.CreateImageAsync(request);

            Data? imageData = imageResult?.Data?.FirstOrDefault();
            if (imageData == null)
            {
                return "";
            }

            string? url = imageData.Url;
            return !String.IsNullOrWhiteSpace(url) ? url : "";
        }
        catch (Exception e)
        {
            _logger.LogWarning("[{MethodName}] {Error}", nameof(GenerateImage), e.Message);
            return "";
        }
    }

    private async Task<string> GetTtsAudio(string text, string token)
    {
        try
        {
            OpenAiApi api = new(token);

            SpeechTtsResult? ttsResult = await api.Audio.CreateSpeechAsync(new SpeechRequest
            {
                Input = text,
                Model = OpenAiNg.Models.Model.TTS_1_HD,
                Voice = SpeechVoice.Nova,
                ResponseFormat = SpeechResponseFormat.Mp3,
            });

            if (ttsResult == null) return "";

            string path = Path.Combine(Path.GetTempPath(),
                Path.ChangeExtension(Path.GetTempFileName(), "mp3"));
            await ttsResult.SaveAndDispose(path);
                
            return System.IO.File.Exists(path) ? path : "";
        }
        catch (Exception e)
        {
            _logger.LogWarning("[{MethodName}] {Error}", nameof(GetTtsAudio), e.Message);
            return "";
        }
    }

    private StoreUser? GetStoreUser(User? messageFrom)
    {
        var user = messageFrom;
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
        var storeUser = GetStoreUser(message.From);
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
                       "/toggle_voice - enable/disable voice mode\n" +
                       "/toggle_img_quality - switch between standard or HD image quality\n" +
                       "/toggle_img_style - switch between vivid or natural image style\n" +
                       "/image - generate an image with help of DALL·E 3\n" +
                       "/usage - view the command list\n" +
                       "/info - show current settings\n" +
                       "/about - about this bot";
        
        if (storeUser.IsAdministrator)
        {
            usage = usage + Environment.NewLine + "---\n" +
                    "/allow - allow user\n" +
                    "/deny - deny user\n" +
                    "/users - show active users\n" +
                    "/all_users - show all users";
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