using System.Text.Json.Serialization;

namespace SgptBot.Models;

public class GeminiPart
{
    [JsonPropertyName("text")]
    public string Text { get; set; }
}