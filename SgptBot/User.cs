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
    
    public void InsertSystemMessage(string message)
    {
        UserMessage[] systemMessages = Messages.Where(userMessage => userMessage.role.Equals(Role.system.ToString())).ToArray();
        foreach (UserMessage systemMessage in systemMessages)
        {
            Messages.Remove(systemMessage);
        }

        Messages.Insert(0, new UserMessage { role = Role.system.ToString(), content = message });
    }
}