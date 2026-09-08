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
        });

        app.MapPut("/api/settings/{key}", async (
            string key,
            UpdateSettingRequest request,
            SysParamCache cache,
            CancellationToken ct) =>
        {
            if (!SysParamKeys.All.Contains(key))
                return Results.BadRequest(new { error = $"Noma'lum sozlama: {key}" });

            await cache.SetAsync(key, request.Value, ct);
            return Results.Ok(new { key, value = request.Value });
        });
    }
}

public record UpdateSettingRequest(string Value);
