using System.Text.Json.Serialization;

internal class GeminiErrorResponse
{
    [JsonPropertyName("error")]
    public Error Error { get; set; }
}
