using CliFx;

namespace SubtitlesExtractorAndRewriter;

internal static class Program
{
    public static async Task<int> Main()
    {
        return await new CliApplicationBuilder()
            .SetDescription("Get subtitles from YouTube videos.")
            .SetTitle("Subtitles")
            .SetVersion("v1.3.0")
            .AddCommandsFromThisAssembly()
            .Build()
            .RunAsync();
    }
}