using VoxMind.Api.DTOs;
using VoxMind.Core.Tts;

namespace VoxMind.Api.Endpoints;

/// <summary>
/// Liste les moteurs TTS et leurs langues — équivalent <c>ModelEndpoints</c> côté STT.
/// </summary>
public static class VoiceEndpoints
{
    public static IEndpointRouteBuilder MapVoiceEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/v1/voices", HandleListAsync)
            .WithTags("Speech")
            .Produces<VoiceListResponse>()
            .WithSummary("Lister les moteurs et voix disponibles");
        return app;
    }

    private static IResult HandleListAsync(TtsEngineRegistry registry)
    {
        var entries = registry.ListAll()
            .Select(kv => new VoiceEngineEntry(
                Engine: kv.Key,
                IsLoaded: kv.Value.IsLoaded,
                AvailableLanguages: kv.Value.AvailableLanguages,
                ResidentLanguages: kv.Value.ResidentLanguages))
            .ToList();

        return Results.Ok(new VoiceListResponse(entries));
    }
}
