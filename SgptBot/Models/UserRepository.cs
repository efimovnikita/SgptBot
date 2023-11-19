using MongoDB.Driver;

namespace SgptBot.Models;

public class UserRepository : IUserRepository
{
    private readonly IMongoDatabase _database;

    public UserRepository(string dbAdmin, string dbPassword, string dbHost, string dbPort, string dbDatabase)
    {
        string connectionString = $"mongodb://{dbAdmin}:{dbPassword}@{dbHost}:{dbPort}";

        MongoClient client = new(connectionString);
        _database = client.GetDatabase(dbDatabase);
    }

    public StoreUser GetUserOrCreate(long id, string firstName, string lastName, string userName, bool isAdministrator)
    {
        IMongoCollection<StoreUser>? usersCollection = _database.GetCollection<StoreUser>("Users");

        FilterDefinition<StoreUser>? filter = Builders<StoreUser>.Filter.Eq("_id", id);

        StoreUser? user = usersCollection.Find(filter).FirstOrDefault();
        if (user != null)
        {
            return user;
        }

        user = new StoreUser(id, firstName, lastName, userName, isAdministrator);
        usersCollection.InsertOne(user);

        return user;
    }

    public bool UpdateUser(StoreUser updateUser)
    {
        IMongoCollection<StoreUser> usersCollection = _database.GetCollection<StoreUser>("Users");
    
        updateUser.ActivityTime = DateTime.UtcNow;

        FilterDefinition<StoreUser>? filter = Builders<StoreUser>.Filter.Eq("_id", updateUser.Id);
    
        ReplaceOptions replaceOptions = new() { IsUpsert = false };
        ReplaceOneResult result = usersCollection.ReplaceOne(filter, updateUser, replaceOptions);

        return result.ModifiedCount > 0;
    }

    public StoreUser[] GetAllUsers()
    {
        IMongoCollection<StoreUser>? usersCollection = _database.GetCollection<StoreUser>("Users");
        IFindFluent<StoreUser, StoreUser>? usersCursor = usersCollection.Find(Builders<StoreUser>.Filter.Empty);
        StoreUser[] usersArray = usersCursor.ToList().ToArray();

        return usersArray;
    }

    public StoreUser? GetUserById(long id)
    {
        IMongoCollection<StoreUser>? usersCollection = _database.GetCollection<StoreUser>("Users");
        FilterDefinition<StoreUser>? filter = Builders<StoreUser>.Filter.Eq("_id", id);
        StoreUser? user = usersCollection.Find(filter).FirstOrDefault();

        return user;
    }
}