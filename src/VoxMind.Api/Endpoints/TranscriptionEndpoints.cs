using Microsoft.AspNetCore.Mvc;
using VoxMind.Api.DTOs;
using VoxMind.Core.SpeakerRecognition;
using VoxMind.Core.Transcription;

namespace VoxMind.Api.Endpoints;

public static class TranscriptionEndpoints
{
    public static IEndpointRouteBuilder MapTranscriptionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1/audio")
            .WithTags("Transcription");

        group.MapPost("/transcriptions", HandleTranscriptionAsync)
            .DisableAntiforgery()
            .Accepts<IFormFile>("multipart/form-data")
            .Produces<TranscriptionResponse>()
            .ProducesProblem(400)
            .ProducesProblem(500)
            .WithSummary("Transcrire un fichier audio")
            .WithDescription(
                "Compatible OpenAI /v1/audio/transcriptions. " +
                "Accepte MP3, WAV, OGG, Opus, WebM via FFmpeg. " +
                "Paramètre ?model=parakeet|cohere (défaut : parakeet). " +
                "Paramètre ?num_speakers=N : nombre de locuteurs attendus — force la fusion des clusters jusqu'à N (améliore la diarisation quand le nombre est connu). " +
                "La diarisation automatique crée les profils locuteurs à la volée.");

        return app;
    }

    private static async Task<IResult> HandleTranscriptionAsync(
        IFormFile file,
        [FromQuery] string? model,
        [FromQuery] string? language,
        [FromQuery(Name = "num_speakers")] int? numSpeakers,
        TranscriptionEngineRegistry registry,
        ISpeakerIdentificationService speakerSvc,
        ILogger<TranscriptionResponse> logger,
        CancellationToken ct)
    {
        if (file.Length == 0)
            return Results.Problem("Le fichier audio est vide.", statusCode: 400);

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        var tempPath = Path.Combine(Path.GetTempPath(), $"voxmind_{Guid.NewGuid()}{ext}");

        try
        {
            // Sauvegarder le fichier uploadé
            await using (var fs = File.Create(tempPath))
                await file.CopyToAsync(fs, ct);

            // Sélectionner le moteur de transcription
            var engine = registry.Get(model);
            logger.LogInformation("Moteur sélectionné : {Engine} (demandé: {Model})", engine.Info.ModelName, model ?? "défaut");

            if (!engine.Info.IsLoaded)
            {
                return Results.Problem(
                    $"Le moteur '{engine.Info.ModelName}' n'est pas disponible (modèle non chargé).",
                    statusCode: 503);
            }

            // ── Transcription ────────────────────────────────────────────────────
            var transcription = await engine.TranscribeFileAsync(tempPath, ct);

            // ── Diarisation automatique (best-effort) ────────────────────────────
            if (transcription.VadSegments is { Count: > 0 })
            {
                try
                {
                    var speakerMap = await speakerSvc.DiarizeSegmentsAsync(transcription.VadSegments, ct, numSpeakers);
                    foreach (var seg in transcription.Segments)
                    {
                        if (speakerMap.TryGetValue(seg.Id, out var label))
                        {
                            seg.SpeakerId = label.ProfileId;
                            seg.SpeakerName = label.Name;
                        }
                    }
                    logger.LogInformation("Diarisation : {Count} locuteur(s) détecté(s).",
                        speakerMap.Values.DistinctBy(l => l.ProfileId).Count());
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Diarisation ignorée (non bloquant).");
                }
            }

            return Results.Ok(TranscriptionMapper.Map(transcription));
        }
        catch (NotSupportedException ex)
        {
            return Results.Problem(ex.Message, statusCode: 503);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Erreur lors de la transcription de {FileName}.", file.FileName);
            return Results.Problem("Erreur interne lors de la transcription.", statusCode: 500);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }
}
