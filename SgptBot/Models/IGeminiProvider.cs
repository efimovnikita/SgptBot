public interface IGeminiProvider
{
    Task<string> GetAnswerFroGemini(string token, GeminiConversation conversation);
}