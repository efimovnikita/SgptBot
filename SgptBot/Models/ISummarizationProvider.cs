namespace SgptBot.Models;

public interface ISummarizationProvider
{
    Task<string> GetSummary(string key, Model storeUserModel, string context);
}