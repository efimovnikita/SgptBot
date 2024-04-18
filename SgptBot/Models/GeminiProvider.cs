using System.Text;
using System.Text.Json;

namespace SgptBot.Models;

internal class GeminiProvider(HttpClient httpClient, string remoteApiUri) : IGeminiProvider
{
    public async Task<(string answer, GeminiResponseStatus status)> GetAnswerFroGemini(string token, GeminiConversation conversation)
    {
        try
        {
            var payload = new GeminiApiRequestPayload
            {
                Key = token,
                Payload = JsonSerializer.Serialize(conversation)
            };

            var jsonPayload = JsonSerializer.Serialize(payload);

            var stringContent = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

            var response = await httpClient.PostAsync($"{remoteApiUri}/api/GetAnswerFromGemini", stringContent);

            var answer = await response.Content.ReadAsStringAsync();

            return response.IsSuccessStatusCode
                ? (answer, GeminiResponseStatus.Success)
                : (answer, GeminiResponseStatus.Failure);
        }
        catch (Exception)
        {
            return (string.Empty, GeminiResponseStatus.Failure);
        }
    }
}