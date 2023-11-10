using LiteDB;

namespace SgptBot.Models;

public class StoreUser
{
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    // ReSharper disable once UnusedMember.Global
    public StoreUser() { }
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    public StoreUser(long id, string firstName, string lastName, string userName, bool isAdministrator)
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
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
    public bool IsBlocked { get; set; }
    public bool VoiceMode { get; set; }
    public ImgQuality ImgQuality { get; set; } = ImgQuality.Standard;
    public ImgStyle ImgStyle { get; set; } = ImgStyle.Natural;
}