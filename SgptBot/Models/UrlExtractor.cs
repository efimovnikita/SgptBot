using System.Text.RegularExpressions;

namespace SgptBot.Models;

public class UrlExtractor
{
    private readonly Regex _urlRegex;

    public UrlExtractor()
    {
        const string pattern = @"^(https?|ftp|file):\/\/[-A-Za-z0-9+&@#\/%?=~_|!:,.;]*[-A-Za-z0-9+&@#\/%=~_|]";
        _urlRegex = new Regex(pattern, RegexOptions.IgnoreCase);
    }

    public string? ExtractUrl(string input)
    {
        if (String.IsNullOrEmpty(input))
        {
            return null;
        }

        Match match = _urlRegex.Match(input);

        return match.Success ? match.Value : null;
    }
}