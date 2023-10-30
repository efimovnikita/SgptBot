using LiteDB;

namespace SgptBot.Models;

public class StoreUser
{
    public StoreUser() { }
    public StoreUser(long id, string firstName, string lastName, string userName, bool isAdministrator)
    {
        Id = id;
        FirstName = firstName;
        LastName = lastName;
        UserName = userName;
        IsAdministrator = isAdministrator;
    }

    [BsonId]
    public long Id { get; set; }

    public string FirstName { get; set; }
    public string LastName { get; set; }
    public string UserName { get; set; }
    public bool IsAdministrator { get; set; }
    public string ApiKey { get; set; }
    public List<Message> Conversation { get; set; } = new();
    public Model Model { get; set; } = Model.Gpt3;
    public bool IsBlocked { get; set; } = true;
}
public enum Model
{
    Gpt3, Gpt4
}

public enum Role
{
    System, User, Ai
}

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