namespace SgptBot.Shared.Models;

#pragma warning disable CS0618
public class MemoryInputDto
{
    public string Key { get; set; } = "";
    public string Memory { get; set; } = "";
    public string MemoryId { get; set; } = "";
    public string UserId { get; set; } = "";
    public int MaxTokensPerLine { get; set; } = 128;
    public int MaxTokensPerParagraph { get; set; } = 200;
    public int OverlapTokens { get; set; } = 0;
}