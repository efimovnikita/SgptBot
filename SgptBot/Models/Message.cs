namespace SgptBot.Models;

public class Message
{
    public Message(Role role, string msg)
    {
        Role = role;
        Msg = msg;
    }

    public Role Role { get; }
    public string Msg { get; }
}