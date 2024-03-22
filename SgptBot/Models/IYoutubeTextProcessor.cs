namespace SgptBot.Models;

public interface IYoutubeTextProcessor
{
    Task<string> ProcessTextAsync(string inputText, string token);
    Task<string> GetTextFromAudioFileAsync(string path, string token);
}