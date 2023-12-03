namespace SgptBot.Shared.Models;

#pragma warning disable CS0618
public class AddMemoryResult
{
    public string MemoryId { get; set; } = "";
    public List<string> ChunkIds { get; set; } = [];
    public DateTime CreationDate { get; set; } = DateTime.Now;
}