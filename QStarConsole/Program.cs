using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OpenAiNg;
using OpenAiNg.Chat;
using OpenAiNg.ChatFunctions;
using OpenAiNg.Models;

namespace QStarConsole;

internal class Program
{
    static async Task Main(string[] args)
    {
        const string variableName = "OPENAI_API_KEY";
        string? key = Environment.GetEnvironmentVariable(variableName);
        if (String.IsNullOrEmpty(key))
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
        
        OpenAiApi api = new(key);
        
        ChatMessage[] chatMessages =
        [
            new ChatMessage(ChatMessageRole.User, prompt)
        ];
        
        ChatRequest request = new()
        {
            Model = Model.GPT4_1106_Preview,
            Messages = chatMessages,
            Temperature = 0.9,
            NumChoicesPerMessage = 5
        };

        var result = await api.Chat.CreateChatCompletionAsync(request);
        string[] choices = (result.Choices ?? throw new InvalidOperationException()).Select(choice =>
                choice.Message != null
                    ? choice.Message.Content ?? ""
                    : "")
            .ToArray();


        string functionPrompt = $"""
                    Get only the number of the most accurate and precise answer to the user's question (in your opinion) in json format.
                    
                    USER QUESTION:
                    
                    [{prompt}]
                    
                    ANSWER N 1:
                    
                    [{choices[1]}]
                    
                    ANSWER N 2:
                    
                    [{choices[0]}]
                    
                    ANSWER N 3:
                    
                    [{choices[2]}]
                    
                    ANSWER N 4:
                    
                    [{choices[3]}]
                    
                    ANSWER N 5:
                    
                    [{choices[4]}]
                    """;
        
        JObject jObjectDescription = new()
        {
            {
                "type", "object"
            },
            {
                "properties", new JObject
                {
                    {
                        "number", new JObject
                        {
                            {"type", "integer"},
                            {
                                "description",
                                "The number of the most accurate and precise answer to the user's question."
                            }
                        }
                    },
                    {
                        "additional", new JObject
                        {
                            {"type", "string"},
                            {
                                "description",
                                "Any additional data, that considered to be valuable."
                            }
                        }
                    }
                }
            },
            {
                "required", new JArray("number")
            }
        };
        
        ChatRequest functionRequest = new()
        {
            Model = Model.GPT4_1106_Preview,
            Messages = new List<ChatMessage>(1)
            {
                new(ChatMessageRole.User, functionPrompt)
            },
            ResponseFormat = new ChatRequestResponseFormats {Type = ChatRequestResponseFormatTypes.Json},
            Tools = new List<Tool>
            {
                new(new ToolFunction(name: "GetAccurateAnswer",
                    description: "The function for extraction the number of the most accurate and precise answer to the user's question.",
                    parameters: jObjectDescription))
            },
            Temperature = 0,
            NumChoicesPerMessage = 1
        };
        
        var functionCallResponse = await api.Chat.CreateChatCompletionAsync(functionRequest);
        string functionCallArguments = functionCallResponse.Choices?[0].Message?.ToolCalls?[0].FunctionCall.Arguments ??
                                       string.Empty;
        if (String.IsNullOrWhiteSpace(functionCallArguments))
        {
            Console.WriteLine("Function arguments is empty");
            return;
        }

        GetAccurateAnswerFunctionResult? functionResult = null;
        try
        {
            functionResult = JsonConvert.DeserializeObject<GetAccurateAnswerFunctionResult>(functionCallArguments);
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
        }
        
        if (functionResult == null)
        {
            Console.WriteLine("Problems with deserialization");
            return;
        }

        int number = functionResult.Number;

        string mva = choices[number - 1];

        Console.WriteLine($"{mva}");
    }
}

public class GetAccurateAnswerFunctionResult
{
    public int Number { get; set; }
    [JsonProperty(PropertyName = "additional")] public string AdditionalInfo { get; set; } = "";
}