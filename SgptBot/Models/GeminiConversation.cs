using System.Text.Json.Serialization;

public class GeminiConversation
{
    [JsonPropertyName("contents")]
    public GeminiContent[] Contents { get; set; }
}
