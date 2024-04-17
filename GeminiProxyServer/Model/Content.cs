using System.Text.Json.Serialization;

internal class Content
{
    [JsonPropertyName("parts")]
    public Part[] Parts { get; set; }

    [JsonPropertyName("role")]
    public string Role { get; set; }
}