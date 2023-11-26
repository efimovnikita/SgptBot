namespace SgptBot.Models;

public class Message(Role role, string msg, DateOnly date)
{
    public Role Role { get; set; } = role;
    public string Msg { get; set; } = msg;
    public DateOnly Date { get; set; } = date;
}