namespace SgptBot.Models;

public interface IUserRepository
{
    StoreUser GetUserOrCreate(long id, string firstName, string lastName, string userName, bool isAdministrator);
    bool UpdateUser(StoreUser updateUser);
    StoreUser[] GetAllUsers();
    StoreUser? GetUserById(long id);
}