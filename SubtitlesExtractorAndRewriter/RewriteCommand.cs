using CliFx;
using CliFx.Attributes;
using CliFx.Infrastructure;
using OpenAI_API;
using OpenAI_API.Chat;

namespace SubtitlesExtractorAndRewriter;

[Command("rewrite", Description = "Rewrite text with help of ChatGPT")]
public class RewriteCommand : ICommand
{
    [CommandParameter(0, Description = "Text that need to be rewritten")]
    public string Text { get; init; }
    
    [CommandOption("prompt", Description = "What ChatGPT should do with text?", IsRequired = false)]
    public string Prompt { get; init; } = "Rewrite in more simple English words. Translate to English only if needed. Use simple language and grammar.";

    public async ValueTask ExecuteAsync(IConsole console)
    {
        string key = Environment.GetEnvironmentVariable("OPENAI_KEY");
        if (String.IsNullOrWhiteSpace(key))
        {
            await console.Output.WriteLineAsync("OPENAI_KEY env variable not set");
            Environment.Exit(1);
        }
        
        if (String.IsNullOrWhiteSpace(Text))
        {
            await console.Output.WriteAsync("Provide some text");
            Environment.Exit(1);
        }
        
        List<string> chunks = Library.SplitTextIntoChunks(Text, 3000);

        OpenAIAPI api = new(key);

        foreach (string chunk in chunks)
        {
            Conversation chat = api.Chat.CreateConversation();

            chat.AppendSystemMessage(Prompt);
            chat.AppendUserInput(chunk);

            string response = await chat.GetResponseFromChatbot();
            await console.Output.WriteLineAsync(response);
        }
    }
}