using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text;
using Humanizer;
using Microsoft.DeepDev;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OpenAiNg;
using OpenAiNg.Audio;
using OpenAiNg.Chat;
using OpenAiNg.ChatFunctions;
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
    private const int MaxMsgLength = 4000;
    private readonly ITelegramBotClient _botClient;
    private readonly ILogger<UpdateHandler> _logger;
    private readonly ApplicationSettings _appSettings;
    private readonly IUserRepository _userRepository;
    private readonly IYoutubeTextProcessor _youtubeTextProcessor;
    private readonly ITokenizer _tokenizer;
    private readonly string[] _allowedExtensions = { ".md", ".txt", ".cs", ".zip" };

    public UpdateHandler(ITelegramBotClient botClient, ILogger<UpdateHandler> logger, ApplicationSettings appSettings,
        IUserRepository userRepository, IYoutubeTextProcessor youtubeTextProcessor)
    {
        _botClient = botClient;
        _logger = logger;
        _appSettings = appSettings;
        _userRepository = userRepository;
        _youtubeTextProcessor = youtubeTextProcessor;
        _tokenizer = TokenizerBuilder.CreateByModelNameAsync("gpt-4").Result;
    }

    public async Task HandleUpdateAsync(ITelegramBotClient client, Update update, CancellationToken cancellationToken)
    {
        Task handler = update switch
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
        
        string? messageText = message.Text;
        if (messageText == null && messageType == MessageType.Voice)
        {
            messageText = await GetTranscriptionTextFromVoiceMessage(message, client, cancellationToken);
        }
        
        if (messageText == null && messageType == MessageType.Document)
        {
            messageText = await GetTextFromDocumentMessage(message, client, cancellationToken);
        }

        if (String.IsNullOrEmpty(messageText))
        {
            await client.SendTextMessageAsync(message.Chat.Id,
                "Your message was empty. Try again.",
                cancellationToken: cancellationToken);
            _logger.LogWarning("[{MethodName}] Message is empty. Return.", nameof(BotOnMessageReceived));
            return;
        }

        Task<Message> action = messageText.Split(' ')[0] switch
        {
            "/usage"                 => UsageCommand(_botClient, message, cancellationToken),
            "/key"                   => SetKeyCommand(_botClient, message, cancellationToken),
            "/key_claude"            => SetKeyClaudeCommand(_botClient, message, cancellationToken),
            "/reset"                 => ResetConversationCommand(_botClient, message, cancellationToken),
            "/info"                  => InfoCommand(_botClient, message, cancellationToken),
            "/model"                 => ModelCommand(_botClient, message, cancellationToken),
            "/context"               => ContextCommand(_botClient, message, cancellationToken),
            "/contact"               => ContactCommand(_botClient, message, cancellationToken),
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
            "/toggle_anew_mode"      => ToggleAnewMode(_botClient, message, cancellationToken),
            "/image"                 => ImageCommand(_botClient, message, cancellationToken),
            "/append"                => AppendCommand(_botClient, message, cancellationToken),
            _                        => TalkToModelCommand(_botClient, message, messageText, cancellationToken)
        };
        Message sentMessage = await action;
        _logger.LogInformation("The message was sent with id: {SentMessageId}", sentMessage.MessageId);
    }

    private async Task<Message> ContactCommand(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
    {
        StoreUser? storeUser = GetStoreUser(message.From);
        if (storeUser == null)
        {
            return await botClient.SendTextMessageAsync(message.Chat.Id, "Error getting the user from the store.",
                cancellationToken: cancellationToken);
        }
        
        if (storeUser is {IsAdministrator: false, IsBlocked: true})
        {
            return await botClient.SendTextMessageAsync(message.Chat.Id,
                "You are blocked. Wait for some time and try again.", cancellationToken: cancellationToken);
        }
        
        string[] strings = message.Text!.Split(' ');
        if (strings.Length < 2)
        {
            return await botClient.SendTextMessageAsync(message.Chat.Id,
                "After the '/contact' command you must input the message to admin. Try again.",
                cancellationToken: cancellationToken);
        }

        string msg = String.Join(' ', strings.Skip(1));
        if (String.IsNullOrWhiteSpace(msg))
        {
            return await botClient.SendTextMessageAsync(message.Chat.Id,
                "After the '/contact' command you must input the message to admin. Try again.",
                cancellationToken: cancellationToken);
        }
        
        await botClient.SendTextMessageAsync(chatId: _appSettings.AdminId,
            text: $"Message from '{storeUser}':\n\n'{msg}'",
            cancellationToken: cancellationToken);
        
        return await botClient.SendTextMessageAsync(
            message.Chat.Id, 
            "Your message was sent to the admin.",
            cancellationToken: cancellationToken);
    }

    private async Task<Message> SetKeyClaudeCommand(ITelegramBotClient botClient, Message message,
        CancellationToken cancellationToken)
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
                "After '/key_claude' command you must input your Anthropic Claude API key. Try again.",
                cancellationToken: cancellationToken);
        }

        string apiKey = strings[1];
        if (String.IsNullOrWhiteSpace(apiKey))
        {
            return await botClient.SendTextMessageAsync(message.Chat.Id,
                "After '/key_claude' command you must input your Anthropic Claude API key. Try again.",
                cancellationToken: cancellationToken);
        }

        storeUser.ClaudeApiKey = apiKey;
        _userRepository.UpdateUser(storeUser);

        return await botClient.SendTextMessageAsync(message.Chat.Id, "Anthropic Claude API key was set.",
            cancellationToken: cancellationToken);
    }

    private async Task<Message> ToggleAnewMode(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
    {
        StoreUser? storeUser = GetStoreUser(message.From);
        if (await ValidateUser(storeUser, botClient, message.Chat.Id) == false)
        {
            return await botClient.SendTextMessageAsync(message.Chat.Id,
                "Error: User validation failed.",
                cancellationToken: cancellationToken);
        }

        storeUser!.AnewMode = !storeUser.AnewMode;
        _userRepository.UpdateUser(storeUser);
        
        return await botClient.SendTextMessageAsync(message.Chat.Id, 
            $"Anew mode is: {(storeUser.AnewMode ? "On" : "Off")}",
            cancellationToken: cancellationToken);
    }

    private async Task<string?> GetTextFromDocumentMessage(Message message, ITelegramBotClient client, CancellationToken cancellationToken)
    {
        try
        {
            StoreUser? storeUser = GetStoreUser(message.From);
            if (await ValidateUser(storeUser, client, message.Chat.Id) == false)
            {
                await client.SendTextMessageAsync(message.Chat.Id,
                    "Error: User validation failed.",
                    cancellationToken: cancellationToken);
                return "";
            }

            Document? document = message.Document;
            if (document == null)
            {
                return "";
            }
        
            if (String.IsNullOrEmpty(document.FileName))
            {
                return "";
            }

            string extension = Path.GetExtension(document.FileName);
            if (_allowedExtensions.Contains(extension) == false)
            {
                await client.SendTextMessageAsync(message.Chat.Id, 
                    "Bot supports '*.txt', '*.md', '*.zip' or '*.cs' formats.",
                    cancellationToken: cancellationToken);
                return "";
            }

            File file = await client.GetFileAsync(document.FileId, cancellationToken: cancellationToken);

            string path = Path.GetTempPath();
            string fullDocumentFileName = Path.Combine(path, document.FileName);

            if (file.FilePath != null)
            {
                await using FileStream stream = System.IO.File.OpenWrite(fullDocumentFileName);
                await client.DownloadFileAsync(file.FilePath, stream, cancellationToken);
            }
            else
            {
                return "";
            }

            if (System.IO.File.Exists(fullDocumentFileName) == false)
            {
                return "";
            }

            string text = extension == ".zip"
                ? await GetTextFromFilesInsideZipArchive(fullDocumentFileName, cancellationToken)
                : await System.IO.File.ReadAllTextAsync(fullDocumentFileName, cancellationToken);

            if (String.IsNullOrEmpty(message.Caption) == false)
            {
                text += $"\n{message.Caption}";
            }
            
            return text;
        }
        catch (Exception e)
        {
            _logger.LogWarning("[{MethodName}] {Error}", nameof(GetTextFromDocumentMessage), e.Message);
            return "";
        }
    }
    
    public async Task<string> GetTextFromFilesInsideZipArchive(string fullDocumentFileName, CancellationToken cancellationToken)
    {
        (List<string> extractedFiles, string errorMsg) = UnzipToTempFolder(fullDocumentFileName);
        if (String.IsNullOrEmpty(errorMsg))
        {
            return await GetTextFromExtractedFiles(extractedFiles, cancellationToken);
        }

        _logger.LogWarning("[{MethodName}] {Error}", nameof(GetTextFromFilesInsideZipArchive), errorMsg);
        return "";
    }
    
    private async Task<string> GetTextFromExtractedFiles(List<string> extractedFiles, CancellationToken cancellationToken)
    {
        string[] allowedPaths = extractedFiles.Where(p =>
                _allowedExtensions.Contains(Path.GetExtension(p).ToLowerInvariant()))
            .ToArray();

        StringBuilder builder = new();
        foreach (string allowedPath in allowedPaths)
        {
            builder.AppendLine(await System.IO.File.ReadAllTextAsync(allowedPath, cancellationToken));
        }

        return builder.ToString();
    }
    
    private (List<string>, string) UnzipToTempFolder(string zipFilePath)
    {
        if (!System.IO.File.Exists(zipFilePath))
        {
            return (Array.Empty<string>().ToList(), "Zip file does not exist: " + zipFilePath);
        }

        string tempFolder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempFolder);

        List<string> extractedFiles = new();
        
        try
        {
            using (ZipArchive archive = ZipFile.OpenRead(zipFilePath))
            {
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    string destinationPath = Path.Combine(tempFolder, entry.FullName);
                    entry.ExtractToFile(destinationPath, overwrite: true); 
                    extractedFiles.Add(destinationPath);
                }
            }

            return (extractedFiles, "");
        }
        catch (Exception ex)
        {
            // Cleanup if there was an error
            TryDeleteDirectory(tempFolder);
            // Return an error message along with an empty list of files
            return (new List<string>(), ex.Message);
        }
    }

    private void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, true);
        }
        catch (Exception e)
        {
            _logger.LogError("[{MethodName}] {Error}", nameof(TryDeleteDirectory), e.Message);
        }
    }

    private async Task<Message> AppendCommand(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
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
                "After the '/append' command you must input the part of your (potentially huge) prompt. Try again.",
                cancellationToken: cancellationToken);
        }

        string contextPrompt = String.Join(' ', strings.Skip(1));
        if (String.IsNullOrWhiteSpace(contextPrompt))
        {
            return await botClient.SendTextMessageAsync(message.Chat.Id,
                "After the '/append' command you must input the part of your (potentially huge) prompt. Try again.",
                cancellationToken: cancellationToken);
        }

        Models.Message? lastUserMessage = GetLastUserMessage(storeUser);
        
        if (lastUserMessage != null)
        {
            lastUserMessage.Msg += contextPrompt;
        }
        else
        {
            storeUser.Conversation.Add(new SgptBot.Models.Message(Role.User, contextPrompt, DateOnly.FromDateTime(DateTime.Today)));
        }
        
        int tokenCount = GetTokenCount(GetLastUserMessage(storeUser)!.Msg);

        _userRepository.UpdateUser(storeUser);
        
        return await botClient.SendTextMessageAsync(
            message.Chat.Id, 
            $"Text was appended to the last message. Token count for the last message: {tokenCount}.",
            cancellationToken: cancellationToken);
    }

    private int GetTokenCount(string text)
    {
        return _tokenizer.Encode(text, Array.Empty<string>()).Count;
    }

    private static Models.Message? GetLastUserMessage(StoreUser storeUser)
    {
        SgptBot.Models.Message[] userMessages = storeUser.Conversation.Where(msg => msg.Role == Role.User).ToArray();
        SgptBot.Models.Message? lastUserMessage = userMessages.LastOrDefault();
        return lastUserMessage;
    }
    
    private static async Task<bool> ValidateUser(StoreUser? user, ITelegramBotClient client, long chatId)
    {
        if (user == null)
        {
            await client.SendTextMessageAsync(chatId, "Error getting the user from the store.");
            return false;
        }

        if (user.Model is Model.Gpt3 or Model.Gpt4)
        {
            if (String.IsNullOrWhiteSpace(user.ApiKey))
            {
                await client.SendTextMessageAsync(chatId, "Your OpenAI API key is not set. Use '/key' command and set key.");
                return false;
            }
        }
        else
        {
            if (String.IsNullOrWhiteSpace(user.ClaudeApiKey))
            {
                await client.SendTextMessageAsync(chatId, "Your Claude API key is not set. Use '/key_claude' command and set key.");
                return false;
            }
        }

        if (user is {IsAdministrator: false, IsBlocked: true})
        {
            await client.SendTextMessageAsync(chatId, "You are blocked. Wait for some time and try again.");
            return false;
        }

        return true;
    }

    private async Task SendPhotoToVisionModel(ITelegramBotClient client, Message message,
        CancellationToken cancellationToken)
    {
        StoreUser? storeUser = GetStoreUser(message.From);
        if (await ValidateUser(storeUser, client, message.Chat.Id) == false)
        {
            await client.SendTextMessageAsync(message.Chat.Id,
                "Error: User validation failed.",
                cancellationToken: cancellationToken);
            return;
        }

        if (message.Photo is not {Length: > 0})
        {
            await client.SendTextMessageAsync(message.Chat.Id, 
                "Problem while saving a photo.",
                cancellationToken: cancellationToken);
            return;
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
            await client.SendTextMessageAsync(message.Chat.Id, 
                "Problem while saving a photo.",
                cancellationToken: cancellationToken);
            return;
        }

        if (System.IO.File.Exists(fileName) == false)
        {
            await client.SendTextMessageAsync(message.Chat.Id, 
                "Problem while saving a photo.",
                cancellationToken: cancellationToken);
            return;
        }

        string base64Image = ImageToBase64(fileName);
        object payload = GetPayload(base64Image, message.Caption);
        
        // Convert payload to JSON string
        string jsonContent = JsonConvert.SerializeObject(payload);

        // Prepare the HTTP client
        HttpClient httpClient = new();
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", storeUser!.ApiKey);
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
            await client.SendTextMessageAsync(message.Chat.Id, 
                "Error while getting response about the image",
                replyToMessageId:  message.MessageId, 
                cancellationToken: cancellationToken);
            return;
        }
        
        if (String.IsNullOrEmpty(content))
        {
            await client.SendTextMessageAsync(message.Chat.Id, 
                "Error while getting response about the image",
                replyToMessageId:  message.MessageId, 
                cancellationToken: cancellationToken);
            return;
        }

        if (!storeUser.VoiceMode)
        {
            await client.SendTextMessageAsync(message.Chat.Id, content,
                parseMode: ParseMode.Markdown,
                replyToMessageId: message.MessageId,
                cancellationToken: cancellationToken);
            return;
        }

        _logger.LogInformation("Voice mode is active.");
        _logger.LogInformation("Response length is: {length}", content.Length);

        string ttsAudioFilePath = await GetTtsAudio(content.Replace(Environment.NewLine, ""), storeUser.ApiKey);
        _logger.LogInformation("Path to tts audio message: {path}", ttsAudioFilePath);
            
        if (String.IsNullOrEmpty(ttsAudioFilePath) == false)
        {
            await client.SendVoiceAsync(message.Chat.Id,
                InputFile.FromStream(System.IO.File.OpenRead(ttsAudioFilePath)),
                replyToMessageId: message.MessageId,
                cancellationToken: cancellationToken);
            return;
        }

        await client.SendTextMessageAsync(message.Chat.Id, content,
            parseMode: ParseMode.Markdown,
            replyToMessageId:  message.MessageId, 
            cancellationToken: cancellationToken);
    }

    private static object GetPayload(string base64Image, string? caption)
    {
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
                        new { type = "text", text = $"{caption}" },
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
        return payload;
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

    private async Task<Message> AllUsersCommand(ITelegramBotClient client, Message message, CancellationToken cancellationToken)
    {
        StoreUser? storeUser = GetStoreUser(message.From);
        if (storeUser == null)
        {
            return await client.SendTextMessageAsync(message.Chat.Id, "Error getting the user from the store.",
                cancellationToken: cancellationToken);
        }
        
        if (storeUser.IsAdministrator == false)
        {
            return await client.SendTextMessageAsync(message.Chat.Id, 
                "This command might be executed only by the administrator.",
                cancellationToken: cancellationToken);
        }

        StoreUser[] users = _userRepository.GetAllUsers().OrderByDescending(user => user.ActivityTime).ToArray();
        
        if (users.Any() == false)
        {
            return await client.SendTextMessageAsync(message.Chat.Id, 
                "Users not found.",
                cancellationToken: cancellationToken);
        }

        StringBuilder builder = new();
        for (int i = 0; i < users.Length; i++)
        {
            StoreUser user = users[i];
            string lastActivityMessage = GetLastActivityMessage(user.ActivityTime);
            builder.AppendLine(
                $"{i + 1}) Id: {user.Id}; First name: {user.FirstName}; Last name: {user.LastName}; Username: {user.UserName}; Is blocked: {user.IsBlocked}; Last activity: {lastActivityMessage} ago;");
        }
        
        return await SendBotResponseDependingOnMsgLength(msg: builder.ToString(),
            client: client,
            chatId: message.Chat.Id,
            userId: storeUser.Id, 
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
        StoreUser[] activeUsers = users
            .Where(user => String.IsNullOrWhiteSpace(user.ApiKey) == false ||
                           String.IsNullOrWhiteSpace(user.ClaudeApiKey) == false)
            .OrderByDescending(user => user.ActivityTime)
            .ToArray();
        
        if (activeUsers.Length == 0)
        {
            return await botClient.SendTextMessageAsync(message.Chat.Id, 
                "Active users not found.",
                cancellationToken: cancellationToken);
        }

        StringBuilder builder = new();
        for (int i = 0; i < activeUsers.Length; i++)
        {
            StoreUser user = activeUsers[i];
            string lastActivityMessage = GetLastActivityMessage(user.ActivityTime);
            builder.AppendLine(
                $"{i + 1}) Id: {user.Id}; First name: {user.FirstName}; Last name: {user.LastName}; Username: {user.UserName}; Is blocked: {user.IsBlocked}; Last activity: {lastActivityMessage} ago;");
        }
        
        return await botClient.SendTextMessageAsync(message.Chat.Id, 
            builder.ToString(),
            cancellationToken: cancellationToken);
    }
    
    private static string GetLastActivityMessage(DateTime lastActivity)
    {
        DateTime current = DateTime.Now;
        TimeSpan timeSinceLastActivity = current - lastActivity;
        string humanizedTimeSinceLastActivity = timeSinceLastActivity.Humanize();
        return humanizedTimeSinceLastActivity;
    }

    private async Task<Message> AboutCommand(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
    {
        return await botClient.SendTextMessageAsync(message.Chat.Id,
            "This bot allows you to talk with OpenAI GPT LLM's.",
            cancellationToken: cancellationToken);
    }

    private async Task<Message> HistoryCommand(ITelegramBotClient client, Message message, CancellationToken cancellationToken)
    {
        StoreUser? storeUser = GetStoreUser(message.From);
        if (storeUser == null)
        {
            return await client.SendTextMessageAsync(message.Chat.Id, "Error getting the user from the store.",
                cancellationToken: cancellationToken);
        }
        
        if (storeUser.History.Count == 0)
        {
            return await client.SendTextMessageAsync(message.Chat.Id,
                "History is empty.",
                cancellationToken: cancellationToken);
        }
        
        string[] strings = message.Text!.Split(' ');
        if (strings.Length < 2)
        {
            return await client.SendTextMessageAsync(message.Chat.Id,
                "After the '/history' command you must input the desired date. Try again.",
                cancellationToken: cancellationToken);
        }

        string datePrompt = String.Join(' ', strings.Skip(1));
        if (String.IsNullOrWhiteSpace(datePrompt))
        {
            return await client.SendTextMessageAsync(message.Chat.Id,
                "After the '/history' command you must input the desired date. Try again.",
                cancellationToken: cancellationToken);
        }

        bool parseResult = DateOnly.TryParse(datePrompt, out DateOnly date);
        if (parseResult == false)
        {
            const string dontUnderstandMsg = "I do not understand your date format. Try again.";
            if (String.IsNullOrWhiteSpace(storeUser.ApiKey))
            {
                return await client.SendTextMessageAsync(message.Chat.Id,
                    dontUnderstandMsg,
                    cancellationToken: cancellationToken);
            }

            GetExtractDateFunctionResult? extractDateFunctionResult =
                await GetDateFunctionCallResult(storeUser, datePrompt);
            if (extractDateFunctionResult == null)
            {
                return await client.SendTextMessageAsync(message.Chat.Id,
                    dontUnderstandMsg,
                    cancellationToken: cancellationToken);
            }

            if (extractDateFunctionResult.Status == false)
            {
                return await client.SendTextMessageAsync(message.Chat.Id,
                    dontUnderstandMsg,
                    cancellationToken: cancellationToken);
            }

            bool tryParseResult = DateOnly.TryParse(extractDateFunctionResult.Date, out date);
            if (tryParseResult == false)
            {
                return await client.SendTextMessageAsync(message.Chat.Id,
                    dontUnderstandMsg,
                    cancellationToken: cancellationToken);
            }
        }

        SgptBot.Models.Message[] messagesFromHistory = storeUser.History.Where(msg => msg.Date == date).ToArray();
        if (messagesFromHistory.Length == 0)
        {
            return await client.SendTextMessageAsync(message.Chat.Id,
                $"I don't have history for '{date}' date. Try again.",
                cancellationToken: cancellationToken);
        }

        return await SendDocumentResponseAsync(text: GetHistory(messagesFromHistory),
            botClient: client,
            chatId: message.Chat.Id,
            userId: storeUser.Id,
            cancellationToken: cancellationToken,
            caption: $"This is your history from '{date}' date.");
    }

    private static string GetHistory(Models.Message[] messagesFromHistory)
    {
        StringBuilder builder = new();
        foreach (SgptBot.Models.Message msg in messagesFromHistory)
        {
            builder.AppendLine($"{msg.Role}:");
            builder.AppendLine(msg.Msg);
            builder.AppendLine();
        }

        string history = builder.ToString();
        return history;
    }

    private async Task<GetExtractDateFunctionResult?> GetDateFunctionCallResult(StoreUser storeUser, string datePrompt)
    {
        try
        {
            OpenAiApi api = new(storeUser.ApiKey);

            JObject jObject = GetJObjectForDateExtractionFunctionCall();

            ChatRequest request = new()
            {
                Model = OpenAiNg.Models.Model.ChatGPTTurbo1106,
                Messages = new List<ChatMessage>(1)
                {
                    new(ChatMessageRole.User,
                        $"Get the date from the user message '{datePrompt}' in json format. Take into account that Today's date is '{DateOnly.FromDateTime(DateTime.Today)}'.")
                },
                ResponseFormat = new ChatRequestResponseFormats {Type = ChatRequestResponseFormatTypes.Json},
                Tools = new List<Tool>
                {
                    new(new ToolFunction(name: "GetDateFromUserPrompt",
                        description: "Function must extract a date from user prompt",
                        parameters: jObject))
                }
            };

            ChatResult response = await api.Chat.CreateChatCompletionAsync(request);
            GetExtractDateFunctionResult? result = JsonConvert.DeserializeObject<GetExtractDateFunctionResult>(
                response.Choices?[0]
                    .Message?.ToolCalls?[0].FunctionCall.Arguments ?? "");

            return result;
        }
        catch (Exception e)
        {
            _logger.LogError("[{MethodName}] {Error}", nameof(GetDateFunctionCallResult), e.Message);
            return null;
        }
    }

    private static JObject GetJObjectForDateExtractionFunctionCall()
    {
        JObject jObject = new()
        {
            {
                "type", "object"
            },
            {
                "properties", new JObject
                {
                    {
                        "date", new JObject
                        {
                            {"type", "string"},
                            {
                                "description",
                                "Date in default for C# DateOnly format. Or default date if it was unsuccessful."
                            }
                        }
                    },
                    {
                        "status", new JObject
                        {
                            {"type", "boolean"},
                            {
                                "description",
                                "Date extraction status. True - if it was successful and false - if it doesn't."
                            }
                        }
                    }
                }
            },
            {
                "required", new JArray("date", "status")
            },
        };
        return jObject;
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

        var newSystemMessage = new SgptBot.Models.Message(Role.System, contextPrompt, DateOnly.FromDateTime(DateTime.Today));
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
        StoreUser? storeUser = GetStoreUser(message.From);
        if (storeUser == null)
        {
            return await botClient.SendTextMessageAsync(message.Chat.Id, "Error getting the user from the store.",
                cancellationToken: cancellationToken);
        }
        
        string[] strings = message.Text!.Split(' ');
        if (strings.Length >= 2)
        {
            return await SetSelectedModel(botClient, message.Chat.Id, strings, storeUser, cancellationToken);
        }

        // Defining buttons
        InlineKeyboardButton gpt3Button = new("OpenAI GPT-3.5 Turbo") { CallbackData = "/model gpt3.5"};
        InlineKeyboardButton gpt4Button = new("OpenAI GPT-4 Turbo") { CallbackData = "/model gpt4"};
        InlineKeyboardButton claudeButton = new("Anthropic Claude 2.1") { CallbackData = "/model claude21"};
     
        InlineKeyboardButton[] row1 = { gpt3Button };
        InlineKeyboardButton[] row2 = { gpt4Button };
        InlineKeyboardButton[] row3 = { claudeButton };
            
        // Buttons by rows
        InlineKeyboardButton[][] buttons = { row1, row2, row3 };
    
        // Keyboard
        InlineKeyboardMarkup inlineKeyboard = new(buttons);
            
        return await botClient.SendTextMessageAsync(message.Chat.Id,
            """
            Select the model that you want to use.

            1) GPT-3.5 models can understand and generate natural language or code. Our most capable and cost effective model in the GPT-3.5 family.
            2) GPT-4 is a large multimodal model (accepting text inputs and emitting text outputs today, with image inputs coming in the future) that can solve difficult problems with greater accuracy than any of our previous models, thanks to its broader general knowledge and advanced reasoning capabilities.
            3) Claude 2 is a language model that can generate various types of text-based outputs from user's prompts. You can use Claude 2 for e-commerce tasks, creating email templates and generating code in popular programming languages.
            """,
            replyMarkup: inlineKeyboard,
            cancellationToken: cancellationToken);

    }

    private async Task<Message> SetSelectedModel(ITelegramBotClient botClient, long chatId,
        string[] strings, StoreUser storeUser, CancellationToken cancellationToken)
    {
        string modelName = strings[1];
        if (String.IsNullOrWhiteSpace(modelName))
        {
            return await botClient.SendTextMessageAsync(chatId,
                "After '/model' command you must input the model name.\nModel name must be either: 'gpt3.5' or 'gpt4'.\nTry again.",
                cancellationToken: cancellationToken);
        }
        
        if (modelName.ToLower().Equals("gpt3.5") == false && modelName.ToLower().Equals("gpt4") == false && 
            modelName.ToLower().Equals("claude21") == false)
        {
            return await botClient.SendTextMessageAsync(chatId,
                "After '/model' command you must input the model name.\nModel name must be either: 'gpt3.5', 'gpt4' or 'claude21'.\nTry again.",
                cancellationToken: cancellationToken);
        }

        Model selectedModel = modelName.ToLower() switch
        {
            "gpt3.5" => Model.Gpt3,
            "gpt4" => Model.Gpt4,
            "claude21" => Model.Claude21,
            _ => Model.Gpt3
        };

        storeUser.Model = selectedModel;
        _userRepository.UpdateUser(storeUser);

#pragma warning disable CS8524 // The switch expression does not handle some values of its input type (it is not exhaustive) involving an unnamed enum value.
        string mName = selectedModel switch
#pragma warning restore CS8524 // The switch expression does not handle some values of its input type (it is not exhaustive) involving an unnamed enum value.
        {
            Model.Gpt3 => "GPT-3.5 Turbo",
            Model.Gpt4 => "GPT-4 Turbo",
            Model.Claude21 => "Claude 2.1",
        };
        
        return await botClient.SendTextMessageAsync(
            chatId, 
            $"Model '{mName}' was set.",
            cancellationToken: cancellationToken);
    }

    private async Task<Message> InfoCommand(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
    {
        StoreUser? storeUser = GetStoreUser(message.From);
        if (storeUser == null)
        {
            return await botClient.SendTextMessageAsync(message.Chat.Id, "Error getting the user from the store.",
                cancellationToken: cancellationToken);
        }

        StringBuilder builder = new();
        foreach (SgptBot.Models.Message msg in storeUser.Conversation)
        {
            builder.Append(msg.Msg);
        }

        int tokenCount = GetTokenCount(builder.ToString());
        
#pragma warning disable CS8524 // The switch expression does not handle some values of its input type (it is not exhaustive) involving an unnamed enum value.
        string mName = storeUser.Model switch
#pragma warning restore CS8524 // The switch expression does not handle some values of its input type (it is not exhaustive) involving an unnamed enum value.
        {
            Model.Gpt3 => "GPT-3.5 Turbo",
            Model.Gpt4 => "GPT-4 Turbo",
            Model.Claude21 => "Claude 2.1",
        };

        return await botClient.SendTextMessageAsync(message.Chat.Id,
            $"First name: `{storeUser.FirstName}`\n" +
            $"Last name: `{storeUser.LastName}`\n" +
            $"Username: `{storeUser.UserName}`\n" +
            $"OpenAI API key: `{storeUser.ApiKey}`\n" +
            $"Claude API key: `{storeUser.ClaudeApiKey}`\n" +
            $"Model: `{mName}`\n" +
            $"Voice mode: `{(storeUser.VoiceMode ? "on" : "off")}`\n" +
            $"Anew mode: `{(storeUser.AnewMode ? "on" : "off")}`\n" +
            $"Image quality: `{storeUser.ImgQuality.ToString().ToLower()}`\n" +
            $"Image style: `{storeUser.ImgStyle.ToString().ToLower()}`\n" +
            $"Current context window size (number of tokens): `{tokenCount}`\n" +
            $"Context prompt: `{storeUser.Conversation.FirstOrDefault(msg => msg.Role == Role.System)?.Msg ?? ""}`",
            parseMode: ParseMode.Markdown,
            cancellationToken: cancellationToken);
    }

    private async Task<Message> ResetConversationCommand(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
    {
        StoreUser? storeUser = GetStoreUser(message.From);
        if (storeUser == null)
        {
            return await botClient.SendTextMessageAsync(message.Chat.Id, "Error getting the user from the store.",
                cancellationToken: cancellationToken);
        }

        Models.Message[] currentContextWindowMessages =
            storeUser.Conversation.Where(m => m.Role != Role.System).ToArray();
        
        storeUser.History.AddRange(currentContextWindowMessages);
        
        foreach (Models.Message msg in currentContextWindowMessages)
        {
            bool removeStatus = storeUser.Conversation.Remove(msg);
            if (removeStatus == false)
            {
                return await botClient.SendTextMessageAsync(message.Chat.Id, "Error while removing the conversation message.",
                    cancellationToken: cancellationToken);
            }
        }
        
        _userRepository.UpdateUser(storeUser);
        
        StringBuilder builder = new();
        foreach (SgptBot.Models.Message msg in storeUser.Conversation)
        {
            builder.Append(msg.Msg);
        }
        
        int tokenCount = GetTokenCount(builder.ToString());
        
        return await botClient.SendTextMessageAsync(message.Chat.Id,
            $"Current conversation was reset.\nCurrent context window size: `{tokenCount}` tokens.",
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

    private async Task<Message> TalkToModelCommand(ITelegramBotClient botClient, Message message, string messageText,
        CancellationToken cancellationToken)
    {
        StoreUser? storeUser = GetStoreUser(message.From);
        if (await ValidateUser(storeUser, botClient, message.Chat.Id) == false)
        {
            return await botClient.SendTextMessageAsync(message.Chat.Id,
                "Error: User validation failed.",
                cancellationToken: cancellationToken);
        }

        string? response;
        if (storeUser!.Model == Model.Gpt3 || storeUser.Model == Model.Gpt4)
        {
            response = await GetResponseFromOpenAiModel(botClient, storeUser, message, messageText, cancellationToken);
        }
        else
        {
            response = await GetResponseFromAnthropicModel(botClient, storeUser, message, messageText, cancellationToken);
        }
        
        if (String.IsNullOrWhiteSpace(response))
        {
            return await botClient.SendTextMessageAsync(message.Chat.Id, "Response from model is empty. Try again.",
                cancellationToken: cancellationToken);
        }
        
        _logger.LogInformation("Received response message from model.");
        
        storeUser.Conversation.Add(new Models.Message(Role.User, messageText, DateOnly.FromDateTime(DateTime.Today)));
        storeUser.Conversation.Add(new Models.Message(Role.Ai, response, DateOnly.FromDateTime(DateTime.Today)));

        if (storeUser.AnewMode == false)
        {
            _userRepository.UpdateUser(storeUser);
        }

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
        
        return await SendBotResponseDependingOnMsgLength(response, botClient, message.Chat.Id, storeUser.Id, cancellationToken, message.MessageId, ParseMode.Markdown);
    }

    private async Task<string> GetResponseFromAnthropicModel(ITelegramBotClient client, StoreUser storeUser,
        Message message, string messageText, CancellationToken cancellationToken)
    {
        string prompt = PreparePromptForClaude(storeUser, messageText);

        string response = await PostToClaudeApiAsync(prompt, storeUser.ClaudeApiKey, client, message, storeUser,
            cancellationToken);
        return response;
    }

    private static string PreparePromptForClaude(StoreUser storeUser, string messageText)
    {
        StringBuilder builder = new();

        SgptBot.Models.Message? systemMsg = storeUser.Conversation.FirstOrDefault(msg => msg.Role == Role.System);
        if (systemMsg != null)
        {
            builder.AppendLine(systemMsg.Msg);
            builder.AppendLine();
        }

        foreach (SgptBot.Models.Message msg in storeUser.Conversation.Where(msg => msg.Role != Role.System).ToArray())
        {
            if (msg.Role == Role.User)
            {
                builder.AppendLine($"Human: {msg.Msg}");
                builder.AppendLine();
            }
            else
            {
                builder.AppendLine($"Assistant: {msg.Msg}");
                builder.AppendLine();
            }
        }

        builder.AppendLine($"Human: {messageText}");
        builder.AppendLine();
        builder.AppendLine("Assistant:");

        string prompt = builder.ToString();
        return prompt;
    }

    private async Task<string> PostToClaudeApiAsync(string prompt, string apiKey, ITelegramBotClient client,
        Message message, StoreUser storeUser, CancellationToken cancellationToken)
    {
        using HttpClient httpClient = new();
        httpClient.Timeout = TimeSpan.FromSeconds(240);

        httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        httpClient.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
        httpClient.DefaultRequestHeaders.Add("x-api-key", apiKey);

        var content = new
        {
            model = "claude-2.1",
            prompt,
            max_tokens_to_sample = 4000
        };

        using StringContent jsonContent = new(JsonConvert.SerializeObject(content), Encoding.UTF8, "application/json");

        try
        {
            HttpResponseMessage response = await httpClient.PostAsync("https://api.anthropic.com/v1/complete",
                jsonContent, cancellationToken);
            string responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                ClaudeApiCompletionResponse? claudeApiCompletionResponse =
                    JsonConvert.DeserializeObject<ClaudeApiCompletionResponse>(responseBody);
                return claudeApiCompletionResponse?.Completion ?? String.Empty;
            }

            ClaudeApiErrorResponse? apiErrorResponse = JsonConvert.DeserializeObject<ClaudeApiErrorResponse>(responseBody);

            if (apiErrorResponse != null)
            {
                await SendBotResponseDependingOnMsgLength(
                    msg: $"API error: {apiErrorResponse.Error.Type} - {apiErrorResponse.Error.Message}",
                    client: client,
                    chatId: message.Chat.Id,
                    userId: storeUser.Id,
                    cancellationToken: cancellationToken,
                    replyMsgId: message.MessageId);
            }
        }
        catch (Exception e)
        {
            _logger.LogError("[{MethodName}] {Error}", nameof(PostToClaudeApiAsync), e.Message);

            await SendBotResponseDependingOnMsgLength(msg: e.Message,
                client: client,
                chatId: message.Chat.Id,
                userId: storeUser.Id,
                cancellationToken: cancellationToken,
                replyMsgId: message.MessageId);
        }

        return "";
    }
    
    private async Task<string?> GetResponseFromOpenAiModel(ITelegramBotClient client,
        StoreUser storeUser,
        Message message,
        string messageText,
        CancellationToken cancellationToken)
    {
        OpenAiApi api = new(storeUser.ApiKey);
        
        List<ChatMessage> chatMessages = [];

        Models.Message? systemMessage = storeUser.Conversation.FirstOrDefault(m => m.Role == Role.System);
        if (systemMessage != null && String.IsNullOrWhiteSpace(systemMessage.Msg) == false)
        {
            chatMessages.Add(new ChatMessage(ChatMessageRole.System, systemMessage.Msg));
        }

        foreach (Models.Message msg in storeUser.Conversation.Where(m => m.Role != Role.System))
        {
            chatMessages.Add(new ChatMessage(msg.Role == Role.Ai ? ChatMessageRole.Assistant : ChatMessageRole.User, msg.Msg));
        }

        messageText = await ProcessYoutubeUrlIfPresent(messageText, client, message.Chat.Id, storeUser, cancellationToken);
        
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
            await SendBotResponseDependingOnMsgLength(msg: e.Message,
                client: client,
                chatId: message.Chat.Id,
                userId: storeUser.Id,
                cancellationToken: cancellationToken, 
                replyMsgId: message.MessageId);

            return "";
        }

        string? response = result.Choices?[0].Message?.Content;
        return response;
    }

    private async Task<string> ProcessYoutubeUrlIfPresent(string messageText,
        ITelegramBotClient botClient, long chatId, StoreUser storeUser,
        CancellationToken cancellationToken)
    {
        string transcriptFromLink = await _youtubeTextProcessor.ProcessTextAsync(messageText, storeUser.ApiKey);
        if (messageText == transcriptFromLink)
        {
            return messageText;
        }

        await SendDocumentResponseAsync(transcriptFromLink, botClient, chatId, storeUser.Id,
            cancellationToken, "This is your transcript \ud83d\udc46");
        return $"The full transcript from the youtube video:\n{transcriptFromLink}\nI want to ask you about this transcript... Wait for my question. Just say - 'Ask me about this transcript...'";
    }

    private Task<Message> SendBotResponseDependingOnMsgLength(string msg, ITelegramBotClient client,
        long chatId,
        long userId, CancellationToken cancellationToken, int? replyMsgId = null, ParseMode? parseMode = null)
    {
        if (msg.Length >= MaxMsgLength)
        {
            return SendDocumentResponseAsync(text: msg,
                botClient: client,
                chatId: chatId,
                userId: userId,
                cancellationToken: cancellationToken);
        }

        try
        {
            return client.SendTextMessageAsync(chatId: chatId,
                text: msg,
                parseMode: parseMode,
                replyToMessageId: replyMsgId,
                cancellationToken: cancellationToken);
        }
        catch (ApiRequestException e)
        {
            _logger.LogError("[{MethodName}] {Error}", nameof(SendBotResponseDependingOnMsgLength), e.Message);

            return client.SendTextMessageAsync(chatId: chatId,
                text: msg,
                parseMode: null,
                replyToMessageId: replyMsgId,
                cancellationToken: cancellationToken);
        }
        catch (Exception e)
        {
            return client.SendTextMessageAsync(chatId: chatId,
                text: e.Message,
                parseMode: null,
                replyToMessageId: replyMsgId,
                cancellationToken: cancellationToken);
        }
    }

    private static async Task<Message> SendDocumentResponseAsync(string text, ITelegramBotClient botClient,
        long chatId,
        long userId,
        CancellationToken cancellationToken, string? caption = "Your answer was too long for sending through telegram. Here is the file with your answer.")
    {
        string filePath = CreateMarkdownFileWithUniqueName(text, userId);

        // Create a FileStream to your text file
        await using FileStream fileStream = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        // Create a new InputOnlineFile from the FileStream
        InputFileStream inputFile = new(fileStream, Path.GetFileName(filePath));

        // Send the file to the specified chat ID
        return await botClient.SendDocumentAsync(
            chatId: chatId,
            document: inputFile,
            caption: caption, 
            cancellationToken: cancellationToken);
    }

    private static string CreateMarkdownFileWithUniqueName(string content, long storeUserId)
    {
        // Get the path to the temp directory
        string tempPath = Path.GetTempPath();

        // Generate a unique filename with the .md extension
        string fileName = Path.ChangeExtension(Path.GetRandomFileName(), ".md");
        fileName = $"{storeUserId}_{fileName}";
        
        // Combine the temp path with the file name to get the full file path
        string filePath = Path.Combine(tempPath, fileName);

        // Write the content to the file
        System.IO.File.WriteAllText(filePath, content);

        // Return the full path of the created file
        return filePath;
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

    private StoreUser? GetStoreUser(User? user)
    {
        if (user == null)
        {
            return null;
        }

        StoreUser storeUser = _userRepository.GetUserOrCreate(user.Id, user.FirstName, user.LastName ?? "", user.Username ?? "",
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
                       "/key_claude - set an Anthropic Claude API key\n" +
                       "/model - choose the GPT model to work with\n" +
                       "/context - set the context message\n" +
                       "/contact - contact the bot admin\n" +
                       "/append - append text to your last message\n" +
                       "/reset_context - reset the context message\n" +
                       "/history - view the conversation history\n" +
                       "/reset - reset the current conversation\n" +
                       "/toggle_voice - enable/disable voice mode\n" +
                       "/toggle_img_quality - switch between standard or HD image quality\n" +
                       "/toggle_img_style - switch between vivid or natural image style\n" +
                       "/toggle_anew_mode - switch on or off 'anew' mode. With this mode you can start each conversation from the beginning without relying on previous history\n" +
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