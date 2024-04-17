using System.Text.Json.Serialization;

public class GeminiContent
{
    [JsonPropertyName("parts")]
    public GeminiPart[] Parts { get; set; }

    [JsonPropertyName("role")]
    public string Role { get; set; }
}
