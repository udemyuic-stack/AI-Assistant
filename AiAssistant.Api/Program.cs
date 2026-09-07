using System.Threading.RateLimiting;
using AiAssistant.Api.Configuration;
using AiAssistant.Api.Data;
using AiAssistant.Api.Endpoints;
using AiAssistant.Api.Services;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Minio;
using Qdrant.Client;
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
builder.Services.Configure<RateLimitOptions>(builder.Configuration.GetSection(RateLimitOptions.SectionName));

// One named policy - "ollama" - applied to every endpoint that triggers a
// model call (chat, upload, scrape). Partitioned per client IP since there is
// no authenticated identity yet (see task: guest mode / auth wiring); revisit
// the partition key once a user id is available so one person's budget isn't
// shared by everyone behind the same NAT.
builder.Services.AddRateLimiter(options =>
{
    options.OnRejected = (context, _) =>
    {
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        return ValueTask.CompletedTask;
    };

    options.AddPolicy("ollama", httpContext =>
    {
        var limit = httpContext.RequestServices
            .GetRequiredService<IOptions<RateLimitOptions>>().Value.RequestsPerMinute;
        var clientKey = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        return RateLimitPartition.GetFixedWindowLimiter(clientKey, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = limit,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        });
    });
});

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

builder.Services.AddHttpClient("scraper", client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("AiAssistant/1.0 (+geology-ministry)");
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

// One gRPC connection for the process, not one per request.
builder.Services.AddSingleton(sp =>
{
    var options = sp.GetRequiredService<IOptions<QdrantOptions>>().Value;
    return new QdrantClient(options.Host, options.GrpcPort);
});

builder.Services.AddMemoryCache();

builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<RagOptions>>().Value);
builder.Services.AddSingleton<DocumentTextExtractor>();
builder.Services.AddSingleton<OllamaClient>();
builder.Services.AddSingleton<DocumentStorage>();

// Scoped, not Singleton: these reach AiDbContext (via SysParamCache and
// SlangDictionary), which is Scoped. A Singleton holding it would be a
// captive dependency - one DbContext shared across every concurrent request.
builder.Services.AddScoped<SysParamCache>();
builder.Services.AddScoped<SlangDictionary>();
builder.Services.AddScoped<QueryPreprocessor>();
builder.Services.AddScoped<DocumentIndex>();
builder.Services.AddScoped<DocumentLibrary>();
builder.Services.AddScoped<WebScraperService>();
builder.Services.AddScoped<ChatOrchestrator>();

var app = builder.Build();

app.UseStaticFiles();
app.UseRateLimiter();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapChatEndpoints();
app.MapDocumentEndpoints();
app.MapSiteEndpoints();

app.Run();
