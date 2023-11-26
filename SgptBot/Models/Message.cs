namespace SgptBot.Models;

public class Message
{
    public Role Role { get; set; }
    public string Msg { get; set; }
    public DateOnly Date { get; set; }
    public Message(Role role, string msg, DateOnly date)
    {
        Role = role;
        Msg = msg;
        Date = date;
    }
}
