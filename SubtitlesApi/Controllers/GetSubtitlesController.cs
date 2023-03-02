using CliWrap;
using CliWrap.Buffered;
using Microsoft.AspNetCore.Mvc;

namespace SubtitlesApi.Controllers;

[ApiController]
[Route("[controller]")]
public class GetSubtitlesController : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(string link, string language)
    {
        try
        {
            // execute script here
            BufferedCommandResult result = await Cli.Wrap("./script.sh")
                .WithArguments($"-v {link} -l {language}")
                .ExecuteBufferedAsync();

            // if successful, return txt file content
            return Ok(await System.IO.File.ReadAllTextAsync(result.StandardOutput.Trim()));
        }
        catch (Exception ex)
        {
            // if there's an error, return error message
            string errorMessage = "Error message: " + ex.Message;
            if (String.IsNullOrEmpty(ex.InnerException?.Message) == false)
            {
                errorMessage += ex.InnerException.Message;
            }
            return BadRequest(errorMessage);
        }
    }

}