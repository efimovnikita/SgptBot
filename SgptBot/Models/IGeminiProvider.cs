public interface IGeminiProvider
{
    Task<(string answer, GeminiResponseStatus status)> GetAnswerFroGemini(string token, GeminiConversation conversation);
}