using System.Text.Json.Serialization;

internal class GeminiConversation
{
    [JsonPropertyName("contents")]
    public GeminiContent[] Contents { get; set; }
}
