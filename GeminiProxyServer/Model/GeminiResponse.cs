using System.Text.Json.Serialization;

internal class GeminiResponse
{
    [JsonPropertyName("candidates")]
    public Candidate[] Candidates { get; set; }

    [JsonPropertyName("promptFeedback")]
    public PromptFeedback PromptFeedback { get; set; }
}
