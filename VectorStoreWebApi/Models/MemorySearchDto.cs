namespace VectorStoreWebApi.Models;

internal class MemorySearchDto
{
    public string Key { get; set; } = "";
    public string Prompt { get; set; } = "";
    public string[] MemoryIds { get; set; } = [];
    public string UserId { get; set; } = "";
}