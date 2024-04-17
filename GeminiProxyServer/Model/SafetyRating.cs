using System.Text.Json.Serialization;

internal class SafetyRating
{
    [JsonPropertyName("category")]
    public string Category { get; set; }

    [JsonPropertyName("probability")]
    public string Probability { get; set; }
}
