using System.Globalization;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using Anthropic.SDK;
using Anthropic.SDK.Constants;
using Anthropic.SDK.Messaging;
using HtmlAgilityPack;
using Humanizer;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using LikhodedDynamics.Sber.GigaChatSDK;
using LikhodedDynamics.Sber.GigaChatSDK.Models;
using Microsoft.DeepDev;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OpenAiNg;
using OpenAiNg.Audio;
using OpenAiNg.Chat;
using SgptBot.Models;
using SgptBot.Models.Extensions;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using File = Telegram.Bot.Types.File;
using Message = Telegram.Bot.Types.Message;
using Model = SgptBot.Models.Model;

namespace SgptBot.Services;

public class UpdateHandler : IUpdateHandler
{
    private const int MaxMsgLength = 4000;
    private const string Gpt4VisionModelName = "gpt-4-vision-preview";
    private const string ClaudeSonnet35LatestApiName = "claude-3-5-sonnet-latest";
    private const string ClaudeHaiku35LatestApiName = "claude-3-5-haiku-latest";
    private const string GptOMiniApiName = "gpt-4o-mini";
    private const string Gpt4OApiName = "gpt-4o";
    private readonly ITelegramBotClient _botClient;
    private readonly ILogger<UpdateHandler> _logger;
    private readonly ApplicationSettings _appSettings;
    private readonly IUserRepository _userRepository;
    private readonly IYoutubeTextProcessor _youtubeTextProcessor;
    private readonly IGeminiProvider _geminiProvider;
    private readonly ITokenizer _tokenizer;
    private readonly string[] _allowedExtensions = [".md", ".txt", ".cs", ".zip", ".html", ".htm", ".pdf", ".mp3"];
    private static readonly ModelInfo[] ModelInfos =
    [
        new ModelInfo(GptOMiniApiName, "OpenAI GPT-4o mini", Model.Gpt4OMini,
            "GPT-4o mini is the most advanced model in the small models category, and cheapest model yet. It is multimodal (accepting text or image inputs and outputting text), has higher intelligence than gpt-3.5-turbo but is just as fast. It is meant to be used for smaller tasks, including vision tasks."),
        new ModelInfo(Gpt4OApiName, "OpenAI GPT-4 Omni", Model.Gpt4O,
            "The latest GPT-4 Omni model with multimodal (accepting text or image inputs and outputting text), and same high intelligence as GPT-4 Turbo but more efficient—generates text 2x faster and is 50% cheaper. Additionally, GPT-4o has the best vision and performance across non-English languages of any of our models."),
        new ModelInfo("claude3opus", "Anthropic Claude 3 Opus", Model.Claude3Opus,
            "The powerful model, delivering state-of-the-art performance on highly complex tasks and demonstrating fluency and human-like understanding."),
        new ModelInfo("claude3sonnet", "Anthropic Claude 3 Sonnet", Model.Claude3Sonnet,
            "The model strikes the ideal balance between intelligence and speed—particularly for high-volume tasks. For the vast majority of workloads, Sonnet is 2x faster than Claude 2 and Claude 2.1 with higher levels of intelligence, and delivers strong performance at a lower cost compared to its peers."),
        new ModelInfo(ClaudeSonnet35LatestApiName, "Anthropic Claude 3.5 Sonnet", Model.Claude35Sonnet,
            "Claude 3.5 Sonnet raises the industry bar for intelligence, outperforming competitor models and Claude 3 Opus on a wide range of evaluations, with the speed and cost of our mid-tier model, Claude 3 Sonnet."),
        new ModelInfo("claude3haiku", "Anthropic Claude 3 Haiku", Model.Claude3Haiku,
            "The fastest and most affordable model in its intelligence class. With state-of-the-art vision capabilities and strong performance on industry benchmarks, Haiku is a versatile solution for a wide range of enterprise applications."),
        new ModelInfo(ClaudeHaiku35LatestApiName, "Anthropic Claude 3.5 Haiku", Model.Claude35Haiku,
            "Claude 3.5 Haiku is the next generation of our fastest model. For a similar speed to Claude 3 Haiku, Claude 3.5 Haiku improves across every skill set and surpasses Claude 3 Opus."),
        new ModelInfo("gigachatpro", "Sber GigaChat Pro", Model.GigaChatPro,
            "The model better follows complex instructions and can perform more complex tasks: significantly improved quality of summarization, rewriting and editing of texts, answering various questions. The model is well-versed in many applied domains, particularly in economic and legal issues."),
        new ModelInfo("gemini15pro", "Google Gemini 1.5 Pro", Model.Gemini15Pro,
            "The mid-size multimodal model, optimized for scaling across a wide-range of tasks."),
        new ModelInfo("recraftai", "Recraft AI", Model.RecraftAi,
            "Recraft AI is a powerful image generation model that creates high-quality, customizable images from text descriptions."),
    ];

    public UpdateHandler(ITelegramBotClient botClient, ILogger<UpdateHandler> logger, ApplicationSettings appSettings,
        IUserRepository userRepository, IYoutubeTextProcessor youtubeTextProcessor,
        IGeminiProvider geminiProvider)
    {
        _botClient = botClient;
        _logger = logger;
        _appSettings = appSettings;
        _userRepository = userRepository;
        _youtubeTextProcessor = youtubeTextProcessor;
        _geminiProvider = geminiProvider;
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
        
        StoreUser? storeUser = GetStoreUser(callbackQuery.From);
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
        
        string[] strings = data.Split(' ');
        
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
        
        string messageText = message.Text ?? "";
        if (messageType == MessageType.Voice)
        {
            messageText = await GetTranscriptionTextFromVoiceMessage(message, client, cancellationToken);
        }
        
        StoreUser? storeUser = GetStoreUser(message.From);
        
        if (messageType is MessageType.Document or MessageType.Audio)
        {
            string? textFromDocumentMessage = await GetTextFromDocumentMessage(message, client, message.Chat.Id,
                cancellationToken);
            messageText = textFromDocumentMessage + (String.IsNullOrEmpty(message.Caption) == false
                ? $"\n{message.Caption}"
                : String.Empty);
        }

        await ProcessUrlIfPresent(messageText: messageText,
            botClient: client,
            chatId: message.Chat.Id,
            storeUser: storeUser!,
            cancellationToken: cancellationToken);
        
        if (String.IsNullOrWhiteSpace(messageText))
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
            "/reset_key"             => ResetKeyCommand(_botClient, message, cancellationToken),
            "/key_claude"            => SetKeyClaudeCommand(_botClient, message, cancellationToken),
            "/reset_key_claude"      => ResetKeyClaudeCommand(_botClient, message, cancellationToken),
            "/key_gigachat"          => SetKeyGigaChatCommand(_botClient, message, cancellationToken),
            "/reset_key_gigachat"    => ResetKeyGigaChatCommand(_botClient, message, cancellationToken),
            "/key_gemini"            => SetKeyGeminiCommand(_botClient, message, cancellationToken),
            "/reset_key_gemini"      => ResetKeyGeminiCommand(_botClient, message, cancellationToken),
            "/reset"                 => ResetConversationCommand(_botClient, message, cancellationToken),
            "/info"                  => InfoCommand(message, cancellationToken),
            "/model"                 => ModelCommand(_botClient, message, cancellationToken),
            "/context"               => ContextCommand(_botClient, message, cancellationToken),
            "/contact"               => ContactCommand(_botClient, message, cancellationToken),
            "/reset_context"         => ResetContextCommand(_botClient, message, cancellationToken),
            "/about"                 => AboutCommand(_botClient, message, cancellationToken),
            "/version"               => VersionCommand(_botClient, message, cancellationToken),
            "/broadcast"             => BroadcastCommand(_botClient, message, cancellationToken),
            "/users"                 => UsersCommand(_botClient, message, cancellationToken),
            "/all_users"             => AllUsersCommand(_botClient, message, cancellationToken),
            "/allow"                 => AllowCommand(_botClient, message, cancellationToken),
            "/deny"                  => DenyCommand(_botClient, message, cancellationToken),
            "/toggle_voice"          => ToggleVoiceCommand(_botClient, message, cancellationToken),
            "/toggle_anew_mode"      => ToggleAnewMode(_botClient, message, cancellationToken),
            "/image"                 => ImageCommand(_botClient, message, cancellationToken),
            "/append"                => AppendCommand(_botClient, message, cancellationToken),
            "/key_recraft"          => SetKeyRecraftCommand(_botClient, message, cancellationToken),
            "/reset_key_recraft"    => ResetKeyRecraftCommand(_botClient, message, cancellationToken),
            "/toggle_recraft_style" => ToggleRecraftStyleCommand(_botClient, message, cancellationToken),
            "/toggle_recraft_substyle" => ToggleRecraftSubStyleCommand(_botClient, message, cancellationToken),
            "/reset_recraft_substyle" => ResetRecraftSubStyleCommand(_botClient, message, cancellationToken),
            _                        => TalkToModelCommand(_botClient, message, messageText, cancellationToken)
        };
        Message sentMessage = await action;
        _logger.LogInformation("The message was sent with id: {SentMessageId}", sentMessage.MessageId);
    }

    private async Task<Message> BroadcastCommand(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
    {
        StoreUser? admin = GetStoreUser(message.From);
        if (admin == null)
        {
            return await botClient.SendTextMessageAsync(message.Chat.Id, "Error getting the user from the store.",
                cancellationToken: cancellationToken);
        }

        if (admin.IsAdministrator == false)
        {
            return await botClient.SendTextMessageAsync(message.Chat.Id,
                "This command might be executed only by the administrator.",
                cancellationToken: cancellationToken);
        }

        StoreUser[] users = GetActiveUsers();

        List<StoreUser> successfullyDelivered = [];
        foreach (var user in users)
        {
            try
            {
                await botClient.SendTextMessageAsync(user.Id,
                        GetVersionMsg(),
                        parseMode: ParseMode.Markdown,
                        cancellationToken: cancellationToken);

                successfullyDelivered.Add(user);
            }
            catch (Exception)
            {
                // ignore
            }
        }

        return await botClient.SendTextMessageAsync(admin.Id,
                $"The new version message was successfully broadcasted ({"user".ToQuantity(successfullyDelivered.Count)} from {"user".ToQuantity(users.Length)}).",
                cancellationToken: cancellationToken);
    }

    private async Task<Message> ResetKeyGeminiCommand(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
    {
        StoreUser? storeUser = GetStoreUser(message.From);
        if (storeUser == null)
        {
            return await botClient.SendTextMessageAsync(message.Chat.Id, "Error getting the user from the store.",
                cancellationToken: cancellationToken);
        }

        storeUser.GeminiApiKey = "";
        _userRepository.UpdateUser(storeUser);

        return await botClient.SendTextMessageAsync(message.Chat.Id, "Gemini API key was reset.",
            cancellationToken: cancellationToken);
    }

    private async Task<Message> SetKeyGeminiCommand(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
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
                "After '/key_gemini' command you must input your Gemini API key. Try again.",
                cancellationToken: cancellationToken);
        }

        string apiKey = strings[1];
        if (String.IsNullOrWhiteSpace(apiKey))
        {
            return await botClient.SendTextMessageAsync(message.Chat.Id,
                "After '/key_gemini' command you must input your Gemini API key. Try again.",
                cancellationToken: cancellationToken);
        }

        storeUser.GeminiApiKey = apiKey;
        _userRepository.UpdateUser(storeUser);

        return await botClient.SendTextMessageAsync(message.Chat.Id, "Gemini API key was set.",
            cancellationToken: cancellationToken);
    }

    private async Task<Message> ResetKeyGigaChatCommand(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
    {
        StoreUser? storeUser = GetStoreUser(message.From);
        if (storeUser == null)
        {
            return await botClient.SendTextMessageAsync(message.Chat.Id, "Error getting the user from the store.",
                cancellationToken: cancellationToken);
        }

        storeUser.GigaChatApiKey = "";
        _userRepository.UpdateUser(storeUser);

        return await botClient.SendTextMessageAsync(message.Chat.Id, "Sber Auth key was reset.",
            cancellationToken: cancellationToken);
    }

    private async Task<Message> SetKeyGigaChatCommand(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
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
                "After '/key_gigachat' command you must input your Sber Auth key. Try again.",
                cancellationToken: cancellationToken);
        }

        string apiKey = strings[1];
        if (String.IsNullOrWhiteSpace(apiKey))
        {
            return await botClient.SendTextMessageAsync(message.Chat.Id,
                "After '/key_gigachat' command you must input your Sber Auth key. Try again.",
                cancellationToken: cancellationToken);
        }

        storeUser.GigaChatApiKey = apiKey;
        _userRepository.UpdateUser(storeUser);

        return await botClient.SendTextMessageAsync(message.Chat.Id, "Sber Auth key was set.",
            cancellationToken: cancellationToken);
    }

    private async Task<Message> ResetKeyClaudeCommand(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
    {
        StoreUser? storeUser = GetStoreUser(message.From);
        if (storeUser == null)
        {
            return await botClient.SendTextMessageAsync(message.Chat.Id, "Error getting the user from the store.",
                cancellationToken: cancellationToken);
        }

        storeUser.ClaudeApiKey = "";
        _userRepository.UpdateUser(storeUser);

        return await botClient.SendTextMessageAsync(message.Chat.Id, "Anthropic Claude API key was reset.",
            cancellationToken: cancellationToken);
    }

    private async Task<Message> ResetKeyCommand(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
    {
        StoreUser? storeUser = GetStoreUser(message.From);
        if (storeUser == null)
        {
            return await botClient.SendTextMessageAsync(message.Chat.Id, "Error getting the user from the store.",
                cancellationToken: cancellationToken);
        }

        storeUser.ApiKey = "";
        _userRepository.UpdateUser(storeUser);

        return await botClient.SendTextMessageAsync(message.Chat.Id, "OpenAI API key was reset.",
            cancellationToken: cancellationToken);
    }

    private static async Task<Message> VersionCommand(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
    {
        string msg = GetVersionMsg();

        return await botClient.SendTextMessageAsync(message.Chat.Id,
            msg,
            parseMode: ParseMode.Markdown,
            disableNotification: true,
            cancellationToken: cancellationToken);
    }

    private static string GetVersionMsg()
    {
        const string name = "LLM Bot";
        var version = GetVersionWithDateTime();
        var msg = $"""
                   🤖 *{name} v.{version} Update* 🚀

                   Hello everyone! We've just rolled out an exciting update to *{name}*. Here's what's new in version *{version}*:

                   ✨ *New Features*:
                   - Integrated Recraft AI for high-quality image generation with customizable styles:
                     • Two main styles: Realistic and Digital Illustration
                     • Multiple sub-styles for each main style
                     • Use `/toggle_recraft_style` and `/toggle_recraft_substyle` to customize your images
                     • Generate images using `/image` command with your prompt
                     • In order to use this feature you need to set your Recraft API key using `/key_recraft` command

                   💬 *Feedback*:
                   We're always looking to improve and value your feedback. If you have any suggestions or encounter any issues, please let us know through (use `/contact <MESSAGE>` command).

                   Stay tuned for more updates, and thank you for using *{name}*!
                   """;
        return msg;
    }

    private static string GetVersionWithDateTime()
    {
        Assembly assembly = Assembly.GetExecutingAssembly();

        FileInfo fileInfo = new(assembly.Location);
        DateTime lastWriteTime = fileInfo.LastWriteTime;

        string versionWithDateTime = String.Format(
            CultureInfo.InvariantCulture,
            "{0}.{1}.{2}{3}",
            1,
            0,
            lastWriteTime.ToString("yyyyMMdd"),
            lastWriteTime.ToString("HHmmss")
        );

        return versionWithDateTime;
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

    private async Task<string?> GetTextFromDocumentMessage(Message message, ITelegramBotClient client, long chatId,
        CancellationToken cancellationToken)
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

            string fileName = "";
            string documentFileId = "";

            if (message.Document != null)
            {
                var document = message.Document;
                
                fileName = document.FileName ?? "";
                documentFileId = document.FileId;
            }
            
            if (message.Audio != null)
            {
                var audio = message.Audio;

                fileName = audio.FileName ?? "";
                documentFileId = audio.FileId;
            }

            if (String.IsNullOrEmpty(fileName))
            {
                return "";
            }

            string extension = Path.GetExtension(fileName);
            if (_allowedExtensions.Contains(extension) == false)
            {
                string extensions = String.Join(", ", _allowedExtensions);
                await client.SendTextMessageAsync(message.Chat.Id, 
                    $"Bot supports {extensions} formats.",
                    cancellationToken: cancellationToken);
                return "";
            }
            
            File file = await client.GetFileAsync(documentFileId, cancellationToken: cancellationToken);

            string path = Path.GetTempPath();
            string fullDocumentFileName = Path.Combine(path, fileName);

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

            text = extension switch
            {
                ".html" or ".htm" => ExtractPlainTextFromHtmDoc(text),
                ".mp3" => await GetTextFromAudioFile(client, fullDocumentFileName, storeUser, chatId,
                    cancellationToken),
                _ => text
            };

            return text;
        }
        catch (Exception e)
        {
            await client.SendTextMessageAsync(chatId,
                e.Message,
                cancellationToken: cancellationToken);

            _logger.LogWarning("[{MethodName}] {Error}", nameof(GetTextFromDocumentMessage), e.Message);
            return "";
        }
    }

    private async Task<string> GetTextFromAudioFile(ITelegramBotClient client,
        string path, StoreUser? storeUser, long chatId, CancellationToken cancellationToken)
    {
        if (IsFileSizeMoreThanLimit(path))
        {
            await client.SendTextMessageAsync(chatId,
                $"The audio file size exceeds the maximum allowed limit of 19 MB.",
                cancellationToken: cancellationToken);

            return "";
        }

        string transcriptFromAudio = await _youtubeTextProcessor.GetTextFromAudioFileAsync(path,
            storeUser!.ApiKey);
        if (String.IsNullOrEmpty(transcriptFromAudio))
        {
            return "";
        }

        await SendDocumentResponseAsync(transcriptFromAudio, client, chatId, storeUser.Id,
            cancellationToken, "This is your transcript \ud83d\udc46");

        return $"""
                This is the transcript from my audio file:

                ######
                {transcriptFromAudio}
                ######

                I want to ask you about this transcript... Wait for my question. Just say - 'Ask me about this transcript...'
                """;
    }

    private static bool IsFileSizeMoreThanLimit(string path)
    {
        long maxFileSizeBytes = 19 * 1024 * 1024; // 19 MB in bytes
        var fileInfo = new FileInfo(path);
        return fileInfo.Length > maxFileSizeBytes;
    }

    private string ExtractPlainTextFromHtmDoc(string text)
    {
        HtmlDocument htmlDocument = new();
        htmlDocument.LoadHtml(text);

        HtmlNode? bodyNode = htmlDocument.DocumentNode.SelectSingleNode("//body");
        return bodyNode == null ? text : ExtractTextFromNode(bodyNode);
    }

    private static string ExtractTextFromNode(HtmlNode? node)
    {
        if (node == null)
        {
            return "";
        }

        if (node.NodeType == HtmlNodeType.Text)
        {
            return node.InnerText.Trim();
        }

        if (node.Name.Equals("script", StringComparison.OrdinalIgnoreCase) ||
            node.Name.Equals("style", StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }

        if (node.HasAttributes && node.Attributes.Contains("style"))
        {
            node.Attributes.Remove("style");
        }

        StringBuilder builder = new();

        foreach (HtmlNode? childNode in node.ChildNodes)
        {
            builder.AppendLine(ExtractTextFromNode(childNode));
        }

        return builder.ToString().Trim();
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
            string extension = Path.GetExtension(allowedPath);
            string textFromFile = "";
            
            if (extension.Equals(".html", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".htm", StringComparison.OrdinalIgnoreCase))
            {
                textFromFile = ExtractPlainTextFromHtmDoc(await System.IO.File.ReadAllTextAsync(allowedPath, cancellationToken));
            }
            
            if (extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                textFromFile = ExtractPlainTextFromPdfDoc(allowedPath);
            }
            
            if (extension.Equals(".md", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".txt", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".cs", StringComparison.OrdinalIgnoreCase))
            {
                textFromFile = await System.IO.File.ReadAllTextAsync(allowedPath, cancellationToken);
            }
            
            builder.AppendLine(textFromFile);
        }

        return builder.ToString();
    }

    private string ExtractPlainTextFromPdfDoc(string path)
    {
        try
        {
            StringBuilder builder = new();

            byte[] fileBytes = System.IO.File.ReadAllBytes(path);

            using (MemoryStream memoryStream = new(fileBytes))
            {
                PdfReader reader = new(memoryStream);
                PdfDocument pdfDocument = new(reader);
            
                int pages = pdfDocument.GetNumberOfPages();
                for (int i = 1; i <= pages; i++)
                {
                    PdfPage? page = pdfDocument.GetPage(i);
                    string? text = PdfTextExtractor.GetTextFromPage(page, new SimpleTextExtractionStrategy());
                    builder.AppendLine(text);
                }
            }

            return builder.ToString();
        }
        catch (Exception)
        {
            return "";
        }
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

        switch (user.Model)
        {
            case Model.Gpt3 or Model.Gpt4 or Model.Gpt4O or Model.Gpt4OMini when String.IsNullOrWhiteSpace(user.ApiKey):
                await client.SendTextMessageAsync(chatId,
                    "Your OpenAI API key is not set. Use '/key' command and set key.");
                return false;
            case Model.Claude21 or Model.Claude3Opus or Model.Claude3Sonnet or Model.Claude3Haiku or Model.Claude35Sonnet or Model.Claude35Haiku when String.IsNullOrWhiteSpace(user.ClaudeApiKey):
                await client.SendTextMessageAsync(chatId,
                    "Your Claude API key is not set. Use '/key_claude' command and set key.");
                return false;
            case Model.GigaChatLite or Model.GigaChatLitePlus or Model.GigaChatPro when String.IsNullOrWhiteSpace(user.GigaChatApiKey):
                await client.SendTextMessageAsync(chatId,
                    "Your GigaChat Auth key is not set. Use '/key_gigachat' command and set key.");
                return false;
            case Model.Gemini15Pro when String.IsNullOrWhiteSpace(user.GeminiApiKey):
                await client.SendTextMessageAsync(chatId,
                    "Your Gemini API key is not set. Use '/key_gemini' command and set key.");
                return false;
            case Model.ElMultilingualV2 when String.IsNullOrWhiteSpace(user.ElevenLabsApiKey):
                await client.SendTextMessageAsync(chatId,
                    "Your ElevenLabs API key is not set. Use '/key_elevenlabs' command and set key.");
                return false;
            case Model.RecraftAi when String.IsNullOrWhiteSpace(user.RecraftApiKey):
                await client.SendTextMessageAsync(chatId,
                    "Your Recraft AI API key is not set. Use '/key_recraft' command and set key.");
                return false;
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
        
        if ((storeUser!.Model == Model.Gpt3 ||
             storeUser.Model == Model.Gpt4 ||
             storeUser.Model == Model.Gpt4O ||
             storeUser.Model == Model.Gpt4OMini ||
             storeUser.Model == Model.Claude3Opus ||
             storeUser.Model == Model.Claude3Haiku ||
             storeUser.Model == Model.Claude3Sonnet ||
             storeUser.Model == Model.Claude35Sonnet) ==
            false)
        {
            await client.SendTextMessageAsync(message.Chat.Id,
                "Error: you must use OpenAI GPT3 or GPT4 or Anthropic Claude Opus or Claude Haiku or Claude Sonnet models in order to analyze pictures.",
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

        string visionModelResponse =
            await GetVisionModelResponse(message, storeUser, ImageToBase64(fileName), client, cancellationToken);

        if (String.IsNullOrEmpty(visionModelResponse))
        {
            await client.SendTextMessageAsync(message.Chat.Id, 
                "Error while getting response about the image. Response is empty.",
                replyToMessageId:  message.MessageId, 
                cancellationToken: cancellationToken);
            return;
        }
        
        storeUser.Conversation.Add(new Models.Message(Role.User, message.Caption ?? "What is it?", DateOnly.FromDateTime(DateTime.Today)));
        storeUser.Conversation.Add(new Models.Message(Role.Ai, visionModelResponse, DateOnly.FromDateTime(DateTime.Today)));
        if (storeUser.AnewMode == false) _userRepository.UpdateUser(storeUser);

        if (!storeUser.VoiceMode)
        {
            await client.SendTextMessageAsync(message.Chat.Id, visionModelResponse,
                parseMode: ParseMode.Markdown,
                replyToMessageId: message.MessageId,
                cancellationToken: cancellationToken);
            return;
        }

        _logger.LogInformation("Voice mode is active.");
        _logger.LogInformation("Response length is: {length}", visionModelResponse.Length);

        string ttsAudioFilePath = await GetTtsAudio(visionModelResponse.Replace(Environment.NewLine, ""), storeUser.ApiKey);
        _logger.LogInformation("Path to tts audio message: {path}", ttsAudioFilePath);
            
        if (String.IsNullOrEmpty(ttsAudioFilePath) == false)
        {
            await client.SendVoiceAsync(message.Chat.Id,
                InputFile.FromStream(System.IO.File.OpenRead(ttsAudioFilePath)),
                replyToMessageId: message.MessageId,
                cancellationToken: cancellationToken);
            return;
        }

        await client.SendTextMessageAsync(message.Chat.Id, visionModelResponse,
            parseMode: ParseMode.Markdown,
            replyToMessageId:  message.MessageId, 
            cancellationToken: cancellationToken);
    }

    private static async Task<string> GetVisionModelResponse(Message message,
        StoreUser storeUser, string base64Image, ITelegramBotClient client, CancellationToken cancellationToken)
    {
        string visionModelResponse;

        switch (storeUser.Model)
        {
            case Model.Gpt4:
            case Model.Gpt3:
            case Model.Gpt4OMini:
            case Model.Gpt4O:
            {
                visionModelResponse =
                    await GetVisionResponseFromOpenAiModel(client, message, storeUser, base64Image, cancellationToken);
                break;
            }
            case Model.Claude3Haiku:
            case Model.Claude3Sonnet:
            case Model.Claude3Opus:
            case Model.Claude35Sonnet:
            {
                visionModelResponse =
                    await GetVisionResponseFromAnthropicModel(client, message, storeUser, base64Image,
                        cancellationToken);
                break;
            }
            default:
            {
                visionModelResponse = "";
                break;
            }
        }

        return visionModelResponse;
    }

    private static async Task<string> GetVisionResponseFromAnthropicModel(ITelegramBotClient client, Message message,
        StoreUser storeUser, string base64Image,
        CancellationToken cancellationToken)
    {
        string visionModelResponse = "";
        AnthropicClient anthropicClient = new(new APIAuthentication(storeUser.ClaudeApiKey));
        List<Anthropic.SDK.Messaging.Message> messages =
        [
            new Anthropic.SDK.Messaging.Message
            {
                Role = RoleType.User,
                Content = new dynamic[]
                {
                    new ImageContent
                    {
                        Source = new ImageSource
                        {
                            MediaType = "image/jpeg",
                            Data = base64Image
                        }
                    },
                    new TextContent
                    {
                        Text = message.Caption
                    }
                }
            }
        ];

        MessageParameters parameters = new()
        {
            Messages = messages,
            MaxTokens = 4090,
            Model = storeUser.Model switch
            {
                Model.Claude3Sonnet => AnthropicModels.Claude3Sonnet,
                Model.Claude3Opus => AnthropicModels.Claude3Opus,
                Model.Claude3Haiku => AnthropicModels.Claude3Haiku,
                Model.Claude35Sonnet => ClaudeSonnet35LatestApiName,
                _ => AnthropicModels.Claude3Haiku
            },
            Stream = false,
            Temperature = 1.0m,
        };

        MessageResponse? messageResponse;
        try
        {
            messageResponse = await anthropicClient.Messages.GetClaudeMessageAsync(parameters,
                cancellationToken);
        }
        catch (Exception e)
        {
            await client.SendTextMessageAsync(message.Chat.Id,
                $"Error while getting response about the image:\n\n{e.Message}",
                replyToMessageId: message.MessageId,
                cancellationToken: cancellationToken);
            return visionModelResponse;
        }

        visionModelResponse = messageResponse.Content.FirstOrDefault(c => c.Type == "text")?.Text ?? "";
        return visionModelResponse;
    }

    private static async Task<string> GetVisionResponseFromOpenAiModel(ITelegramBotClient client, Message message,
        StoreUser storeUser, string base64Image,
        CancellationToken cancellationToken)
    {
        string visionModelResponse = "";
        object payload = GetPayload(base64Image, message.Caption, storeUser.Model);

        string jsonContent = JsonConvert.SerializeObject(payload);

        HttpClient httpClient = new();
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", storeUser.ApiKey);
        httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        HttpResponseMessage response = await httpClient.PostAsync(
            "https://api.openai.com/v1/chat/completions",
            new StringContent(jsonContent, Encoding.UTF8, "application/json"), cancellationToken);

        string responseString = await response.Content.ReadAsStringAsync(cancellationToken);

        JObject parsedJson = JObject.Parse(responseString);
        try
        {
            visionModelResponse = (string) parsedJson["choices"]?[0]?["message"]?["content"]!;
        }
        catch (Exception e)
        {
            await client.SendTextMessageAsync(message.Chat.Id,
                $"Error while getting response about the image:\n\n{e.Message}",
                replyToMessageId: message.MessageId,
                cancellationToken: cancellationToken);
            return visionModelResponse;
        }

        return visionModelResponse;
    }

    private static object GetPayload(string base64Image, string? caption, Model storeUserModel)
    {
        var payload = new
        {
            model = storeUserModel switch
            {
                Model.Gpt3 => Gpt4VisionModelName,
                Model.Gpt4 => Gpt4VisionModelName,
                Model.Gpt4OMini => GptOMiniApiName,
                Model.Gpt4O => Gpt4OApiName,
                _ => Gpt4OApiName
            },
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
                $"{i + 1}) Id: {user.Id}; First name: {user.FirstName}; Last name: {user.LastName}; Username: {user.UserName}; Is blocked: {user.IsBlocked}; Last activity: {lastActivityMessage} ago; Model: {user.Model};");
        }
        
        return await SendBotResponseDependingOnMsgLength(msg: builder.ToString(),
            client: client,
            chatId: message.Chat.Id,
            userId: storeUser.Id, 
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
        
        if (storeUser.Model != Model.RecraftAi)
        {
            return await botClient.SendTextMessageAsync(message.Chat.Id,
                "You must use a RecraftAi model in order to get an image. Try again.",
                cancellationToken: cancellationToken);
        }

        if (String.IsNullOrWhiteSpace(storeUser.RecraftApiKey))
        {
            return await botClient.SendTextMessageAsync(message.Chat.Id,
                "You must setup your Recraft API key in order to use this model. Try again.",
                cancellationToken: cancellationToken);
        }

        string? url = await GenerateImage(prompt, storeUser.RecraftApiKey, storeUser.RecraftImgStyle, storeUser.RecraftImgSubStyle);
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
        
        string[] strings = message.Text!.Split(' ');
        if (strings.Length < 2)
        {
            return await botClient.SendTextMessageAsync(message.Chat.Id,
                "After '/deny' command you must input the user id.\nTry again.",
                cancellationToken: cancellationToken);
        }

        string userId = strings[1];
        if (String.IsNullOrWhiteSpace(userId))
        {
            return await botClient.SendTextMessageAsync(message.Chat.Id,
                "After '/deny' command you must input the user id.\nTry again.",
                cancellationToken: cancellationToken);
        }

        bool parseResult = Int32.TryParse(userId, out int id);
        if (parseResult == false)
        {
            return await botClient.SendTextMessageAsync(message.Chat.Id,
                "After '/deny' command you must input the user id.\nTry again.",
                cancellationToken: cancellationToken);
        }

        StoreUser? userById = _userRepository.GetUserById(id);
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
        
        string[] strings = message.Text!.Split(' ');
        if (strings.Length < 2)
        {
            return await botClient.SendTextMessageAsync(message.Chat.Id,
                "After '/allow' command you must input the user id.\nTry again.",
                cancellationToken: cancellationToken);
        }

        string userId = strings[1];
        if (String.IsNullOrWhiteSpace(userId))
        {
            return await botClient.SendTextMessageAsync(message.Chat.Id,
                "After '/allow' command you must input the user id.\nTry again.",
                cancellationToken: cancellationToken);
        }

        bool parseResult = Int32.TryParse(userId, out int id);
        if (parseResult == false)
        {
            return await botClient.SendTextMessageAsync(message.Chat.Id,
                "After '/allow' command you must input the user id.\nTry again.",
                cancellationToken: cancellationToken);
        }

        StoreUser? userById = _userRepository.GetUserById(id);
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

        var activeUsers = GetActiveUsers();

        if (activeUsers.Length == 0)
        {
            return await botClient.SendTextMessageAsync(message.Chat.Id,
                "Active users not found.",
                cancellationToken: cancellationToken);
        }

        StringBuilder builder = new();
        for (var i = 0; i < activeUsers.Take(15).ToArray().Length; i++)
        {
            var user = activeUsers[i];
            var lastActivityMessage = GetLastActivityMessage(user.ActivityTime);
            builder.AppendLine(
                $"{i + 1}) Id: {user.Id}; First name: {GetUserField(user.FirstName)}; Last name: {GetUserField(user.LastName)}; Username: {GetUserField(user.UserName)}; Last activity: {lastActivityMessage} ago; Model: {ModelInfos.FirstOrDefault(info => info.ModelEnum.Equals(user.Model))?.PrettyName};");
            builder.AppendLine();
        }

        builder.AppendLine($"The total number: {"user".ToQuantity(activeUsers.Length)}");

        return await botClient.SendTextMessageAsync(message.Chat.Id,
            builder.ToString(),
            cancellationToken: cancellationToken);
    }

    private static string GetUserField(string field) => string.IsNullOrWhiteSpace(field) == false ? field : "<EMPTY>";

    private StoreUser[] GetActiveUsers()
    {
        StoreUser[] users = _userRepository.GetAllUsers();
        StoreUser[] activeUsers = users
            .Where(user => user.History.Any(msg => msg.Role == Role.Ai) ||
                           user.Conversation.Any(msg => msg.Role == Role.Ai))
            .OrderByDescending(user => user.ActivityTime)
            .ToArray();
        return activeUsers;
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
            "This bot allows you to use different LLM models in order to receive useful information from them.",
            cancellationToken: cancellationToken);
    }

    private async Task<Message> ResetContextCommand(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
    {
        StoreUser? storeUser = GetStoreUser(message.From);
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
                "After the '/context' command you must input the context (system) prompt. Try again.",
                cancellationToken: cancellationToken);
        }

        string contextPrompt = String.Join(' ', strings.Skip(1));
        if (String.IsNullOrWhiteSpace(contextPrompt))
        {
            return await botClient.SendTextMessageAsync(message.Chat.Id,
                "After the '/context' command you must input the context (system) prompt. Try again.",
                cancellationToken: cancellationToken);
        }

        RemoveAllSystemMessages(storeUser);

        Models.Message newSystemMessage = new SgptBot.Models.Message(Role.System, contextPrompt, DateOnly.FromDateTime(DateTime.Today));
        storeUser.Conversation.Insert(0, newSystemMessage);

        _userRepository.UpdateUser(storeUser);
        
        return await botClient.SendTextMessageAsync(
            message.Chat.Id, 
            "Context prompt was set.",
            cancellationToken: cancellationToken);
    }

    private static void RemoveAllSystemMessages(StoreUser storeUser)
    {
        Models.Message[] systemMessages = storeUser.Conversation.Where(msg => msg.Role == Role.System).ToArray();
        foreach (Models.Message systemMessage in systemMessages)
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
        if (strings.Length >= 2)
        {
            return await SetSelectedModel(botClient, message.Chat.Id, strings, storeUser, cancellationToken);
        }

        var inlineKeyboardButtons = ModelInfos.Select(info => new InlineKeyboardButton(info.PrettyName)
            { CallbackData = $"/model {info.InternalName}" }).ToList();
        var buttons = inlineKeyboardButtons.Select(button => (InlineKeyboardButton[]) [button]).ToArray();

        InlineKeyboardMarkup inlineKeyboard = new(buttons);

        return await botClient.SendTextMessageAsync(message.Chat.Id,
            GetModelDescriptions(),
            replyMarkup: inlineKeyboard,
            parseMode: ParseMode.Markdown,
            cancellationToken: cancellationToken);
    }

    private static string GetModelDescriptions()
    {
        var builder = new StringBuilder();
        for (var i = 0; i < ModelInfos.Length; i++)
        {
            var info = ModelInfos[i];
            builder.AppendLine($"{i + 1}) *{info.PrettyName}*. {info.Description}");
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private async Task<Message> SetSelectedModel(ITelegramBotClient botClient, long chatId,
        string[] strings, StoreUser storeUser, CancellationToken cancellationToken)
    {
        var modelName = strings[1];
        var modelsNames = ModelInfos.Select(info => info.InternalName).ToArray();
        var errorMsg = $"After the `/model` command you must input the model name.\nModel name must be one of: {modelsNames.Humanize(s => $"`{s}`", "or")}.\nTry again.";
        if (string.IsNullOrWhiteSpace(modelName))
        {
            return await botClient.SendTextMessageAsync(chatId,
                errorMsg,
                cancellationToken: cancellationToken);
        }

        var lowerInvariantOfName = modelName.ToLowerInvariant();

        if (!modelsNames.Contains(lowerInvariantOfName))
        {
            return await botClient.SendTextMessageAsync(chatId,
                errorMsg,
                parseMode: ParseMode.Markdown,
                cancellationToken: cancellationToken);
        }

        storeUser.Model = ModelInfos.FirstOrDefault(info => info.InternalName.Equals(lowerInvariantOfName))?.ModelEnum ?? Model.Gpt3;
        _userRepository.UpdateUser(storeUser);

        return await botClient.SendTextMessageAsync(
            chatId, 
            $"Model `{ModelInfos.FirstOrDefault(info => info.ModelEnum.Equals(storeUser.Model))?.PrettyName}` was set.",
            parseMode: ParseMode.Markdown,
            cancellationToken: cancellationToken);
    }

    private async Task<Message> InfoCommand(Message message, CancellationToken cancellationToken)
    {
        var storeUser = GetStoreUser(message.From);
        if (storeUser == null)
        {
            return await _botClient.SendTextMessageAsync(message.Chat.Id, "Error getting the user from the store.",
                cancellationToken: cancellationToken);
        }

        StringBuilder builder = new();
        foreach (var msg in storeUser.Conversation)
        {
            builder.Append(msg.Msg);
        }

        var tokenCount = GetTokenCount(builder.ToString());
        
        return await _botClient.SendTextMessageAsync(message.Chat.Id,
            $"First name: `{storeUser.FirstName}`\n" +
            $"Last name: `{storeUser.LastName}`\n" +
            $"Username: `{storeUser.UserName}`\n" +
            $"OpenAI API key: `{storeUser.ApiKey}`\n" +
            $"Claude API key: `{storeUser.ClaudeApiKey}`\n" +
            $"GigaChat API key: `{storeUser.GigaChatApiKey}`\n" +
            $"Gemini API key: `{storeUser.GeminiApiKey}`\n" +
            $"Recraft AI API key: `{storeUser.RecraftApiKey}`\n" +
            $"Model: `{ModelInfos.FirstOrDefault(info => info.ModelEnum.Equals(storeUser.Model))?.PrettyName}`\n" +
            $"Image style: `{storeUser.RecraftImgStyle.GetDescription()}`\n" +
            $"Image sub-style: `{storeUser.RecraftImgSubStyle.GetDescription()}`\n" +
            $"Voice mode: `{(storeUser.VoiceMode ? "on" : "off")}`\n" +
            $"Anew mode: `{(storeUser.AnewMode ? "on" : "off")}`\n" +
            $"Current context window size (number of tokens): `{tokenCount}`\n" +
            $"Context prompt: {storeUser.Conversation.FirstOrDefault(msg => msg.Role == Role.System)?.Msg ?? "_<empty>_"}\n",
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
        foreach (Models.Message msg in storeUser.Conversation)
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
        StoreUser? storeUser = GetStoreUser(message.From);
        if (storeUser == null)
        {
            return await botClient.SendTextMessageAsync(message.Chat.Id, "Error getting the user from the store.",
                cancellationToken: cancellationToken);
        }

        string[] strings = message.Text!.Split(' ');
        if (strings.Length < 2)
        {
            return await botClient.SendTextMessageAsync(message.Chat.Id, "After '/key' command you must input your openAI API key. You can get your key here - https://platform.openai.com/account/api-keys. Try again.",
                cancellationToken: cancellationToken);
        }

        string apiKey = strings[1];
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

        string? response = storeUser!.Model switch
        {
            Model.Gpt3 or Model.Gpt4 or Model.Gpt4O or Model.Gpt4OMini => await GetResponseFromOpenAiLikeModel(botClient, storeUser,
                message, messageText, cancellationToken),
            Model.Claude3Opus or Model.Claude3Sonnet or Model.Claude3Haiku or Model.Claude35Sonnet or Model.Claude35Haiku => await GetResponseFromClaude3Model(botClient, storeUser, message, messageText,
                cancellationToken),
            Model.GigaChatLite or Model.GigaChatLitePlus or Model.GigaChatPro => await GetResponseFromGigaChatModel(
                botClient, storeUser, message, messageText, cancellationToken),
            Model.Gemini15Pro => await GetResponseFromGeminiModel(botClient, storeUser, message, messageText, cancellationToken),
            Model.RecraftAi => "The Recraft AI model can only be used with the '/image' command to generate images. Please use '/image' followed by your prompt, or select a different model for text conversations.",
            _ => await GetResponseFromAnthropicModel(botClient, storeUser, message, messageText, cancellationToken),
        };
        
        if (String.IsNullOrWhiteSpace(response))
        {
            return await botClient.SendTextMessageAsync(message.Chat.Id, "Response from model is empty. Try again.",
                cancellationToken: cancellationToken);
        }
        
        _logger.LogInformation("Received response message from model.");
        
        storeUser.Conversation.Add(new Models.Message(Role.User, messageText, DateOnly.FromDateTime(DateTime.Today)));
        storeUser.Conversation.Add(new Models.Message(Role.Ai, response, DateOnly.FromDateTime(DateTime.Today)));
        if (storeUser.AnewMode == false) _userRepository.UpdateUser(storeUser);

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

        try
        {
            return await SendBotResponseDependingOnMsgLength(response, botClient, message.Chat.Id, storeUser.Id, cancellationToken, message.MessageId, ParseMode.Markdown);
        }
        catch (ApiRequestException e)
        {
            _logger.LogError("[{MethodName}] {Error}", nameof(TalkToModelCommand), e.Message);

            return await botClient.SendTextMessageAsync(chatId: message.Chat.Id,
                text: response,
                parseMode: null,
                replyToMessageId: message.MessageId,
                cancellationToken: cancellationToken);
        }
        catch (Exception e)
        {
            return await botClient.SendTextMessageAsync(chatId: message.Chat.Id,
                text: e.Message,
                parseMode: null,
                replyToMessageId: message.MessageId,
                cancellationToken: cancellationToken);
        }
    }

    private async Task<string?> GetResponseFromGeminiModel(ITelegramBotClient botClient, StoreUser storeUser, Message message, string messageText, CancellationToken cancellationToken)
    {
        List<GeminiContent> contents = [];
        foreach (var msg in storeUser.Conversation.Where(m => m.Role != Role.System))
        {
            var content = new GeminiContent
                { Role = msg.Role == Role.Ai ? "model" : "user", Parts = [new GeminiPart { Text = msg.Msg }] };
            contents.Add(content);
        }

        contents.Add(new GeminiContent { Role = "user", Parts = [new GeminiPart { Text = messageText }] });

        GeminiConversation conversation = new() { Contents = [.. contents] };

        var (answer, status) = await _geminiProvider.GetAnswerFroGemini(storeUser.GeminiApiKey, conversation);

        if (status == GeminiResponseStatus.Failure)
        {
            await SendBotResponseDependingOnMsgLength(msg: $"The model returns an error:\n{answer}",
                client: botClient,
                chatId: message.Chat.Id,
                userId: storeUser.Id,
                cancellationToken: cancellationToken,
                replyMsgId: message.MessageId,
                disableWebPagePreview: true);

            return string.Empty;
        }

        return answer;
    }

    private async Task<string?> GetResponseFromGigaChatModel(ITelegramBotClient botClient, StoreUser storeUser, Message message, string messageText, CancellationToken cancellationToken)
    {
        var gigaChat = new GigaChat(storeUser.GigaChatApiKey, false, true, false);
        await gigaChat.CreateTokenAsync();
        
        List<MessageContent> chatMessages = [];
        foreach (Models.Message msg in storeUser.Conversation.Where(m => m.Role != Role.System))
        {
            var item = new MessageContent(msg.Role == Role.Ai ? "assistant" : "user", msg.Msg);
            chatMessages.Add(item);
        }

        chatMessages.Add(new MessageContent("user", messageText));
        
        string model = storeUser.Model switch
        {
            Model.GigaChatLite => "GigaChat",
            Model.GigaChatLitePlus => "GigaChat-Plus",
            Model.GigaChatPro => "GigaChat-Pro",
            _ => "GigaChat"
        };

        var messageQuery = new MessageQuery(chatMessages, model);

        Response? messageResponse;
        try
        {
            messageResponse = await gigaChat.CompletionsAsync(messageQuery);
        }
        catch (Exception e)
        {
            await SendBotResponseDependingOnMsgLength(msg: e.Message,
                client: botClient,
                chatId: message.Chat.Id,
                userId: storeUser.Id,
                cancellationToken: cancellationToken,
                replyMsgId: message.MessageId);

            return "";
        }

        return messageResponse?.choices!.FirstOrDefault()?.message?.content ?? "";
    }

    private async Task<string?> GetResponseFromClaude3Model(ITelegramBotClient botClient, StoreUser storeUser,
        Message message, string messageText, CancellationToken cancellationToken)
    {
        AnthropicClient client = new(new APIAuthentication(storeUser.ClaudeApiKey));
        List<Anthropic.SDK.Messaging.Message> chatMessages = [];
        foreach (Models.Message msg in storeUser.Conversation.Where(m => m.Role != Role.System))
        {
            Anthropic.SDK.Messaging.Message item = new()
            {
                Role = msg.Role == Role.Ai ? RoleType.Assistant : RoleType.User,
                Content = msg.Msg
            };
            chatMessages.Add(item);
        }

        chatMessages.Add(new Anthropic.SDK.Messaging.Message()
        {
            Role = RoleType.User,
            Content = messageText
        });

        MessageParameters parameters = new()
        {
            Messages = chatMessages,
            MaxTokens = 4090,
            Model = storeUser.Model switch
            {
                Model.Claude3Sonnet => AnthropicModels.Claude3Sonnet,
                Model.Claude3Opus => AnthropicModels.Claude3Opus,
                Model.Claude3Haiku => AnthropicModels.Claude3Haiku,
                Model.Claude35Sonnet => ClaudeSonnet35LatestApiName,
                Model.Claude35Haiku => ClaudeHaiku35LatestApiName,
                _ => AnthropicModels.Claude3Haiku
            },
            Stream = false,
            Temperature = 1.0m,
        };

        MessageResponse? messageResponse;
        try
        {
            messageResponse = await client.Messages.GetClaudeMessageAsync(parameters, cancellationToken);
        }
        catch (Exception e)
        {
            await SendBotResponseDependingOnMsgLength(msg: e.Message,
                client: botClient,
                chatId: message.Chat.Id,
                userId: storeUser.Id,
                cancellationToken: cancellationToken,
                replyMsgId: message.MessageId);

            return "";
        }

        return messageResponse.Content.FirstOrDefault(content => content.Type == "text")?.Text ?? "";
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
    
    private async Task<string?> GetResponseFromOpenAiLikeModel(ITelegramBotClient client,
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
        
        chatMessages.Add(new ChatMessage(ChatMessageRole.User, messageText));
        
        ChatRequest request = new()
        {
            Model = storeUser.Model switch
            {
                Model.Gpt3 => OpenAiNg.Models.Model.ChatGPTTurbo1106,
                Model.Gpt4OMini => GptOMiniApiName,
                Model.Gpt4O => Gpt4OApiName,
                _ => OpenAiNg.Models.Model.GPT4_1106_Preview
            },
            Messages = chatMessages.ToArray()
        };

        StringBuilder builder = new();
        try
        {
            DateTime lastSentAt = DateTime.Now;
            
            await foreach (ChatResult chatResult in api.Chat.StreamChatEnumerableAsync(request)
                               .WithCancellation(cancellationToken))
            {
                string? content = chatResult.Choices?[0].Delta?.Content;
                if (String.IsNullOrEmpty(content) == false)
                {
                    builder.Append(content);
                }

                if (DateTime.Now - lastSentAt < TimeSpan.FromSeconds(45)) continue;
                
                await client.SendTextMessageAsync(chatId: message.Chat.Id,
                    text: "Receiving a message from the LLM. Please wait...",
                    cancellationToken: cancellationToken);
                
                lastSentAt = DateTime.Now;
            }
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

        return builder.ToString();
    }

    private async Task ProcessUrlIfPresent(string messageText,
        ITelegramBotClient botClient, long chatId, StoreUser storeUser,
        CancellationToken cancellationToken)
    {
        if (String.IsNullOrWhiteSpace(storeUser.ApiKey))
        {
            return;
        }
        
        string transcriptFromLink = await _youtubeTextProcessor.ProcessTextAsync(messageText, storeUser.ApiKey);
        if (messageText == transcriptFromLink)
        {
            return;
        }

        await SendDocumentResponseAsync(transcriptFromLink, botClient, chatId, storeUser.Id,
            cancellationToken, "This is your transcript \ud83d\udc46");
    }

    private static Task<Message> SendBotResponseDependingOnMsgLength(string msg, ITelegramBotClient client,
        long chatId,
        long userId, CancellationToken cancellationToken, int? replyMsgId = null, ParseMode? parseMode = null,
        bool disableWebPagePreview = false)
    {
        if (msg.Length >= MaxMsgLength)
        {
            return SendDocumentResponseAsync(text: msg,
                botClient: client,
                chatId: chatId,
                userId: userId,
                cancellationToken: cancellationToken);
        }
        
        return client.SendTextMessageAsync(chatId: chatId,
            text: msg,
            parseMode: parseMode,
            replyToMessageId: replyMsgId,
            disableWebPagePreview: disableWebPagePreview,
            cancellationToken: cancellationToken);
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

    private async Task<string?> GenerateImage(string prompt, string apiKey, RecraftImgStyle style, RecraftImgSubStyle subStyle)
    {
        _logger.LogInformation("Starting image generation with Recraft AI for prompt: {Prompt}", prompt);
        try
        {
            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            _logger.LogInformation("Using style: {Style}", style.GetDescription());
            _logger.LogInformation("Using sub-style: {SubStyle}", subStyle.GetDescription());

            var payload = new
            {
                prompt,
                style = style.GetDescription(),
                substyle = subStyle != RecraftImgSubStyle.None ? subStyle.GetDescription() : null
            };

            var response = await httpClient.PostAsync(
                "https://external.api.recraft.ai/v1/images/generations",
                new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json")
            );

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadAsStringAsync();
                var jsonResponse = JObject.Parse(result);
                var imageUrl = jsonResponse["data"]?[0]?["url"]?.ToString(); // Access the URL inside the data array
                _logger.LogInformation("Image generated successfully. URL: {ImageUrl}", imageUrl);
                return imageUrl;
            }

            _logger.LogWarning("Failed to generate image. Status code: {StatusCode}", response.StatusCode);
        }
        catch (Exception e)
        {
            _logger.LogWarning("[{MethodName}] {Error}", nameof(GenerateImage), e.Message);
        }

        _logger.LogInformation("Image generation process completed with Recraft AI.");
        return "";
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
        StoreUser? storeUser = GetStoreUser(message.From);
        if (storeUser == null)
        {
            return await botClient.SendTextMessageAsync(message.Chat.Id, "Error getting the user from the store",
                cancellationToken: cancellationToken);
        }

        string usage = "Usage:\n" +
                       "/key - set an OpenAI API key\n" +
                       "/reset_key - reset an OpenAI API key\n" +
                       "/key_claude - set an Anthropic Claude API key\n" +
                       "/reset_key_claude - reset an Anthropic Claude API key\n" +
                       "/key_gigachat - set a Sber Auth key\n" +
                       "/reset_key_gigachat - reset a Sber Auth key\n" +
                       "/key_gemini - set a Gemini API key\n" +
                       "/reset_key_gemini - reset an Google Gemini API key\n" +
                       "/model - choose the GPT model to work with\n" +
                       "/context - set the context message\n" +
                       "/contact - contact the bot admin\n" +
                       "/append - append text to your last message\n" +
                       "/reset_context - reset the context message\n" +
                       "/reset - reset the current conversation\n" +
                       "/toggle_voice - enable/disable voice mode\n" +
                       "/toggle_anew_mode - switch on or off 'anew' mode. With this mode you can start each conversation from the beginning without relying on previous history\n" +
                       "/image - generate an image with help of DALL·E 3\n" +
                       "/usage - view the command list\n" +
                       "/info - show current settings\n" +
                       "/about - about this bot\n" +
                       "/version - version of this bot\n" +
                       "/key_recraft - set a Recraft AI API key\n" +
                       "/reset_key_recraft - reset a Recraft AI API key\n" +
                       "/toggle_recraft_style - switch between Realistic and Digital Illustration styles\n" +
                       "/toggle_recraft_substyle - choose a sub-style for the current style\n" +
                       "/reset_recraft_substyle - remove sub-style from image generation\n";
        
        if (storeUser.IsAdministrator)
        {
            usage = usage + Environment.NewLine + "---\n" +
                    "/allow - allow user\n" +
                    "/deny - deny user\n" +
                    "/users - show active users\n" +
                    "/all_users - show all users\n" +
                    "/broadcast - broadcast the version message";
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
        string errorMessage = exception switch
        {
            ApiRequestException apiRequestException => $"Telegram API Error:\n[{apiRequestException.ErrorCode}]\n{apiRequestException.Message}",
            _ => exception.ToString()
        };

        _logger.LogInformation("HandleError: {ErrorMessage}", errorMessage);

        // Cooldown in case of network connection error
        if (exception is RequestException)
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
    }

    private async Task<Message> SetKeyRecraftCommand(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
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
                "After '/key_recraft' command you must input your Recraft AI API key. Try again.",
                cancellationToken: cancellationToken);
        }

        string apiKey = strings[1];
        if (String.IsNullOrWhiteSpace(apiKey))
        {
            return await botClient.SendTextMessageAsync(message.Chat.Id,
                "After '/key_recraft' command you must input your Recraft AI API key. Try again.",
                cancellationToken: cancellationToken);
        }

        storeUser.RecraftApiKey = apiKey;
        _userRepository.UpdateUser(storeUser);

        return await botClient.SendTextMessageAsync(message.Chat.Id, "Recraft AI API key was set.",
            cancellationToken: cancellationToken);
    }

    private async Task<Message> ResetKeyRecraftCommand(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
    {
        StoreUser? storeUser = GetStoreUser(message.From);
        if (storeUser == null)
        {
            return await botClient.SendTextMessageAsync(message.Chat.Id, "Error getting the user from the store.",
                cancellationToken: cancellationToken);
        }

        storeUser.RecraftApiKey = "";
        _userRepository.UpdateUser(storeUser);

        return await botClient.SendTextMessageAsync(message.Chat.Id, "Recraft AI API key was reset.",
            cancellationToken: cancellationToken);
    }

    private async Task<Message> ToggleRecraftStyleCommand(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
    {
        StoreUser? storeUser = GetStoreUser(message.From);
        if (storeUser == null)
        {
            return await botClient.SendTextMessageAsync(message.Chat.Id, "Error getting the user from the store.",
                cancellationToken: cancellationToken);
        }
        
        if (String.IsNullOrWhiteSpace(storeUser.RecraftApiKey))
        {
            return await botClient.SendTextMessageAsync(message.Chat.Id, 
                "Your Recraft API key is not set. Use '/key_recraft' command and set key.",
                cancellationToken: cancellationToken);
        }

        // Toggle between Realistic and DigitalIllustration
        storeUser.RecraftImgStyle = storeUser.RecraftImgStyle == RecraftImgStyle.Realistic 
            ? RecraftImgStyle.DigitalIllustration 
            : RecraftImgStyle.Realistic;

        // Reset sub-style when changing main style
        storeUser.RecraftImgSubStyle = storeUser.RecraftImgStyle.GetSubStyles().FirstOrDefault();
        _userRepository.UpdateUser(storeUser);

        return await botClient.SendTextMessageAsync(message.Chat.Id,
            $"Current Recraft style is: {storeUser.RecraftImgStyle.GetDescription()}\n" +
            $"Current sub-style is: {storeUser.RecraftImgSubStyle.GetDescription()}",
            cancellationToken: cancellationToken);
    }

    private async Task<Message> ToggleRecraftSubStyleCommand(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
    {
        StoreUser? storeUser = GetStoreUser(message.From);
        if (storeUser == null)
        {
            return await botClient.SendTextMessageAsync(message.Chat.Id, "Error getting the user from the store.",
                cancellationToken: cancellationToken);
        }
        
        if (String.IsNullOrWhiteSpace(storeUser.RecraftApiKey))
        {
            return await botClient.SendTextMessageAsync(message.Chat.Id, 
                "Your Recraft API key is not set. Use '/key_recraft' command and set key.",
                cancellationToken: cancellationToken);
        }

        var availableSubStyles = storeUser.RecraftImgStyle.GetSubStyles().ToArray();
        var currentIndex = Array.IndexOf(availableSubStyles, storeUser.RecraftImgSubStyle);
        
        // Move to next sub-style or wrap around to first
        storeUser.RecraftImgSubStyle = availableSubStyles[(currentIndex + 1) % availableSubStyles.Length];
        _userRepository.UpdateUser(storeUser);

        var subStylesList = string.Join("\n", availableSubStyles.Select(s => $"- {s.GetDescription()}"));
        
        var currentSubStyleText = storeUser.RecraftImgSubStyle == RecraftImgSubStyle.None 
            ? "no sub-style" 
            : storeUser.RecraftImgSubStyle.GetDescription();
        
        return await botClient.SendTextMessageAsync(message.Chat.Id,
            $"Current Recraft style is: {storeUser.RecraftImgStyle.GetDescription()}\n" +
            $"Current sub-style is: {currentSubStyleText}\n\n" +
            $"Available sub-styles for {storeUser.RecraftImgStyle.GetDescription()}:\n{subStylesList}",
            cancellationToken: cancellationToken);
    }

    private async Task<Message> ResetRecraftSubStyleCommand(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
    {
        StoreUser? storeUser = GetStoreUser(message.From);
        if (storeUser == null)
        {
            return await botClient.SendTextMessageAsync(message.Chat.Id, "Error getting the user from the store.",
                cancellationToken: cancellationToken);
        }
        
        if (String.IsNullOrWhiteSpace(storeUser.RecraftApiKey))
        {
            return await botClient.SendTextMessageAsync(message.Chat.Id, 
                "Your Recraft API key is not set. Use '/key_recraft' command and set key.",
                cancellationToken: cancellationToken);
        }

        storeUser.RecraftImgSubStyle = RecraftImgSubStyle.None;
        _userRepository.UpdateUser(storeUser);
        
        return await botClient.SendTextMessageAsync(message.Chat.Id,
            $"Recraft sub-style has been reset to none.\n" +
            $"Current style is: {storeUser.RecraftImgStyle.GetDescription()}",
            cancellationToken: cancellationToken);
    }
}