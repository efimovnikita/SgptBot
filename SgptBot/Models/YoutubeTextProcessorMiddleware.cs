namespace SgptBot.Models;

public class YoutubeTextProcessorMiddleware : IYoutubeTextProcessor
{
    private readonly HttpClient _httpClient;
    private readonly string _remoteApiUri;

    public YoutubeTextProcessorMiddleware(HttpClient httpClient, string remoteApiUri)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _remoteApiUri = remoteApiUri ?? throw new ArgumentNullException(nameof(remoteApiUri));
    }

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

            string apiUrl = $"{_remoteApiUri}?url={Uri.EscapeDataString(url)}&token={token}";
            string apiResponse = await _httpClient.GetStringAsync(apiUrl);

            return !String.IsNullOrEmpty(apiResponse) ? apiResponse : inputText;
        }
        catch
        {
            return inputText;
        }
    }
}