using System.Diagnostics.CodeAnalysis;

namespace SgptBot;

[SuppressMessage("ReSharper", "InconsistentNaming")]
public enum UserRole
{
    user,
    assistant,
    system
}