using System.Text.Json;
using AiAssistant.Api.Configuration;
using AiAssistant.Api.Data;
using AiAssistant.Api.Models;
using AiAssistant.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Minio;
using Refit;

var builder = WebApplication.CreateBuilder(args);

// Same physical Postgres instance as TicketSystem, own schema ("ai") and own
// migration history - see Data/AiDbContext.cs for why the two never collide.
builder.Services.AddDbContext<AiDbContext>(options =>
    options.UseNpgsql(
        builder.Configuration.GetConnectionString("AiDb"),
        npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "ai")));

builder.Services.Configure<OllamaOptions>(builder.Configuration.GetSection(OllamaOptions.SectionName));
builder.Services.Configure<QdrantOptions>(builder.Configuration.GetSection(QdrantOptions.SectionName));
builder.Services.Configure<RagOptions>(builder.Configuration.GetSection(RagOptions.SectionName));
builder.Services.Configure<AssistantOptions>(builder.Configuration.GetSection(AssistantOptions.SectionName));
builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection(StorageOptions.SectionName));

// Generated client, not the reflection-based one: Refit 15 emits the request
// builder at compile time, so there is no startup reflection cost and a bad
// interface fails the build instead of the first request.
builder.Services
    .AddRefitGeneratedClient<IOllamaApi>()
    .ConfigureHttpClient((sp, client) =>
    {
        var options = sp.GetRequiredService<IOptions<OllamaOptions>>().Value;
        client.BaseAddress = new Uri(options.Host);
        client.Timeout = TimeSpan.FromMinutes(options.TimeoutMinutes);
    });

builder.Services.AddSingleton<IMinioClient>(sp =>
{
    var options = sp.GetRequiredService<IOptions<StorageOptions>>().Value;
    return new MinioClient()
        .WithEndpoint(options.Endpoint)
        .WithCredentials(options.AccessKey, options.SecretKey)
        .WithSSL(options.UseSsl)
        .Build();
});

builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<RagOptions>>().Value);
builder.Services.AddSingleton<DocumentTextExtractor>();
builder.Services.AddSingleton<OllamaClient>();
builder.Services.AddSingleton<DocumentStorage>();
builder.Services.AddSingleton<DocumentIndex>();
builder.Services.AddSingleton<DocumentLibrary>();
builder.Services.AddScoped<ChatOrchestrator>();

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// Server-sent events. Every event is one JSON object: {"token": "..."} while
// the answer streams, then a final {"done": true, ...} carrying the sources
// used, so the client can render citations without a second request.
app.MapPost("/api/chat/stream", async (
    ChatRequest request,
    ChatOrchestrator orchestrator,
    ILogger<Program> logger,
    HttpContext http,
    CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Message))
    {
        await Results.BadRequest(new { error = "Savol matni bo'sh." }).ExecuteAsync(http);
        return;
    }

    http.Response.Headers.ContentType = "text/event-stream";
    http.Response.Headers.CacheControl = "no-cache";

    try
    {
        await foreach (var chunk in orchestrator.AskAsync(request.Message, request.Scope(), ct))
        {
            switch (chunk)
            {
                case AnswerEvent.Token token:
                    await WriteEventAsync(http, new { token = token.Text }, ct);
                    break;

                case AnswerEvent.Completed completed:
                    var summary = completed.Summary;
                    if (summary.LanguageDriftSuspected)
                        logger.LogWarning(
                            "Javob o'zbek tilidan chetlashgan bo'lishi mumkin. Scope={Scope}",
                            request.TicketScope ?? RetrievalScope.GlobalValue);

                    await WriteEventAsync(http, new
                    {
                        done = true,
                        answeredFromContext = summary.AnsweredFromContext,
                        sources = summary.ContextUsed.Select(c => c.Source).Distinct()
                    }, ct);
                    break;
            }
        }
    }
    catch (OllamaUnavailableException ex)
    {
        logger.LogError(ex, "Ollama javob bermadi");
        await WriteEventAsync(http, new { error = "Model hozir mavjud emas. Keyinroq urinib ko'ring." }, ct);
    }
});

// Stores one upload and indexes it. ticketScope (optional form field) keeps the
// document private to a single conversation instead of adding it to the shared
// knowledge base.
app.MapPost("/api/documents", async (
    HttpRequest http,
    DocumentLibrary library,
    ILogger<Program> logger,
    CancellationToken ct) =>
{
    if (!http.HasFormContentType)
        return Results.BadRequest(new { error = "multipart/form-data kutilgan." });

    var form = await http.ReadFormAsync(ct);
    var file = form.Files.GetFile("file");
    if (file is null || file.Length == 0)
        return Results.BadRequest(new { error = "Fayl topilmadi." });

    var ticketScope = form["ticketScope"].FirstOrDefault();
    var scope = string.IsNullOrWhiteSpace(ticketScope)
        ? RetrievalScope.Global
        : RetrievalScope.ForTicket(ticketScope);

    await using var content = file.OpenReadStream();
    var request = new IngestRequest(content, file.FileName, scope, form["language"].FirstOrDefault());

    try
    {
        var ingested = await library.AddAsync(request, progress: null, ct);
        return Results.Ok(new
        {
            status = "ingested",
            file = ingested.Document.FileName,
            objectKey = ingested.Document.ObjectKey,
            scope = ingested.Document.Scope.Value,
            chunks = ingested.ChunkCount
        });
    }
    catch (OllamaUnavailableException ex)
    {
        logger.LogError(ex, "Vektorlash muvaffaqiyatsiz. Fayl={FileName}", file.FileName);
        return Results.Problem("Vektorlash xizmati mavjud emas.", statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (DocumentStorageException ex)
    {
        logger.LogError(ex, "Fayl saqlanmadi. Fayl={FileName}", file.FileName);
        return Results.Problem("Fayl ombori mavjud emas.", statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.Run();

static async Task WriteEventAsync(HttpContext http, object payload, CancellationToken ct)
{
    await http.Response.WriteAsync($"data: {JsonSerializer.Serialize(payload)}\n\n", ct);
    await http.Response.Body.FlushAsync(ct);
}

internal record ChatRequest(string Message, string? TicketScope)
{
    public RetrievalScope? Scope() =>
        string.IsNullOrWhiteSpace(TicketScope) ? null : RetrievalScope.ForTicket(TicketScope);
}
