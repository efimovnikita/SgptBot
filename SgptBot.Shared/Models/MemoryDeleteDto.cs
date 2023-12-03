namespace SgptBot.Shared.Models;

public class MemoryDeleteDto
{
    public string UserId { get; set; } = "";
    public string[] IdListToDelete { get; set; } = [];
}