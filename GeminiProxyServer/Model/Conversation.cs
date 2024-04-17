using System.Text.Json.Serialization;

internal class Conversation
{
    [JsonPropertyName("contents")]
    public Content[] Contents { get; set; }
}
