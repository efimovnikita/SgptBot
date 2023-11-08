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
    public bool IsBlocked { get; set; } = false;
    public bool VoiceMode { get; set; } = false;
    public ImgQuality ImgQuality { get; set; } = ImgQuality.Standard;
    public ImgStyle ImgStyle { get; set; } = ImgStyle.Natural;
}