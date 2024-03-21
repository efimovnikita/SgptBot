namespace QStarConsole;

internal class Program
{
    static void Main(string[] args)
    {
        const string variableName = "OPENAI_API_KEY";
        string? keyEnvVar = Environment.GetEnvironmentVariable(variableName);
        if (String.IsNullOrEmpty(keyEnvVar))
        {
            Console.WriteLine($"I need the env variable '{variableName}'.");
            return;
        }
        
        Console.Write("Give me your question: ");
        string? prompt = Console.ReadLine();
        if (String.IsNullOrEmpty(prompt))
        {
            Console.WriteLine("Prompt was empty.");
            return;
        }
        
        
    }
}