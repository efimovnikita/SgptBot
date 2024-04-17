using System.Text.Json;
using System.Text;

internal class GeminiProvider(HttpClient httpClient, string remoteApiUri) : IGeminiProvider
{
    public async Task<string> GetAnswerFroGemini(string token, string payload)
    {
        var requestPayload = new GeminiApiRequestPayload
        {
            Key = token,
            Payload = payload
        };

        var jsonPayload = JsonSerializer.Serialize(requestPayload);

        var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

        var response = await httpClient.PostAsync(remoteApiUri + "/api/GetAnswerFromGemini", content);

        if (response.IsSuccessStatusCode)
        {
            var jsonResponse = await response.Content.ReadAsStringAsync();
            var geminiConversation = JsonSerializer.Deserialize<GeminiConversation>(jsonResponse);

            if (geminiConversation != null && geminiConversation.Contents != null && geminiConversation.Contents.Length > 0)
            {
                var assistantContent = geminiConversation.Contents.FirstOrDefault(c => c.Role == "model");

                if (assistantContent != null && assistantContent.Parts != null && assistantContent.Parts.Length > 0)
                {
                    return assistantContent.Parts[0].Text;
                }
            }
        }

        return string.Empty;
    }
}