using AiAssistant.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpClient("ollama", (sp, client) =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    client.BaseAddress = new Uri(config["Ollama:Host"] ?? "http://localhost:11434");
    var timeoutMinutes = double.Parse(config["Ollama:TimeoutMinutes"] ?? "10");
    client.Timeout = TimeSpan.FromMinutes(timeoutMinutes);
});

builder.Services.AddSingleton<OllamaService>();
builder.Services.AddSingleton<QdrantRagService>();
builder.Services.AddScoped<ChatOrchestrator>();

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// SSE streaming chat. ticketScope (optional): "ticket:{id}" - scopes retrieval
// to the global knowledge base plus that ticket's own uploaded attachments.
app.MapPost("/api/chat/stream", async (
    ChatRequest request,
    ChatOrchestrator orchestrator,
    ILogger<Program> logger,
    HttpContext http,
    CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Message))
        return Results.BadRequest(new { error = "Message bo'sh bo'lishi mumkin emas." });

    http.Response.Headers.ContentType = "text/event-stream";
    http.Response.Headers.CacheControl = "no-cache";

    var full = new System.Text.StringBuilder();

    await foreach (var chunk in orchestrator.AskAsync(request.Message, request.TicketScope, ct))
    {
        full.Append(chunk);
        var payload = System.Text.Json.JsonSerializer.Serialize(new { token = chunk });
        await http.Response.WriteAsync($"data: {payload}\n\n", ct);
        await http.Response.Body.FlushAsync(ct);
    }

    if (ChatOrchestrator.LooksLikeRussianDrift(full.ToString()))
        logger.LogWarning("Javob rus tiliga chalg'igan bo'lishi mumkin. Savol: {Message}", request.Message);

    await http.Response.WriteAsync("data: [DONE]\n\n", ct);
    return Results.Empty;
});

// Multipart file upload -> chunk -> embed -> Qdrant. ticketScope (form field,
// optional): tag the ingested chunks so only that ticket's questions can see
// them, instead of joining the shared knowledge base.
app.MapPost("/api/documents/ingest", async (
    HttpRequest request,
    QdrantRagService rag) =>
{
    if (!request.HasFormContentType)
        return Results.BadRequest(new { error = "multipart/form-data kutilgan." });

    var form = await request.ReadFormAsync();
    var file = form.Files.GetFile("file");
    if (file is null || file.Length == 0)
        return Results.BadRequest(new { error = "Fayl topilmadi." });

    var scope = form["ticketScope"].FirstOrDefault() ?? QdrantRagService.GlobalScope;
    var language = form["language"].FirstOrDefault();

    var tmp = Path.GetTempFileName();
    try
    {
        await using (var fs = new FileStream(tmp, FileMode.Create))
            await file.CopyToAsync(fs);

        await rag.IngestAsync(tmp, file.FileName, scope, language);
        return Results.Ok(new { status = "ingested", file = file.FileName, scope });
    }
    finally
    {
        if (File.Exists(tmp)) File.Delete(tmp);
    }
});

app.Run();

internal record ChatRequest(string Message, string? TicketScope);
