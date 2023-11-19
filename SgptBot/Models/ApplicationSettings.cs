namespace SgptBot.Models;

public class ApplicationSettings 
{
    public long AdminId { get; }
    public string TtsApiUrl { get; set; }

    public ApplicationSettings(long adminId, string ttsApiUrl)
    {
        AdminId = adminId;
        TtsApiUrl = ttsApiUrl;
    }
}