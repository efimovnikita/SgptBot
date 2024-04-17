using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddCors();

builder.WebHost.UseUrls("http://*:5000");

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapPost("/api/GetAnswerFromGemini", async ([FromBody] RequestPayload request, ILogger<Program> logger) =>
{
    logger.LogInformation("Received request for /api/GetAnswerFromGemini");

    if (string.IsNullOrEmpty(request.Key))
    {
        logger.LogWarning("The 'key' parameter is missing or empty");
        return Results.BadRequest("The 'key' parameter is required.");
    }

    if (string.IsNullOrEmpty(request.Payload))
    {
        logger.LogWarning("The 'payload' parameter is missing or empty");
        return Results.BadRequest("The 'payload' parameter is required.");
    }

    try
    {
        string url = "https://generativelanguage.googleapis.com/v1beta/models/gemini-pro:generateContent?key=" + request.Key;

        Conversation? conversation = null;
        try
        {
            conversation = JsonSerializer.Deserialize<Conversation>(request.Payload);
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Failed to deserialize 'payload'");
            return Results.BadRequest($"Invalid 'payload' format. Error: {ex.Message}");
        }

        if (conversation == null)
        {
            logger.LogWarning("The 'payload' is null after deserialization");
            return Results.BadRequest("The 'payload' is null after deserialization.");
        }

        var conversationString = JsonSerializer.Serialize(conversation);
        var content = new StringContent(conversationString, System.Text.Encoding.UTF8, "application/json");

        using var client = new HttpClient();
        var response = await client.PostAsync(url, content);

        if (response.IsSuccessStatusCode)
        {
            var responseContent = await response.Content.ReadAsStringAsync();
            GeminiResponse? geminiResponse = null;
            try
            {
                geminiResponse = JsonSerializer.Deserialize<GeminiResponse>(responseContent);
            }
            catch (JsonException ex)
            {
                logger.LogError(ex, "Failed to deserialize Gemini response");
                return Results.Problem($"Failed to deserialize Gemini response. Error: {ex.Message}");
            }

            if (geminiResponse == null)
            {
                logger.LogWarning("The 'GeminiResponse' is null after deserialization");
                return Results.BadRequest("The 'GeminiResponse' is null after deserialization.");
            }

            logger.LogInformation("Successfully retrieved answer from Gemini");
            return Results.Ok(geminiResponse.Candidates[0].Content.Parts[0].Text);
        }
        else
        {
            var responseContent = await response.Content.ReadAsStringAsync();
            GeminiErrorResponse? geminiErrorResponse = null;
            try
            {
                geminiErrorResponse = JsonSerializer.Deserialize<GeminiErrorResponse>(responseContent);
            }
            catch (JsonException ex)
            {
                logger.LogError(ex, "Failed to deserialize Gemini error response");
                return Results.Problem($"Failed to deserialize Gemini error response. Error: {ex.Message}");
            }

            if (geminiErrorResponse == null || geminiErrorResponse.Error == null)
            {
                logger.LogError("Unknown error occurred");
                return Results.Problem("Unknown error occurred.");
            }

            return Results.Problem(geminiErrorResponse.Error.Message);
        }
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "An exception occurred");
        return Results.Problem(ex.Message);
    }
});

app.UseCors(policyBuilder =>
    policyBuilder.AllowAnyOrigin()
        .AllowAnyMethod()
        .AllowAnyHeader());

app.Run();