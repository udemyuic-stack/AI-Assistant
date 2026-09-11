using System.Security.Cryptography;
using System.Text;
using AiAssistant.Api.Configuration;
using Microsoft.Extensions.Options;

namespace AiAssistant.Api.Services;

// Gates the endpoints that expose other people's data or change behaviour
// globally: the trace of one answer, the gold-set dump of every answer, and
// the runtime settings. The chat endpoint stays open - it is what
// TicketSystem calls on behalf of an already-authenticated user.
//
// A shared key, not per-user auth, because the only caller is TicketSystem
// server-side. If this API ever becomes reachable from a browser, this has to
// become real per-user authorization instead.
public class AdminKeyFilter(IOptions<AdminOptions> options, ILogger<AdminKeyFilter> logger)
    : IEndpointFilter
{
    public const string HeaderName = "X-Admin-Key";

    private readonly AdminOptions _options = options.Value;

    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var http = context.HttpContext;

        // An unset key means the endpoint is closed, not open: a deployment
        // that forgot to configure it must fail shut.
        if (string.IsNullOrEmpty(_options.ApiKey))
        {
            logger.LogError("Admin:ApiKey sozlanmagan - admin endpointlari yopiq.");
            return Results.Problem(
                "Admin kaliti sozlanmagan.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var provided = http.Request.Headers[HeaderName].FirstOrDefault();
        if (string.IsNullOrEmpty(provided) || !FixedTimeEquals(provided, _options.ApiKey))
        {
            logger.LogWarning(
                "Admin endpointiga ruxsatsiz urinish. Path={Path}, Ip={Ip}",
                http.Request.Path, http.Connection.RemoteIpAddress);
            return Results.Unauthorized();
        }

        return await next(context);
    }

    // Constant-time: a plain == leaks how many leading characters matched
    // through response timing, which turns guessing a key into a per-character
    // search instead of a brute force over the whole value.
    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
