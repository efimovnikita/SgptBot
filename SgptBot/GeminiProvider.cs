internal class GeminiProvider(HttpClient httpClient, string remoteApiUri) : IGeminiProvider
{
    public Task<string> GetAnswerFroGemini(string token, string payload)
    {
        throw new NotImplementedException();
    }
}