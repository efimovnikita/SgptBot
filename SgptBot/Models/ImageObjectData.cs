using System.Text.Json.Serialization;

namespace SgptBot.Models;

public record ImageObjectData(
    [property: JsonPropertyName("revised_prompt")] string RevisedPrompt,
    [property: JsonPropertyName("url")] string Url
);