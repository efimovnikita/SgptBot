using System.Security.Cryptography;
using System.Text;
using LiteDB;

namespace SgptBot.Models;

public class UserRepository
{
    private readonly string _name;
    private readonly string _folder;
    private readonly string _password;

    public UserRepository(string name, string folder, string password)
    {
        _name = name;
        _folder = folder;
        _password = password;
    }

    public StoreUser GetUserOrCreate(long id, string firstName, string lastName, string userName, bool isAdministrator)
    {
        // Ensure the directory exists. If doesn't, this function will create it.
        Directory.CreateDirectory(_folder);
        
        using var db = new LiteDatabase(
            new ConnectionString($"Filename={Path.Combine(_folder, _name)};Password={GetSha256Hash(_password)}")
            {
                Connection = ConnectionType.Direct,
            });
        var users = db.GetCollection<StoreUser>("Users");

        var user = users.FindById(id);
        if (user != null) return user;
        user = new StoreUser(id, firstName, lastName, userName, isAdministrator);
        users.Insert(user);

        return user;
    }

    public bool UpdateUser(StoreUser updateUser)
    {
        // Ensure the directory exists. If doesn't, this function will create it.
        Directory.CreateDirectory(_folder);
        
        using var db = new LiteDatabase(
            new ConnectionString($"Filename={Path.Combine(_folder, _name)};Password={GetSha256Hash(_password)}")
            {
                Connection = ConnectionType.Direct,
            });
        var users = db.GetCollection<StoreUser>("Users");

        updateUser.ActivityTime = DateTime.Now;
        
        return users.Update(updateUser);
    }
    
    public StoreUser[] GetAllUsers()
    {
        // Ensure the directory exists. If doesn't, this function will create it.
        Directory.CreateDirectory(_folder);
        
        using var db = new LiteDatabase(
            new ConnectionString($"Filename={Path.Combine(_folder, _name)};Password={GetSha256Hash(_password)}")
            {
                Connection = ConnectionType.Direct,
            });
        var users = db.GetCollection<StoreUser>("Users");

        return users.FindAll().ToArray();
    }
    
    public StoreUser? GetUserById(long id)
    {
        // Ensure the directory exists. If doesn't, this function will create it.
        Directory.CreateDirectory(_folder);
        
        using var db = new LiteDatabase(
            new ConnectionString($"Filename={Path.Combine(_folder, _name)};Password={GetSha256Hash(_password)}")
            {
                Connection = ConnectionType.Direct,
            });
        var users = db.GetCollection<StoreUser>("Users");

        var user = users.FindById(id);
        return user;
    }

    private static string GetSha256Hash(string inputString)
    {
        using (SHA256 sha256Hash = SHA256.Create())
        {
            byte[] bytes = sha256Hash.ComputeHash(Encoding.UTF8.GetBytes(inputString));

            StringBuilder builder = new StringBuilder();
            for (int i = 0; i < bytes.Length; i++)
            {
                builder.Append(bytes[i].ToString("x2"));
            }

            return builder.ToString();
        }
    }
}