namespace SgptBot.Models;

public class ApplicationSettings 
{
    public long AdminId { get; }
    public string DbConnectionString { get; }
    public string DbPassword { get; }

    public ApplicationSettings(long adminId, string dbConnectionString, string dbPassword)
    {
        AdminId = adminId;
        DbConnectionString = dbConnectionString;
        DbPassword = dbPassword;
    }
}