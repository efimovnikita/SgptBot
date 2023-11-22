namespace SgptBot.Models;

public interface IYoutubeTextProcessor
{
    Task<string> ProcessTextAsync(string inputText, string token);
}