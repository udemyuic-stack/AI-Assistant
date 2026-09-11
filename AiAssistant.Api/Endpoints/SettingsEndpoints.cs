using AiAssistant.Api.Data;
using AiAssistant.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace AiAssistant.Api.Endpoints;

// Runtime-tunable RAG parameters (SysParamKeys) - the backend half of what
// the guide calls the admin panel. The panel itself (sliders) lives in
// TicketSystem's Blazor UI, which is the only place with an admin session to
// gate it behind; this API trusts the caller the same way /api/feedback does.
public static class SettingsEndpoints
{
    public static void MapSettingsEndpoints(this WebApplication app)
    {
        app.MapGet("/api/settings", async (AiDbContext db, CancellationToken ct) =>
        {
            var stored = await db.SysParams.AsNoTracking()
                .ToDictionaryAsync(p => p.Key, p => p.Value, ct);

            // Every known key is listed even if never set, so a client
            // rendering sliders doesn't need a separate "what exists" call.
            var settings = SysParamKeys.All.Select(key => new
            {
                key,
                value = stored.GetValueOrDefault(key),
                isDefault = !stored.ContainsKey(key)
            });

            return Results.Ok(settings);
        }).AddEndpointFilter<AdminKeyFilter>();

        // Admin-only, and validated: these values feed retrieval and
        // generation directly. An unvalidated TopK of 100000 or a negative
        // threshold would degrade or break every subsequent question, and
        // the failure would surface far from here.
        app.MapPut("/api/settings/{key}", async (
            string key,
            UpdateSettingRequest request,
            SysParamCache cache,
            CancellationToken ct) =>
        {
            if (!SysParamKeys.All.Contains(key))
                return Results.BadRequest(new { error = $"Noma'lum sozlama: {key}" });

            if (!SysParamKeys.IsValidValue(key, request.Value, out var reason))
                return Results.BadRequest(new { error = reason });

            await cache.SetAsync(key, request.Value, ct);
            return Results.Ok(new { key, value = request.Value });
        }).AddEndpointFilter<AdminKeyFilter>();
    }
}

public record UpdateSettingRequest(string Value);
