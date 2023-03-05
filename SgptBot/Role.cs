using System.Diagnostics.CodeAnalysis;

namespace SgptBot;

[SuppressMessage("ReSharper", "InconsistentNaming")]
public enum Role
{
    user,
    assistant,
    system
}