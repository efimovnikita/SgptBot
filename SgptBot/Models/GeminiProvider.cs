using System.Text.Json;
using System.Text;

internal class GeminiProvider(HttpClient httpClient, string remoteApiUri) : IGeminiProvider
{
    public async Task<string> GetAnswerFroGemini(string token, GeminiConversation conversation)
    {
        try
        {
            var requestPayload = new GeminiApiRequestPayload
            {
                Key = token,
                Payload = JsonSerializer.Serialize(conversation)
            };

            var jsonPayload = JsonSerializer.Serialize(requestPayload);

            var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

            var response = await httpClient.PostAsync(remoteApiUri + "/api/GetAnswerFromGemini", content);

            if (!response.IsSuccessStatusCode)
            {
                return string.Empty;
            }

            var text = await response.Content.ReadAsStringAsync();
            return text;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }
}