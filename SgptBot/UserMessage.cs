using System.Diagnostics.CodeAnalysis;

namespace SgptBot;

[SuppressMessage("ReSharper", "InconsistentNaming")]
public class UserMessage
{
    public string role { get; set; } = "";
    public string content { get; set; } = "";
}