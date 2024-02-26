using LiteDB;
using SgptBot.Shared.Models;

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
    public List<Message> Conversation { get; set; } = [];
    public Model Model { get; set; } = Model.Custom;
    public bool IsBlocked { get; set; }
    public bool VoiceMode { get; set; }
    public ImgQuality ImgQuality { get; set; } = ImgQuality.Standard;
    public ImgStyle ImgStyle { get; set; } = ImgStyle.Natural;
    public DateTime ActivityTime { get; set; } = new(2020, 1, 1, 1, 00, 00);
    public bool AnewMode { get; set; }
    public string ClaudeApiKey { get; set; }
    public List<Message> History { get; set; } = [];
    public bool ContextFilterMode { get; set; }
    public List<VectorMemoryItem> MemoryStorage { get; set; } = [];
    public List<VectorMemoryItem> WorkingMemory { get; set; } = [];

    public override string ToString()
    {
        return $"{nameof(Id)}: {Id}, {nameof(FirstName)}: {FirstName}, {nameof(LastName)}: {LastName}, {nameof(UserName)}: {UserName}";
    }
}