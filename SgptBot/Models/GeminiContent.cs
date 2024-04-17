using System.Text.Json.Serialization;

internal class GeminiContent
{
    [JsonPropertyName("parts")]
    public GeminiPart[] Parts { get; set; }

    [JsonPropertyName("role")]
    public string Role { get; set; }
}
