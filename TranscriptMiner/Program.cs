using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using Cocona;
using Cocona.Builder;
using Humanizer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ValidationResult = System.ComponentModel.DataAnnotations.ValidationResult;

namespace TranscriptMiner;

internal class Program
{
    // ReSharper disable once UnusedParameter.Local
    // ReSharper disable once CognitiveComplexity
    static void Main(string[] args)
    {
        CoconaAppBuilder builder = CoconaApp.CreateBuilder();

        builder.Logging.AddSimpleConsole();
        builder.Configuration.AddEnvironmentVariables();

        CoconaApp app = builder.Build();

        app.AddCommand(async ([FilePathExists] [Option('l')] string listOfLinks,
            [DirectoryPathExists] [Option('s')] string saveDirPath, IConfiguration configuration,
            ILogger<Program> logger, [Option('n')] int numberOfWords = 5) =>
        {
            string key = configuration["OPENAI_API_KEY"];
            if (String.IsNullOrWhiteSpace(key))
            {
                logger.LogError("'OPENAI_API_KEY' env variable wasn't found.");
                return 1;
            }

            string remoteApiUri = configuration["TRANSCRIPTION_ENDPOINT"];
            if (String.IsNullOrWhiteSpace(remoteApiUri))
            {
                logger.LogError("'TRANSCRIPTION_ENDPOINT' env variable wasn't found.");
                return 1;
            }
            
            string[] links = await File.ReadAllLinesAsync(listOfLinks);
            if (links.Length == 0)
            {
                logger.LogError("File with links is empty.");
                return 1;
            }
            
            foreach (string link in links)
            {
                logger.LogInformation("The current link is '{link}'", link);

                try
                {
                    string pageTitle = await GetWebPageTitleAsync(link, logger);
                    if (String.IsNullOrWhiteSpace(pageTitle))
                    {
                        continue;
                    }
                                
                    string fileWithTranscript = GetValidFileName(pageTitle, numberOfWords);
                    
                    logger.LogInformation("The file name is '{name}'", fileWithTranscript);
                                
                    HttpClientHandler httpClientHandler = new()
                    {
                        ServerCertificateCustomValidationCallback =
                            HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
                    };
                    HttpClient httpClient = new(httpClientHandler);
                    httpClient.Timeout = TimeSpan.FromMinutes(5);

                    string apiUrl = $"{remoteApiUri}?url={Uri.EscapeDataString(link)}&token={key}";
                    string apiResponse = await httpClient.GetStringAsync(apiUrl);
                
                    if (String.IsNullOrWhiteSpace(apiResponse) == false)
                    {
                        string path = Path.Combine(saveDirPath, fileWithTranscript);
                        await File.WriteAllTextAsync(path,
                            apiResponse);
                        
                        logger.LogInformation("The file '{name}' was created", path);
                    }
                }
                catch (Exception e)
                {
                    logger.LogError("Error while handling the response from an endpoint:\n{Error}", e);
                }
            }

            return 0;
        });

        app.Run();
    }

    private static string GetValidFileName(string pageTitle, int length)
    {
        // Remove invalid characters from the page title
        foreach (char c in Path.GetInvalidFileNameChars())
        {
            pageTitle = pageTitle.Replace(c, '_');
        }

        // Trim the page title to a maximum length (optional)
        const int maxLength = 35;
        if (pageTitle.Length > maxLength)
        {
            pageTitle = pageTitle[..maxLength];
        }

        pageTitle = pageTitle.Truncate(length, Truncator.FixedNumberOfWords);

        return $"{pageTitle}.txt";
    }

    static async Task<string> GetWebPageTitleAsync(string url, ILogger logger)
    {
        try
        {
            using HttpClient client = new();
            HttpResponseMessage response = await client.GetAsync(url);
            response.EnsureSuccessStatusCode();

            string htmlContent = await response.Content.ReadAsStringAsync();

            // Use regular expression to extract the title from the HTML
            string pattern = "<title.*?>(.*?)</title>";
            Match match = Regex.Match(htmlContent, pattern, RegexOptions.IgnoreCase);

            if (match.Success)
            {
                return match.Groups[1].Value;
            }
        }
        catch (HttpRequestException ex)
        {
            logger.LogError("Error while extracting the page title:\n{Error}", ex);
        }

        return "";
    }
}

internal class DirectoryPathExistsAttribute : ValidationAttribute
{
    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
    {
        if (value is string path && (Directory.Exists(path) || Directory.Exists(path)))
        {
            return ValidationResult.Success;
        }
        return new ValidationResult($"The path '{value}' is not found.");
    }
}

internal class FilePathExistsAttribute : ValidationAttribute
{
    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
    {
        if (value is string path && (File.Exists(path) || File.Exists(path)))
        {
            return ValidationResult.Success;
        }
        return new ValidationResult($"The path '{value}' is not found.");
    }
}