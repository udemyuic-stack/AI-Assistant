using System.Net;
using System.Net.Sockets;
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

// No fallback connection string lives in appsettings.json - one used to sit
// there as a "CHANGE_ME" placeholder, which meant a deployment that forgot
// to set the real value didn't fail at startup; it failed confusingly on the
// first request with a Postgres auth error, far from the actual mistake.
// Failing here, with a message that says what to set, is the same "fail
// shut, not fail open" call as AdminKeyFilter makes for a missing admin key.
var aiDbConnectionString = builder.Configuration.GetConnectionString("AiDb");
if (string.IsNullOrWhiteSpace(aiDbConnectionString) || aiDbConnectionString.Contains("CHANGE_ME"))
    throw new InvalidOperationException(
        "ConnectionStrings:AiDb sozlanmagan. ConnectionStrings__AiDb muhit o'zgaruvchisini yoki " +
        "appsettings.Development.json/Vault orqali bering - ishlab chiqarish sirlari appsettings.json'da saqlanmaydi.");

// Same physical Postgres instance as TicketSystem, own schema ("ai") and own
// migration history - see Data/AiDbContext.cs for why the two never collide.
builder.Services.AddDbContext<AiDbContext>(options =>
    options.UseNpgsql(
        aiDbConnectionString,
        npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "ai")));

builder.Services.Configure<OllamaOptions>(builder.Configuration.GetSection(OllamaOptions.SectionName));
builder.Configuration.GetSection(OllamaOptions.SectionName).Get<OllamaOptions>()?.Validate();
builder.Services.Configure<QdrantOptions>(builder.Configuration.GetSection(QdrantOptions.SectionName));
builder.Services.Configure<RagOptions>(builder.Configuration.GetSection(RagOptions.SectionName));
builder.Services.Configure<AssistantOptions>(builder.Configuration.GetSection(AssistantOptions.SectionName));
builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection(StorageOptions.SectionName));
builder.Services.Configure<RateLimitOptions>(builder.Configuration.GetSection(RateLimitOptions.SectionName));
builder.Services.Configure<AdminOptions>(builder.Configuration.GetSection(AdminOptions.SectionName));

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

// Checked at connect time, not just against the URL string beforehand: a
// SocketsHttpHandler.ConnectCallback sees the actual IP the connection is
// about to open, after DNS resolution - a hostname that resolves to a
// public IP during validation and a private one by the time of connect
// (DNS rebinding) can't slip through. See PrivateNetworkGuard for why the
// scraper specifically needs this - it fetches URLs an admin typed in.
builder.Services.AddHttpClient("scraper", client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("AiAssistant/1.0 (+geology-ministry)");
}).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
{
    ConnectCallback = async (context, ct) =>
    {
        var entry = await Dns.GetHostEntryAsync(context.DnsEndPoint.Host, ct);
        var address = entry.AddressList.FirstOrDefault()
            ?? throw new SocketException((int)SocketError.HostNotFound);

        if (PrivateNetworkGuard.IsBlocked(address))
            throw new HttpRequestException(
                $"'{context.DnsEndPoint.Host}' ({address}) ichki/ayrilgan tarmoq manzili - scraper uchun ruxsat etilmagan.");

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(address, context.DnsEndPoint.Port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
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
builder.Services.AddScoped<Bm25Index>();
builder.Services.AddScoped<SlangDictionary>();
builder.Services.AddScoped<QueryPreprocessor>();
builder.Services.AddScoped<HistoryContextualizer>();
builder.Services.AddScoped<DocumentIndex>();
builder.Services.AddScoped<DocumentLibrary>();
builder.Services.AddScoped<WebScraperService>();
builder.Services.AddScoped<QuestionLog>();
builder.Services.AddScoped<GuestLimiter>();
builder.Services.AddScoped<InlineLinkIngestor>();
builder.Services.AddScoped<ChatOrchestrator>();
builder.Services.AddScoped<AdminKeyFilter>();

var app = builder.Build();

app.UseStaticFiles();
app.UseRateLimiter();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapChatEndpoints();
app.MapDocumentEndpoints();
app.MapSiteEndpoints();
app.MapFeedbackEndpoints();
app.MapRubricEndpoints();
app.MapSettingsEndpoints();
app.MapEvaluationEndpoints();

app.Run();
