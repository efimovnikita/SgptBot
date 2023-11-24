using System.Diagnostics.CodeAnalysis;

namespace SgptBot.Models;

[SuppressMessage("ReSharper", "UnusedMember.Global")]
public class ClaudeApiCompletionResponse
{
    public string Completion { get; set; }
    public string StopReason { get; set; }
    public string Model { get; set; }
}