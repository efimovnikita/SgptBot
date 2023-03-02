using System.CommandLine;

namespace SubtitlesSplitter;

internal static class Program
{
    public static void Main(string[] args)
    {
        Option<string> inputOption = new("--input", "Full path to subtitles file")
        {
            IsRequired = true
        };
        inputOption.AddAlias("-i");
        
        Option<string> outputOption = new("--output", "Full path to output file")
        {
            IsRequired = true
        };
        outputOption.AddAlias("-o");

        Option<int> sizeOption = new("--size", () => 570, "Chunk size (characters count)");
        Option<string> promtOption = new("--prompt",
            () => "Turn this into the normal text and translate it to simple English", "Default prompt before chunk");

        RootCommand rootCommand = new("Subtitles splitter");
        rootCommand.AddOption(inputOption);
        rootCommand.AddOption(outputOption);
        rootCommand.AddOption(sizeOption);
        rootCommand.AddOption(promtOption);

        rootCommand.SetHandler(RunCommand, inputOption, outputOption, sizeOption, promtOption);
            
        // Parse the command line arguments
        rootCommand.Invoke(args);
    }

    private static void RunCommand(string input, string output, int chunkSize, string promt)
    {
        List<string> chunks = SplitTextIntoChunks(input, chunkSize, promt);
        
        // Join the chunks together with a newline character
        string result = String.Join("\n\n", chunks);

        // Write the result to the specified output file
        File.WriteAllText(output, result);
    }
    
    private static List<string> SplitTextIntoChunks(string filePath, int chunkSize, string prompt)
    {
        // Read the entire text file into a string
        string text = File.ReadAllText(filePath);

        // Remove all new line characters and extra spaces from the text
        text = text.Replace("\n", " ").Replace("\r", "").Replace("  ", " ");
        text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // Split the text into chunks
        List<string> chunks = new();
        int startIndex = 0;
        while (startIndex < text.Length)
        {
            int remainingLength = text.Length - startIndex;
            int length = Math.Min(chunkSize, remainingLength);
            string chunk = text.Substring(startIndex, length);

            // Check if the last character of the chunk is a whitespace
            if (chunk[length - 1] != ' ' && startIndex + length < text.Length)
            {
                // Find the index of the last whitespace in the chunk
                int lastWhitespaceIndex = chunk.LastIndexOf(' ');
                if (lastWhitespaceIndex >= 0)
                {
                    // Adjust the length to the index of the last whitespace in the chunk
                    length = lastWhitespaceIndex + 1;
                    chunk = text.Substring(startIndex, length);
                }
            }

            chunk = chunk.TrimEnd();

            // Check if the last character of the chunk is not a period
            if (chunk[^1] != '.')
            {
                // Append a period to the chunk
                chunk += '.';
            }

            // Concatenate the prompt text with the chunk
            string concatenatedChunk = $"{prompt}:\n{chunk}";

            chunks.Add(concatenatedChunk);
            startIndex += length;
        }

        return chunks;
    }
}