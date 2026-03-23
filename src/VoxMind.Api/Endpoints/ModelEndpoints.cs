using System.Text.Json.Serialization;
using VoxMind.Core.Transcription;

namespace VoxMind.Api.Endpoints;

public static class ModelEndpoints
{
    public static IEndpointRouteBuilder MapModelEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/v1/models", GetModelsAsync)
            .WithTags("Models")
            .Produces<ModelListResponse>()
            .WithSummary("Lister les moteurs de transcription disponibles");

        return app;
    }

    private static IResult GetModelsAsync(TranscriptionEngineRegistry registry)
    {
        var models = registry.ListAll().Select(e => new ModelEntry(
            Id: e.Name,
            Object: "model",
            Available: e.Info.IsLoaded,
            Backend: e.Info.Backend.ToString().ToLowerInvariant(),
            Note: e.Name == "cohere"
                ? "Requires Python gRPC backend (PyTorch 2B params Conformer)"
                : null
        )).ToList();

        return Results.Ok(new ModelListResponse("list", models));
    }
}

public record ModelListResponse(
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("data")]   IReadOnlyList<ModelEntry> Data
);

public record ModelEntry(
    [property: JsonPropertyName("id")]        string Id,
    [property: JsonPropertyName("object")]    string Object,
    [property: JsonPropertyName("available")] bool Available,
    [property: JsonPropertyName("backend")]   string Backend,
    [property: JsonPropertyName("note")]      string? Note
);
