using CliWrap;
using CliWrap.Buffered;
using Microsoft.AspNetCore.Mvc;

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
        
        string? path = Environment.GetEnvironmentVariable("SHARPPATH");
        if (String.IsNullOrWhiteSpace(path))
        {
            return Ok("Path to SharpGTP should be set");
        }

        BufferedCommandResult result = await Cli.Wrap(path!)
            .WithArguments($"--key \"{key}\" --promt \"Rewrite this in more simple words:\n{input}\"")
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync();
        if (result.ExitCode != 0)
        {
            return Ok("Error while getting result from GPT-3");
        }

        return Ok(result.StandardOutput);
    }
}