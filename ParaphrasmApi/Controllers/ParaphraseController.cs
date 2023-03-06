using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using SgptBot;

namespace ParaphrasmApi.Controllers;

[ApiController]
[Route("[controller]")]
public class ParaphraseController : ControllerBase
{
    [HttpGet(Name = "Paraphrase")]
    public async Task<IActionResult> Get(string input)
    {
        string? key = Environment.GetEnvironmentVariable("KEY");
        if (String.IsNullOrWhiteSpace(key))
        {
            return Ok("Key should be set");
        }
        
        User user = new(123);
        user.InsertSystemMessage("You are a professional English teacher. Your specialization is to paraphrase and rewrite English sentences and you usually use simple English words for beginner (A2) or intermediate (B1) English learners.");
        user.AddMessage(Role.user.ToString(), input);
        
        string promptJson = JsonSerializer.Serialize(user.Messages);
            
        // Save the JSON string to a file
        const string path = "user_message.json";
        System.IO.File.WriteAllText(path, promptJson);
            
        string arguments = $"pygpt.py -k \"{key}\" -p \"{path}\"";
        ProcessStartInfo start = new()
        {
            FileName = "python3",
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true
        };

        string trimmedResult;
        try
        {
            using Process process = Process.Start(start)!;
            using StreamReader reader = process.StandardOutput;
            string result = await reader.ReadToEndAsync();
            trimmedResult = result.Trim();
            
        }
        catch (Exception)
        {
            return Ok("Error while getting result from GPT-3");
        }
        
        return Ok(trimmedResult);
    }
}