namespace VectorStoreWebApi.Models;

internal class MemoryDeleteDto
{
    public string UserId { get; set; } = "";
    public string[] IdListToDelete { get; set; } = [];
}