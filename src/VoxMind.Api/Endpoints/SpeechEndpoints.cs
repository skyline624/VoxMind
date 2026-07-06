using Microsoft.AspNetCore.Mvc;
using VoxMind.Api.DTOs;
using VoxMind.Core.Audio;
using VoxMind.Core.Configuration;
using VoxMind.Core.Transcription;
using VoxMind.Core.Tts;

namespace VoxMind.Api.Endpoints;

/// <summary>
/// Endpoint OpenAI-compatible pour la synthèse vocale (TTS).
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
                "Audio 24 kHz mono streamé en Transfer-Encoding: chunked, synthétisé phrase par phrase " +
                "(premier son ≈ 1ʳᵉ phrase). response_format : 'wav' (défaut, en-tête streaming) ou 'pcm' " +
                "(PCM16 brut sans en-tête, recommandé pour la lecture progressive).");

        return app;
    }

    private static async Task<IResult> HandleSpeechAsync(
        [FromBody] SpeechRequest request,
        [FromServices] TtsEngineRegistry registry,
        [FromServices] AppConfiguration config,
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

        // Format de sortie. 'pcm' = PCM16 brut 24 kHz sans en-tête (le plus propre à streamer) ; sinon WAV
        // avec en-tête « streaming » (tailles sentinelles). Les deux sont émis au fil de l'eau, phrase par
        // phrase, en chunked transfer — un client à lecture progressive démarre dès le 1ᵉʳ segment.
        var format = (request.ResponseFormat ?? "wav").Trim().ToLowerInvariant();
        bool rawPcm = format is "pcm";
        string contentType = rawPcm ? "audio/pcm" : "audio/wav";

        // On tire le 1ᵉʳ segment AVANT d'ouvrir le flux de réponse : les erreurs de configuration (modèle
        // absent, voix espeak inconnue) remontent ainsi en 503 propre, plutôt qu'en flux tronqué une fois
        // les en-têtes 200 déjà envoyés.
        var enumerator = engine.SynthesizeStreamAsync(request.Input, language, request.Instructions, request.Voice, ct).GetAsyncEnumerator(ct);
        bool hasFirst;
        try
        {
            hasFirst = await enumerator.MoveNextAsync();
        }
        catch (NotSupportedException ex)
        {
            await enumerator.DisposeAsync();
            logger.LogWarning(ex, "TTS indisponible pour la requête.");
            return Results.Problem(ex.Message, statusCode: 503);
        }
        catch (FileNotFoundException ex)
        {
            await enumerator.DisposeAsync();
            logger.LogWarning(ex, "TTS : ressource manquante.");
            return Results.Problem(ex.Message, statusCode: 503);
        }
        catch (OperationCanceledException)
        {
            await enumerator.DisposeAsync();
            return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
        }
        catch (Exception ex)
        {
            await enumerator.DisposeAsync();
            logger.LogError(ex, "Erreur lors de la synthèse TTS.");
            return Results.Problem("Erreur interne lors de la synthèse vocale.", statusCode: 500);
        }

        return Results.Stream(async output =>
        {
            try
            {
                if (hasFirst)
                {
                    if (!rawPcm)
                        WavWriter.WriteStreamingHeader(output, enumerator.Current.SampleRate, channels: 1);

                    WavWriter.WritePcm16(output, enumerator.Current.Pcm);
                    await output.FlushAsync(ct).ConfigureAwait(false);

                    while (await enumerator.MoveNextAsync().ConfigureAwait(false))
                    {
                        WavWriter.WritePcm16(output, enumerator.Current.Pcm);
                        await output.FlushAsync(ct).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Client déconnecté en cours de lecture — rien à faire.
            }
            catch (Exception ex)
            {
                // En-têtes 200 déjà envoyés : impossible de changer le statut. On coupe le flux et on logge.
                logger.LogError(ex, "Erreur pendant le streaming TTS (flux tronqué).");
            }
            finally
            {
                await enumerator.DisposeAsync();
            }
        }, contentType: contentType);
    }
}
