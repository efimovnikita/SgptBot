namespace SgptBot;

public class User
{
    public long Id { get; set; }
    public List<UserMessage> Messages { get; set; }

    public User(long id)
    {
        Id = id;
        Messages = new List<UserMessage>();
    }

    public void AddMessage(string role, string message)
    {
        Messages.Add(new UserMessage { role = role, content = message });
    }
}