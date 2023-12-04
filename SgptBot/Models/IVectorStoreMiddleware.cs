using SgptBot.Shared.Models;

namespace SgptBot.Models;

public interface IVectorStoreMiddleware
{
    Task<string[]> RecallMemoryFromVectorContext(StoreUser user, string prompt);
    Task<VectorMemoryItem?> Memorize(StoreUser user, string? memories, string? fileName);
}