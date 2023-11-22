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
            if (IsUrlFromYouTube(inputText) == false)
            {
                return inputText;
            }
            
            // Input might contains some garbage
            string[] strings = inputText.Split(' ');
            string url = strings[0];

            string apiUrl = $"{_remoteApiUri}?url={Uri.EscapeDataString(url)}&token={token}";
            string apiResponse = await _httpClient.GetStringAsync(apiUrl);

            return !String.IsNullOrEmpty(apiResponse) ? apiResponse : inputText;
        }
        catch
        {
            return inputText;
        }
    }

    private static bool IsUrlFromYouTube(string url)
    {
        Uri uri = new(url);
        return uri.Host.Contains("youtu.be") || uri.Host.Contains("youtube.com");
    }
}