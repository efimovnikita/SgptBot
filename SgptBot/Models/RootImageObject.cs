using System.Text.Json.Serialization;

namespace SgptBot.Models;

public record RootImageObject(
    [property: JsonPropertyName("created")] int Created,
    [property: JsonPropertyName("data")] IReadOnlyList<ImageObjectData> Data
);