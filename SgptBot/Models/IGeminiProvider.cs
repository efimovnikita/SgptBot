public interface IGeminiProvider
{
    Task<string> GetAnswerFroGemini(string token, string payload);
}