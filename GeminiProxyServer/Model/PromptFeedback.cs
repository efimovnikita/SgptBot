using System.Text.Json.Serialization;

internal class PromptFeedback
{
    [JsonPropertyName("safetyRatings")]
    public SafetyRating[] SafetyRatings { get; set; }
}
