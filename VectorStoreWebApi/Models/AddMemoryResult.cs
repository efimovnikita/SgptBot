namespace VectorStoreWebApi.Models;

#pragma warning disable CS0618
public class AddMemoryResult
{
    public string MemoryId { get; set; } = "";
    public List<string> ParagraphIds { get; set; } = [];
    public DateTime CreationDate { get; set; } = DateTime.Now;
}