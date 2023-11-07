namespace SgptBot.Models;

public class ApplicationSettings 
{
    public long AdminId { get; }
    public string DbConnectionString { get; }
    public string DbPassword { get; }
    public string TtsApiUrl { get; set; }

    public ApplicationSettings(long adminId, string dbConnectionString, string dbPassword, string ttsApiUrl)
    {
        AdminId = adminId;
        DbConnectionString = dbConnectionString;
        DbPassword = dbPassword;
        TtsApiUrl = ttsApiUrl;
    }
}