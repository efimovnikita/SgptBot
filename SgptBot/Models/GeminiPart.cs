using System.Text.Json.Serialization;

internal class GeminiPart
{
    [JsonPropertyName("text")]
    public string Text { get; set; }
}
