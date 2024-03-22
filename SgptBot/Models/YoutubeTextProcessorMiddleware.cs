namespace SgptBot.Models;

public class YoutubeTextProcessorMiddleware(HttpClient httpClient, string remoteApiUri) : IYoutubeTextProcessor
{
    public async Task<string> ProcessTextAsync(string inputText, string token)
    {
        try
        {
            UrlExtractor urlExtractor = new();
            string? url = urlExtractor.ExtractUrl(inputText);
            if (String.IsNullOrWhiteSpace(url))
            {
                return inputText;
            }

            string apiUrl = $"{remoteApiUri}/api/getTextFromYoutube?url={Uri.EscapeDataString(url)}&token={token}";
            string apiResponse = await httpClient.GetStringAsync(apiUrl);

            return !String.IsNullOrEmpty(apiResponse) ? apiResponse : inputText;
        }
        catch
        {
            return inputText;
        }
    }

    public async Task<string> GetTextFromAudioFileAsync(string path, string token)
    {
        try
        {
            using var form = new MultipartFormDataContent();

            byte[] fileData = await File.ReadAllBytesAsync(path);

            var fileContent = new ByteArrayContent(fileData);

            form.Add(fileContent, "audioFile", Path.GetFileName(path));
            form.Add(new StringContent(token), "token");

            HttpResponseMessage response = await httpClient.PostAsync($"{remoteApiUri}/api/getTextFromAudio", form);

            response.EnsureSuccessStatusCode();
        
            string responseBody = await response.Content.ReadAsStringAsync();
            return responseBody;
        }
        catch (Exception)
        {
            return "";
        }
    }
}