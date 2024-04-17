using System.Diagnostics.CodeAnalysis;
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
using OpenAiNg.ChatFunctions;
using OpenAiNg.Images;
using SgptBot.Models;
using SgptBot.Shared.Models;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using Data = OpenAiNg.Images.Data;
using File = Telegram.Bot.Types.File;
using Message = Telegram.Bot.Types.Message;
using Model = SgptBot.Models.Model;

namespace SgptBot.Services;

public class UpdateHandler : IUpdateHandler
{
    private const int MaxMsgLength = 4000;
    private readonly ITelegramBotClient _botClient;
    private readonly ILogger<UpdateHandler> _logger;
    private readonly ApplicationSettings _appSettings;
    private readonly IUserRepository _userRepository;
    private readonly IYoutubeTextProcessor _youtubeTextProcessor;
    private readonly IVectorStoreMiddleware _vectorStoreMiddleware;
    private readonly ISummarizationProvider _summarizationProvider;
    private readonly IGeminiProvider _geminiProvider;
    private readonly ITokenizer _tokenizer;
    private readonly string[] _allowedExtensions = [".md", ".txt", ".cs", ".zip", ".html", ".htm", ".pdf", ".mp3"];

    public UpdateHandler(ITelegramBotClient botClient, ILogger<UpdateHandler> logger, ApplicationSettings appSettings,
        IUserRepository userRepository, IYoutubeTextProcessor youtubeTextProcessor, IVectorStoreMiddleware vectorStoreMiddleware, 
        ISummarizationProvider summarizationProvider, IGeminiProvider geminiProvider)
    {
        _botClient = botClient;
        _logger = logger;
        _appSettings = appSettings;
        _userRepository = userRepository;
        _youtubeTextProcessor = youtubeTextProcessor;
        _vectorStoreMiddleware = vectorStoreMiddleware;
        _summarizationProvider = summarizationProvider;
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
            if (storeUser is {ContextFilterMode: false})
            {
                messageText = textFromDocumentMessage + (String.IsNullOrEmpty(message.Caption) == false
                    ? $"\n{message.Caption}"
                    : String.Empty);
            }
            
            if (storeUser is {ContextFilterMode: true})
            {
                await StoreNewMemory(storeUser, textFromDocumentMessage, message.Document!.FileName);
                messageText = message.Caption ?? "";
            }
        }

        string processedMsg = await ProcessUrlIfPresent(messageText: messageText,
            botClient: client,
            chatId: message.Chat.Id,
            storeUser: storeUser!,
            cancellationToken: cancellationToken);
        
        if (await StoreUrlTranscriptInMemory(message, client, processedMsg, messageText, storeUser,
                cancellationToken)) return;
        
        messageText = processedMsg;

        if (String.IsNullOrWhiteSpace(messageText))
        {
            await client.SendTextMessageAsync(message.Chat.Id,
                "Your message was empty. Try again.",
                cancellationToken: cancellationToken);
            _logger.LogWarning("[{MethodName}] Message is empty. Return.", nameof(BotOnMessageReceived));
            return;
        }

        messageText = await EnrichUserPromptWithRelevantContext(storeUser, messageText);

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
            "/history"               => HistoryCommand(_botClient, message, cancellationToken),
            "/about"                 => AboutCommand(_botClient, message, cancellationToken),
            "/version"               => VersionCommand(_botClient, message, cancellationToken),
            "/users"                 => UsersCommand(_botClient, message, cancellationToken),
            "/all_users"             => AllUsersCommand(_botClient, message, cancellationToken),
            "/allow"                 => AllowCommand(_botClient, message, cancellationToken),
            "/deny"                  => DenyCommand(_botClient, message, cancellationToken),
            "/toggle_voice"          => ToggleVoiceCommand(_botClient, message, cancellationToken),
            "/toggle_img_quality"    => ToggleImgQualityCommand(_botClient, message, cancellationToken),
            "/toggle_img_style"      => ToggleImgStyleCommand(_botClient, message, cancellationToken),
            "/toggle_anew_mode"      => ToggleAnewMode(_botClient, message, cancellationToken),
            "/toggle_context_filter_mode" => ToggleContextFilterModeCommand(message, cancellationToken),
            "/select_memory"         => SelectMemoryCommand(message, cancellationToken),
            "/clear_working_memory"  => ClearWorkingMemoryCommand(message, cancellationToken),
            "/summarize"             => SummarizeCommand(message, cancellationToken),
            "/image"                 => ImageCommand(_botClient, message, cancellationToken),
            "/append"                => AppendCommand(_botClient, message, cancellationToken),
            _                        => TalkToModelCommand(_botClient, message, messageText, cancellationToken)
        };
        Message sentMessage = await action;
        _logger.LogInformation("The message was sent with id: {SentMessageId}", sentMessage.MessageId);
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
        const string name = "LLM Bot";
        string version = GetVersionWithDateTime();
        string msg = $"""
                       🤖 *{name} v.{version} Update* 🚀

                       Hello everyone! We've just rolled out an exciting update to *{name}*. Here’s what’s new in version *{version}*:

                       ✨ *New Features*:
                       - Added new Google Gemini 1.5 Pro model: Now the bot can work with the Google Gemini 1.5 Pro model. Try it out!
                       
                       💬 *Feedback*:
                       We're always looking to improve and value your feedback. If you have any suggestions or encounter any issues, please let us know through (use /contact command).

                       Stay tuned for more updates, and thank you for using *{name}*!
                       """;
        
        return await botClient.SendTextMessageAsync(message.Chat.Id,
            msg,
            parseMode: ParseMode.Markdown,
            disableNotification: true,
            cancellationToken: cancellationToken);
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

    private async Task<bool> StoreUrlTranscriptInMemory(Message message, ITelegramBotClient client,
        string processedMsg, string? messageText, StoreUser? storeUser, CancellationToken cancellationToken)
    {
        if (processedMsg.Equals(messageText, StringComparison.OrdinalIgnoreCase) ||
            processedMsg.Length <= messageText!.Length || !storeUser!.ContextFilterMode) return false;
        
        await StoreNewMemory(storeUser, processedMsg, $"Url_{Guid.NewGuid()}");
        await client.SendTextMessageAsync(message.Chat.Id,
            "The transcript form the url was stored in the memory.",
            cancellationToken: cancellationToken);
        
        return true;
    }

    private async Task<Message> SummarizeCommand(Message message, CancellationToken cancellationToken)
    {
        StoreUser? storeUser = GetStoreUser(message.From);
        if (await ValidateUser(storeUser, _botClient, message.Chat.Id) == false)
        {
            return await _botClient.SendTextMessageAsync(message.Chat.Id,
                "Error: User validation failed.",
                cancellationToken: cancellationToken);
        }

        StringBuilder builder = new();
        foreach (Models.Message msg in storeUser!.Conversation.Where(m => m.Role != Role.System))
        {
            builder.AppendLine($"{(msg.Role == Role.User ? "USER:\n" : "AI:\n")}{msg.Msg}");
        }

        string context = builder.ToString();
        
        if (String.IsNullOrWhiteSpace(context))
        {
            return await _botClient.SendTextMessageAsync(message.Chat.Id,
                "Warning: context is empty. Nothing to summarize. Try again.",
                cancellationToken: cancellationToken);
        }

        string summary = await _summarizationProvider.GetSummary(storeUser.ApiKey, storeUser.Model, context);
        
        if (String.IsNullOrWhiteSpace(summary))
        {
            return await _botClient.SendTextMessageAsync(message.Chat.Id,
                "Warning: summary is empty. Try again.",
                cancellationToken: cancellationToken);
        }

        return await SendBotResponseDependingOnMsgLength($"Summary:\n\n{summary}", _botClient, message.Chat.Id, storeUser.Id,
            cancellationToken);
    }

    private async Task<Message> ClearWorkingMemoryCommand(Message message, CancellationToken cancellationToken)
    {
        StoreUser? storeUser = GetStoreUser(message.From);
        if (await ValidateUser(storeUser, _botClient, message.Chat.Id) == false)
        {
            return await _botClient.SendTextMessageAsync(message.Chat.Id,
                "Error: User validation failed.",
                cancellationToken: cancellationToken);
        }
        
        if (storeUser!.ContextFilterMode == false)
        {
            return await _botClient.SendTextMessageAsync(message.Chat.Id,
                "In order to clear the working memory, please active the context filter mode (use '/toggle_context_filter_mode' command)",
                cancellationToken: cancellationToken);
        }
        
        storeUser.WorkingMemory.Clear();
        _userRepository.UpdateUser(storeUser);
        
        await SendInfoAboutAvailableMemories(message, storeUser, cancellationToken);
        
        return await _botClient.SendTextMessageAsync(message.Chat.Id,
            "Working memory was cleared.",
            cancellationToken: cancellationToken);
    }

    private async Task<Message> SelectMemoryCommand(Message message, CancellationToken cancellationToken)
    {
        StoreUser? storeUser = GetStoreUser(message.From);
        if (await ValidateUser(storeUser, _botClient, message.Chat.Id) == false)
        {
            return await _botClient.SendTextMessageAsync(message.Chat.Id,
                "Error: User validation failed.",
                cancellationToken: cancellationToken);
        }
        
        if (storeUser!.ContextFilterMode == false)
        {
            return await _botClient.SendTextMessageAsync(message.Chat.Id,
                "In order to select the memory, please active the context filter mode (use '/toggle_context_filter_mode' command)",
                cancellationToken: cancellationToken);
        }
        
        string[] strings = message.Text!.Split(' ');
        if (strings.Length < 2)
        {
            await SendInfoAboutAvailableMemories(message, storeUser, cancellationToken);

            return await _botClient.SendTextMessageAsync(message.Chat.Id,
                "After the '/select_memory' command you must input the memory name (or multiple names, separated by comma). Try again.",
                cancellationToken: cancellationToken);
        }

        string contextNames = String.Join(' ', strings.Skip(1));
        if (String.IsNullOrWhiteSpace(contextNames))
        {
            await SendInfoAboutAvailableMemories(message, storeUser, cancellationToken);

            return await _botClient.SendTextMessageAsync(message.Chat.Id,
                "After the '/select_memory' command you must input the memory name (or multiple names, separated by comma). Try again.",
                cancellationToken: cancellationToken);
        }

        string[] individualNames = contextNames
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(name => name.Trim()).ToArray();
        
        storeUser.WorkingMemory.Clear();
        
        foreach (string name in individualNames)
        {
            VectorMemoryItem? memoryItem = storeUser.MemoryStorage.FirstOrDefault(item => item.MemoryId == name);
            if (memoryItem == null)
            {
                continue;
            }

            if (storeUser.WorkingMemory.FirstOrDefault(item => item.MemoryId == name) != null)
            {
                continue;
            }

            storeUser.WorkingMemory.Add(memoryItem);
        }
        
        _userRepository.UpdateUser(storeUser);
        
        return await SendInfoAboutAvailableMemories(message, storeUser, cancellationToken);
    }

    private async Task<Message> SendInfoAboutAvailableMemories(Message message,
        StoreUser storeUser, CancellationToken cancellationToken)
    {
        string availableMemories = String.Join("\n", storeUser.MemoryStorage.Select(item => $"- `{item.MemoryId}`").ToArray());
        string currentWorkingMemoryItems =
            String.Join("\n", storeUser.WorkingMemory.Select(item => $"- `{item.MemoryId}`").ToArray());

        return await SendBotResponseDependingOnMsgLength(msg: $"""
                                                        You can add a memory by uploading a text file (.txt, .md or .zip archive with text files) to the bot. 
                                                        Remember — you must use 'Context Filter' mode.
                                                        
                                                        *Available memories ({"item".ToQuantity(storeUser.MemoryStorage.Count)}):*

                                                        {(availableMemories.Length > 0 ? availableMemories : "_...empty..._")}

                                                        *Current working memory items ({"item".ToQuantity(storeUser.WorkingMemory.Count)}):*

                                                        {(currentWorkingMemoryItems.Length > 0 ? currentWorkingMemoryItems : "_...empty..._")}
                                                        """,
            _botClient, message.Chat.Id, storeUser.Id, cancellationToken, parseMode: ParseMode.Markdown);
    }

    private async Task StoreNewMemory(StoreUser storeUser, string? text, string? fileName)
    {
        VectorMemoryItem? memoryItem = await _vectorStoreMiddleware.Memorize(user: storeUser,
            memories: text,
            fileName: fileName);
        if (memoryItem != null)
        {
            storeUser.MemoryStorage.Add(memoryItem);
            storeUser.WorkingMemory = [memoryItem];
            _userRepository.UpdateUser(storeUser);
        }
    }

    private async Task<string> EnrichUserPromptWithRelevantContext(StoreUser? storeUser, string userPrompt)
    {
        if (userPrompt.StartsWith('/')) return userPrompt;
        if (storeUser == null) return userPrompt;
        if (storeUser.Model is not (Model.Gpt3 or Model.Gpt4)) return userPrompt;
        if (String.IsNullOrWhiteSpace(storeUser.ApiKey)) return userPrompt;
        if (storeUser is not {ContextFilterMode: true, WorkingMemory.Count: > 0}) return userPrompt;
        string[] context =
            await _vectorStoreMiddleware.RecallMemoryFromVectorContext(storeUser, userPrompt);
        return context.Length > 0
            ? $"""
               This is additional context. Use this to create a high-quality answer.
               This context may be irrelevant. If the context is irrelevant, simply disregard it.

               ADDITIONAL CONTEXT START
               {String.Join("\n", context)}
               ADDITIONAL CONTEXT END

               Question: {userPrompt}
               """
            : userPrompt;
    }

    private async Task<Message> ToggleContextFilterModeCommand(Message message, CancellationToken cancellationToken)
    {
        StoreUser? storeUser = GetStoreUser(message.From);
        if (await ValidateUser(storeUser, _botClient, message.Chat.Id) == false)
        {
            return await _botClient.SendTextMessageAsync(message.Chat.Id,
                "Error: User validation failed.",
                cancellationToken: cancellationToken);
        }

        storeUser!.ContextFilterMode = !storeUser.ContextFilterMode;
        storeUser.AnewMode = storeUser.ContextFilterMode;
        
        _userRepository.UpdateUser(storeUser);
        
        return await _botClient.SendTextMessageAsync(message.Chat.Id, 
            $"Context filter mode is: {(storeUser.ContextFilterMode ? "`on`" : "`off`")}\nAnew mode is: {(storeUser.AnewMode ? "`on`" : "`off`")}\n\nTurn on the 'Context Filter' mode, and the bot will keep conversations simple and to the point. This mode makes the bot act like a filter, picking out only the most important parts of what you say. It helps avoid confusion by ignoring the bits that aren't needed for understanding. With this mode, the bot's answers are clear and focused, making sure you get the information you need quickly and easily.",
            cancellationToken: cancellationToken, parseMode: ParseMode.Markdown);
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
            case Model.Gpt3 or Model.Gpt4 when String.IsNullOrWhiteSpace(user.ApiKey):
                await client.SendTextMessageAsync(chatId,
                    "Your OpenAI API key is not set. Use '/key' command and set key.");
                return false;
            case Model.Claude21 or Model.Claude3Opus or Model.Claude3Sonnet or Model.Claude3Haiku when String.IsNullOrWhiteSpace(user.ClaudeApiKey):
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
             storeUser.Model == Model.Claude3Opus ||
             storeUser.Model == Model.Claude3Haiku ||
             storeUser.Model == Model.Claude3Sonnet) ==
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
            {
                visionModelResponse =
                    await GetVisionResponseFromOpenAiModel(client, message, storeUser, base64Image, cancellationToken);
                break;
            }
            case Model.Claude3Haiku:
            case Model.Claude3Sonnet:
            case Model.Claude3Opus:
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
        object payload = GetPayload(base64Image, message.Caption);

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
                $"{i + 1}) Id: {user.Id}; First name: {user.FirstName}; Last name: {user.LastName}; Username: {user.UserName}; Is blocked: {user.IsBlocked}; Last activity: {lastActivityMessage} ago; Model: {user.Model};");
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
            .Where(user => user.History.Any(msg => msg.Role == Role.Ai) ||
                           user.Conversation.Any(msg => msg.Role == Role.Ai))
            .OrderByDescending(user => user.ActivityTime)
            .Take(15)
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
                $"{i + 1}) Id: {user.Id}; First name: {user.FirstName}; Last name: {user.LastName}; Username: {user.UserName}; Is blocked: {user.IsBlocked}; Last activity: {lastActivityMessage} ago; Model: {user.Model};");
            builder.AppendLine();
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
            "This bot allows you to use different LLM models in order to receive useful information from them.",
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
            DateOnly? dateOnly =
                await TryToCallFuncApiAndGetDate(client, message, storeUser, datePrompt, cancellationToken);
            if (dateOnly != null)
            {
                date = (DateOnly) dateOnly;
            }
            else
            {
                return await client.SendTextMessageAsync(message.Chat.Id,
                    "Try '/history' command again.",
                    cancellationToken: cancellationToken);
            }
        }

        SgptBot.Models.Message[] messagesFromHistory = storeUser.History
            .Where(msg => msg.Date == date).ToArray();
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

    private async Task<DateOnly?> TryToCallFuncApiAndGetDate(ITelegramBotClient client, Message message,
        StoreUser storeUser, string datePrompt, CancellationToken cancellationToken)
    {
        const string dontUnderstandMsg = "I do not understand your date format.";
        if (String.IsNullOrWhiteSpace(storeUser.ApiKey))
        {
            await client.SendTextMessageAsync(message.Chat.Id,
                dontUnderstandMsg,
                cancellationToken: cancellationToken);
            return null;
        }

        GetExtractDateFunctionResult? extractDateFunctionResult =
            await GetDateFunctionCallResult(storeUser, datePrompt);
        if (extractDateFunctionResult == null)
        {
            await client.SendTextMessageAsync(message.Chat.Id,
                dontUnderstandMsg,
                cancellationToken: cancellationToken);
            return null;
        }

        if (extractDateFunctionResult.Status == false)
        {
            await client.SendTextMessageAsync(message.Chat.Id,
                dontUnderstandMsg,
                cancellationToken: cancellationToken);
            return null;
        }

        bool tryParseResult = DateOnly.TryParse(extractDateFunctionResult.Date, out DateOnly parsedDate);
        if (tryParseResult == false)
        {
            await client.SendTextMessageAsync(message.Chat.Id,
                dontUnderstandMsg,
                cancellationToken: cancellationToken);
            return null;
        }

        return parsedDate;
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

        return builder.ToString();
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

    [SuppressMessage("ReSharper", "StringLiteralTypo")]
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
        InlineKeyboardButton claude3OpusButton = new("Anthropic Claude 3 Opus") { CallbackData = "/model claude3opus"};
        InlineKeyboardButton claude3SonnetButton = new("Anthropic Claude 3 Sonnet") { CallbackData = "/model claude3sonnet"};
        InlineKeyboardButton claude3HaikuButton = new("Anthropic Claude 3 Haiku") { CallbackData = "/model claude3haiku"};
        InlineKeyboardButton gigaChatLiteButton = new("Sber GigaChat Lite") { CallbackData = "/model gigachatlite"};
        InlineKeyboardButton gigaChatLitePlusButton = new("Sber GigaChat Lite+") { CallbackData = "/model gigachatliteplus"};
        InlineKeyboardButton gigaChatProButton = new("Sber GigaChat Pro") { CallbackData = "/model gigachatpro"};
        InlineKeyboardButton gemini15ProButton = new("Google Gemini 1.5 Pro") { CallbackData = "/model gemini15pro" };

        InlineKeyboardButton[] row1 = [gpt3Button];
        InlineKeyboardButton[] row2 = [gpt4Button];
        InlineKeyboardButton[] row3 = [claudeButton];
        InlineKeyboardButton[] row5 = [claude3OpusButton];
        InlineKeyboardButton[] row6 = [claude3SonnetButton];
        InlineKeyboardButton[] row7 = [claude3HaikuButton];
        InlineKeyboardButton[] row8 = [gigaChatLiteButton];
        InlineKeyboardButton[] row9 = [gigaChatLitePlusButton];
        InlineKeyboardButton[] row10 = [gigaChatProButton];
        InlineKeyboardButton[] row11 = [gemini15ProButton];

        // Buttons by rows
        InlineKeyboardButton[][] buttons = [row1, row2, row3, row5, row6, row7, row8, row9, row10, row11];
    
        // Keyboard
        InlineKeyboardMarkup inlineKeyboard = new(buttons);
            
        return await botClient.SendTextMessageAsync(message.Chat.Id,
            """
            Select the model that you want to use.

            1) *GPT-3.5* models can understand and generate natural language or code. Most capable and cost effective model in the GPT-3.5 family.
            
            2) *GPT-4 Turbo* is a large multimodal model (accepting text inputs and emitting text outputs today, with image inputs coming in the future) that can solve difficult problems with greater accuracy than any previous models, thanks to its broader general knowledge and advanced reasoning capabilities.
            
            3) *Claude 2.1* is a language model that can generate various types of text-based outputs from user's prompts. You can use Claude 2 for e-commerce tasks, creating email templates and generating code in popular programming languages.
            
            4) *Claude 3 Opus* is a powerful model, delivering state-of-the-art performance on highly complex tasks and demonstrating fluency and human-like understanding.
            
            5) *Claude 3 Sonnet* strikes the ideal balance between intelligence and speed—particularly for high-volume tasks. For the vast majority of workloads, Sonnet is 2x faster than Claude 2 and Claude 2.1 with higher levels of intelligence, and delivers strong performance at a lower cost compared to its peers.
            
            6) *Claude 3 Haiku*, the fastest and most affordable model in its intelligence class. With state-of-the-art vision capabilities and strong performance on industry benchmarks, Haiku is a versatile solution for a wide range of enterprise applications.
            
            7) *Sber GigaChat Lite* model is suitable for solving simpler tasks that require maximum operating speed.
            
            8) *Sber GigaChat Lite+* model is suitable for tasks that require processing large amounts of data. For example: summarization of articles or call transcriptions, extraction of information from documents.
            
            9) *Sber GigaChat Pro* model better follows complex instructions and can perform more complex tasks: significantly improved quality of summarization, rewriting and editing of texts, answering various questions. The model is well-versed in many applied domains, particularly in economic and legal issues.
            
            7) *Google Gemini 1.5 Pro* a mid-size multimodal model, optimized for scaling across a wide-range of tasks.
            """,
            replyMarkup: inlineKeyboard,
            parseMode: ParseMode.Markdown,
            cancellationToken: cancellationToken);
    }

    private async Task<Message> SetSelectedModel(ITelegramBotClient botClient, long chatId,
        string[] strings, StoreUser storeUser, CancellationToken cancellationToken)
    {
        string modelName = strings[1];
        if (String.IsNullOrWhiteSpace(modelName))
        {
            return await botClient.SendTextMessageAsync(chatId,
                "After '/model' command you must input the model name.\nModel name must be either: 'gpt3.5', 'gpt4', 'claude21', 'claude3opus' or 'custom'.\nTry again.",
                cancellationToken: cancellationToken);
        }

        if (modelName.ToLower().Equals("gpt3.5") == false &&
            modelName.ToLower().Equals("gpt4") == false &&
            modelName.ToLower().Equals("claude21") == false &&
            modelName.ToLower().Equals("claude3opus") == false &&
            modelName.ToLower().Equals("claude3sonnet") == false &&
            modelName.ToLower().Equals("claude3haiku") == false &&
            modelName.ToLower().Equals("gigachatlite") == false &&
            modelName.ToLower().Equals("gigachatliteplus") == false &&
            modelName.ToLower().Equals("gigachatpro") == false &&
            modelName.ToLower().Equals("gemini15pro") == false)
        {
            return await botClient.SendTextMessageAsync(chatId,
                "After '/model' command you must input the model name.\nModel name must be either: 'gpt3.5', 'gpt4', 'claude21', 'claude3opus', 'claude3sonnet', 'claude3haiku', 'gigachatlite', 'gigachatliteplus', 'gigachatpro' or 'gemini15pro'.\nTry again.",
                cancellationToken: cancellationToken);
        }

        Model selectedModel = modelName.ToLower() switch
        {
            "gpt3.5" => Model.Gpt3,
            "gpt4" => Model.Gpt4,
            "claude21" => Model.Claude21,
            "claude3opus" => Model.Claude3Opus,
            "claude3sonnet" => Model.Claude3Sonnet,
            "claude3haiku" => Model.Claude3Haiku,
            "gigachatlite" => Model.GigaChatLite,
            "gigachatliteplus" => Model.GigaChatLitePlus,
            "gigachatpro" => Model.GigaChatPro,
            "gemini15pro" => Model.Gemini15Pro,
            _ => Model.Gpt3
        };

        storeUser.Model = selectedModel;
        _userRepository.UpdateUser(storeUser);

        string mName = GetModelName(selectedModel);
        
        return await botClient.SendTextMessageAsync(
            chatId, 
            $"Model '{mName}' was set.",
            cancellationToken: cancellationToken);
    }

    private static string GetModelName(Model selectedModel)
    {
        return selectedModel switch
        {
            Model.Gpt3 => "GPT-3.5 Turbo",
            Model.Gpt4 => "GPT-4 Turbo",
            Model.Claude21 => "Claude 2.1",
            Model.Claude3Sonnet => "Claude 3 Sonnet",
            Model.Claude3Opus => "Claude 3 Opus",
            Model.Claude3Haiku => "Claude 3 Haiku",
            Model.GigaChatLite => "GigaChat Lite",
            Model.GigaChatLitePlus => "GigaChat Lite+",
            Model.GigaChatPro => "GigaChat Pro",
            Model.Gemini15Pro => "Gemini 1.5 Pro",
            _ => throw new ArgumentOutOfRangeException()
        };
    }

    private async Task<Message> InfoCommand(Message message, CancellationToken cancellationToken)
    {
        StoreUser? storeUser = GetStoreUser(message.From);
        if (storeUser == null)
        {
            return await _botClient.SendTextMessageAsync(message.Chat.Id, "Error getting the user from the store.",
                cancellationToken: cancellationToken);
        }

        StringBuilder builder = new();
        foreach (Models.Message msg in storeUser.Conversation)
        {
            builder.Append(msg.Msg);
        }

        int tokenCount = GetTokenCount(builder.ToString());
        
        string mName = GetModelName(storeUser.Model);

        return await _botClient.SendTextMessageAsync(message.Chat.Id,
            $"First name: `{storeUser.FirstName}`\n" +
            $"Last name: `{storeUser.LastName}`\n" +
            $"Username: `{storeUser.UserName}`\n" +
            $"OpenAI API key: `{storeUser.ApiKey}`\n" +
            $"Claude API key: `{storeUser.ClaudeApiKey}`\n" +
            $"GigaChat API key: `{storeUser.GigaChatApiKey}`\n" +
            $"Gemini API key: `{storeUser.GeminiApiKey}`\n" +
            $"Model: `{mName}`\n" +
            $"Image quality: `{storeUser.ImgQuality.ToString().ToLower()}`\n" +
            $"Image style: `{storeUser.ImgStyle.ToString().ToLower()}`\n" +
            $"Voice mode: `{(storeUser.VoiceMode ? "on" : "off")}`\n" +
            $"Context filter mode: `{(storeUser.ContextFilterMode ? "on" : "off" )}`\n" +
            $"Anew mode: `{(storeUser.AnewMode ? "on" : "off")}`\n" +
            $"Current context window size (number of tokens): `{tokenCount}`\n" +
            $"Context prompt: {storeUser.Conversation.FirstOrDefault(msg => msg.Role == Role.System)?.Msg ?? "_<empty>_"}",
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
            Model.Gpt3 or Model.Gpt4 => await GetResponseFromOpenAiLikeModel(botClient, storeUser,
                message, messageText, cancellationToken),
            Model.Claude3Opus or Model.Claude3Sonnet or Model.Claude3Haiku => await GetResponseFromClaude3Model(botClient, storeUser, message, messageText,
                cancellationToken),
            Model.GigaChatLite or Model.GigaChatLitePlus or Model.GigaChatPro => await GetResponseFromGigaChatModel(
                botClient, storeUser, message, messageText, cancellationToken),
            Model.Gemini15Pro => await GetResponseFromGeminiModel(storeUser, messageText),
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

    private async Task<string?> GetResponseFromGeminiModel(StoreUser storeUser, string messageText)
    {
        List<GeminiContent> geminiContents = [];
        foreach (Models.Message msg in storeUser.Conversation.Where(m => m.Role != Role.System))
        {
            var item = new GeminiContent() { Role = msg.Role == Role.Ai ? "model" : "user", Parts = [new GeminiPart() { Text = msg.Msg }] };
            geminiContents.Add(item);
        }

        geminiContents.Add(new GeminiContent() { Role = "user", Parts = [new GeminiPart() { Text = messageText }] });

        GeminiConversation geminiConversation = new() { Contents = [.. geminiContents] };

        string response = await _geminiProvider.GetAnswerFroGemini(storeUser.GeminiApiKey, geminiConversation);
        return response;
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

    private async Task<string> ProcessUrlIfPresent(string messageText,
        ITelegramBotClient botClient, long chatId, StoreUser storeUser,
        CancellationToken cancellationToken)
    {
        if (String.IsNullOrWhiteSpace(storeUser.ApiKey))
        {
            return messageText;
        }
        
        string transcriptFromLink = await _youtubeTextProcessor.ProcessTextAsync(messageText, storeUser.ApiKey);
        if (messageText == transcriptFromLink)
        {
            return messageText;
        }

        await SendDocumentResponseAsync(transcriptFromLink, botClient, chatId, storeUser.Id,
            cancellationToken, "This is your transcript \ud83d\udc46");
        return $"This is the transcript from my web link:\n\n######\n{transcriptFromLink}\n######\n\nI want to ask you about this transcript... Wait for my question. Just say - 'Ask me about this transcript...'";
    }

    private static Task<Message> SendBotResponseDependingOnMsgLength(string msg, ITelegramBotClient client,
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
        
        return client.SendTextMessageAsync(chatId: chatId,
            text: msg,
            parseMode: parseMode,
            replyToMessageId: replyMsgId,
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
                       "/history - view the conversation history\n" +
                       "/reset - reset the current conversation\n" +
                       "/summarize - get a summary from the current conversation\n" +
                       "/toggle_voice - enable/disable voice mode\n" +
                       "/toggle_img_quality - switch between standard or HD image quality\n" +
                       "/toggle_img_style - switch between vivid or natural image style\n" +
                       "/toggle_anew_mode - switch on or off 'anew' mode. With this mode you can start each conversation from the beginning without relying on previous history\n" +
                       "/toggle_context_filter_mode - switch on or off 'context filter' mode. This mode simplifies interactions by narrowing down the context to just the essential parts for quick and clear communication\n" +
                       "/select_memory - add previously uploaded memory into the working memory. Works only with the 'context filter' mode\n" +
                       "/clear_working_memory - clear current working memory. Works only with the 'context filter' mode\n" +
                       "/image - generate an image with help of DALL·E 3\n" +
                       "/usage - view the command list\n" +
                       "/info - show current settings\n" +
                       "/about - about this bot\n" +
                       "/version - version of this bot";
        
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
}