using System.Text.Json.Serialization;
using VoxMind.Core.Configuration;
using VoxMind.Core.Session;
using VoxMind.Core.Transcription;

namespace VoxMind.Api.Endpoints;

public static class StatusEndpoints
{
    private static readonly DateTime _startedAt = DateTime.UtcNow;

    public static IEndpointRouteBuilder MapStatusEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/v1/status", GetStatusAsync)
            .WithTags("Status")
            .Produces<StatusResponse>()
            .WithSummary("État du service VoxMind");

        return app;
    }

    private static IResult GetStatusAsync(
        TranscriptionEngineRegistry registry,
        ISessionManager sessionManager,
        AppConfiguration config)
    {
        var backend = ComputeBackendDetector.DetectBestAvailable().ToString().ToLowerInvariant();
        var models = registry.ListAll()
            .Select(e => new ModelStatusEntry(e.Name, e.Info.IsLoaded, e.Info.Backend.ToString().ToLowerInvariant()))
            .ToList();

        var uptime = DateTime.UtcNow - _startedAt;
        var session = sessionManager.CurrentSession;

        return Results.Ok(new StatusResponse(
            Status: "ok",
            Version: config.Application.Version,
            UptimeSeconds: (long)uptime.TotalSeconds,
            ComputeBackend: backend,
            Models: models,
            ActiveSession: session is not null
                ? new ActiveSessionInfo(session.Id, session.Name ?? "unnamed", session.StartedAt)
                : null
        ));
    }
}

public record StatusResponse(
    [property: JsonPropertyName("status")]          string Status,
    [property: JsonPropertyName("version")]         string Version,
    [property: JsonPropertyName("uptime_seconds")]  long UptimeSeconds,
    [property: JsonPropertyName("compute_backend")] string ComputeBackend,
    [property: JsonPropertyName("models")]          IReadOnlyList<ModelStatusEntry> Models,
    [property: JsonPropertyName("active_session")]  ActiveSessionInfo? ActiveSession
);

public record ModelStatusEntry(
    [property: JsonPropertyName("id")]        string Id,
    [property: JsonPropertyName("loaded")]    bool Loaded,
    [property: JsonPropertyName("backend")]   string Backend
);

public record ActiveSessionInfo(
    [property: JsonPropertyName("id")]         Guid Id,
    [property: JsonPropertyName("name")]       string Name,
    [property: JsonPropertyName("started_at")] DateTime StartedAt
);
