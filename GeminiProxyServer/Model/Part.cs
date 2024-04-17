using System.Text.Json.Serialization;

internal class Part
{
    [JsonPropertyName("text")]
    public string Text { get; set; }
}
