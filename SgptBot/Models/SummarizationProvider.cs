using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Connectors.AI.OpenAI;
using Microsoft.SemanticKernel.Text;

namespace SgptBot.Models;

public class SummarizationProvider : ISummarizationProvider
{
    private readonly int _maxTokensPerLine;
    private readonly int _maxTokensPerParagraph;
    private readonly int _overlapTokens;
    private readonly ILogger<SummarizationProvider> _logger;

    public SummarizationProvider(int maxTokensPerLine, int maxTokensPerParagraph, int overlapTokens,
        ILogger<SummarizationProvider> logger)
    {
        _maxTokensPerLine = maxTokensPerLine;
        _maxTokensPerParagraph = maxTokensPerParagraph;
        _overlapTokens = overlapTokens;
        _logger = logger;
    }
    
#pragma warning disable SKEXP0055
    public async Task<string> GetSummary(string key, Model storeUserModel, string context)
    {
        _logger.LogInformation("Starting summary generation");

        try
        {
            KernelBuilder builder = new();
            builder.AddOpenAIChatCompletion(storeUserModel == Model.Gpt3 ? "gpt-3.5-turbo-1106" : "gpt4-1106-preview", key);

            Kernel kernel = builder.Build();
    
            List<string> lines = TextChunker.SplitPlainTextLines(text: context, maxTokensPerLine: _maxTokensPerLine);
            _logger.LogInformation("Split text into {LineCount} lines", lines.Count);

            string[] paragraphs = TextChunker.SplitPlainTextParagraphs(
                lines: lines,
                overlapTokens: _overlapTokens,
                maxTokensPerParagraph: _maxTokensPerParagraph).ToArray();
            _logger.LogInformation("Split text into {ParagraphCount} paragraphs", paragraphs.Length);
    
            const string promptTemplate = """
                                          Create a summary capturing the main points and key details of:

                                          {{$input}}
                                          """;

            KernelFunction summarize = kernel.CreateFunctionFromPrompt(promptTemplate: promptTemplate,
                executionSettings: new OpenAIPromptExecutionSettings {MaxTokens = 150});

            StringBuilder stringBuilder = new();
            foreach (string paragraph in paragraphs)
            {
                FunctionResult functionResult = await kernel.InvokeAsync(summarize, new KernelArguments(paragraph));
                stringBuilder.AppendLine(functionResult.ToString());
            }

            _logger.LogInformation("Successfully generated summary");
        
            return stringBuilder.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error occurred while generating summary");
            return "";
        }
    }
#pragma warning restore SKEXP0055
}