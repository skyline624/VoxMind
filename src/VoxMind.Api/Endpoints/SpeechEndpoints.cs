using Microsoft.AspNetCore.Mvc;
using VoxMind.Api.DTOs;
using VoxMind.Core.Configuration;
using VoxMind.Core.Transcription;
using VoxMind.Core.Tts;

namespace VoxMind.Api.Endpoints;

/// <summary>
/// Endpoint OpenAI-compatible pour la synthèse vocale F5-TTS.
/// </summary>
public static class SpeechEndpoints
{
    public static IEndpointRouteBuilder MapSpeechEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1/audio").WithTags("Speech");

        group.MapPost("/speech", HandleSpeechAsync)
            .Produces(StatusCodes.Status200OK, contentType: "audio/wav")
            .ProducesProblem(400)
            .ProducesProblem(503)
            .ProducesProblem(500)
            .WithSummary("Synthétiser un texte en audio (TTS)")
            .WithDescription(
                "Compatible OpenAI /v1/audio/speech (sous-ensemble). " +
                "Body JSON : { input, language?, model?, voice?, response_format? }. " +
                "Si language est absent, la langue est détectée depuis le texte (FR/EN). " +
                "Retourne du WAV PCM 24 kHz mono.");

        return app;
    }

    private static async Task<IResult> HandleSpeechAsync(
        [FromBody] SpeechRequest request,
        TtsEngineRegistry registry,
        AppConfiguration config,
        ILanguageDetector languageDetector,
        ILogger<SpeechRequest> logger,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Input))
            return Results.Problem("Le champ 'input' est requis.", statusCode: 400);

        var engine = registry.Get(request.Model);
        if (!engine.Info.IsLoaded)
            return Results.Problem(
                $"Le moteur TTS '{engine.Info.EngineName}' n'a aucun checkpoint chargé. " +
                "Voir docs/F5TtsExport.md pour la procédure.",
                statusCode: 503);

        // Résolution de la langue : (1) explicite > (2) détection sur le texte > (3) défaut config
        string language;
        if (!string.IsNullOrWhiteSpace(request.Language))
        {
            language = request.Language!;
        }
        else
        {
            var detected = languageDetector.DetectLanguage(
                request.Input,
                engine.Info.AvailableLanguages);
            language = detected != "und" ? detected : config.Ml.Tts.DefaultLanguage;
            logger.LogInformation(
                "TTS : langue non précisée, détection auto = {Lang} (texte de {Chars} char).",
                language, request.Input.Length);
        }

        try
        {
            var result = await engine.SynthesizeAsync(
                request.Input,
                language,
                referenceWav: null,
                referenceText: null,
                ct);

            return Results.File(
                result.ToWavBytes(),
                contentType: "audio/wav",
                fileDownloadName: $"speech-{language}.wav");
        }
        catch (NotSupportedException ex)
        {
            logger.LogWarning(ex, "TTS indisponible pour la requête.");
            return Results.Problem(ex.Message, statusCode: 503);
        }
        catch (FileNotFoundException ex)
        {
            logger.LogWarning(ex, "TTS : ressource manquante.");
            return Results.Problem(ex.Message, statusCode: 503);
        }
        catch (OperationCanceledException)
        {
            return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Erreur lors de la synthèse TTS.");
            return Results.Problem("Erreur interne lors de la synthèse vocale.", statusCode: 500);
        }
    }
}
