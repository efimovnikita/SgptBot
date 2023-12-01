namespace VectorStoreWebApi.Models;

#pragma warning disable CS0618
internal class MemoryInputDto
{
    public string Key { get; set; } = "";
    public string Memory { get; set; } = "";
    public string MemoryId { get; set; } = "";
    public string UserId { get; set; } = "";
}