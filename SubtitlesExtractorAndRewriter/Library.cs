namespace SubtitlesExtractorAndRewriter;

public static class Library
{

    // ReSharper disable once CognitiveComplexity
    public static List<string> SplitTextIntoChunks(string text, int size)
    {
        List<string> chunks = new();
        int start = 0;
        int end = 0;

        while (end < text.Length)
        {
            end = start + size;

            if (end >= text.Length)
            {
                chunks.Add(text.Substring(start));
                break;
            }

            int splitIndex = text.LastIndexOf('.', end);

            if (splitIndex == -1 || splitIndex < start)
            {
                splitIndex = text.IndexOf(',', end);
            }

            if (splitIndex == -1 || splitIndex < start)
            {
                chunks.Add(text.Substring(start, size));
                start += size;
            }
            else
            {
                chunks.Add(text.Substring(start, splitIndex - start + 1));
                start = splitIndex + 1;
            }
        }

        return chunks;
    }
}