using System.Text.Json.Serialization;

namespace SgptBot.Models;

public class GeminiConversation
{
    [JsonPropertyName("contents")]
    public GeminiContent[] Contents { get; set; }
}