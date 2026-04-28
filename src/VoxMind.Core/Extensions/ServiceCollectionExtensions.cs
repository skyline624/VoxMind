using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VoxMind.Core.Audio;
using VoxMind.Core.Configuration;
using VoxMind.Core.Database;
using VoxMind.Core.Session;
using VoxMind.Core.SpeakerRecognition;
using VoxMind.Core.Transcription;
using VoxMind.Core.Tts;
using VoxMind.Core.Vad;
using System.Runtime.InteropServices;

namespace VoxMind.Core.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddVoxMind(this IServiceCollection services, AppConfiguration config)
    {
        // Configuration
        services.AddSingleton(config);

        // Base de données — DbContextFactory : chaque opération crée son propre contexte court.
        // Indispensable pour la sécurité de concurrence : DbContext n'est PAS thread-safe et
        // les services consommateurs (SessionManager, SherpaOnnxSpeakerService) sont Singleton.
        services.AddDbContextFactory<VoxMindDbContext>(options =>
            options.UseSqlite($"Data Source={config.Database.Path}"));

        // Audio (sélection par plateforme)
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            services.AddSingleton<IAudioCapture, NAudioCapture>();
        else
            services.AddSingleton<IAudioCapture, PortAudioCapture>();

        // AudioSourceFactory (live / file)
        services.AddSingleton<AudioSourceFactory>();

        // VAD — Silero VAD via sherpa-onnx
        if (config.Ml.Vad.Enabled)
            services.AddSingleton<IVadService>(sp =>
                new SherpaOnnxVadService(
                    config.Ml.Vad,
                    sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SherpaOnnxVadService>>()));
        else
            services.AddSingleton<IVadService, DisabledVadService>();

        // Détecteur de langue post-hoc (NTextCat-style stopwords pour les 25 langues
        // supportées par Parakeet v3). Utilisé aussi côté TTS pour borner la synthèse
        // quand la requête API ne précise pas la langue cible.
        services.AddSingleton<ILanguageDetector>(sp =>
            new StopwordLanguageDetector(
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<StopwordLanguageDetector>>()
            )
        );

        // Moteur Parakeet ONNX (local, sans serveur Python)
        services.AddSingleton<ITranscriptionService>(sp =>
            new ParakeetOnnxTranscriptionService(
                config.Ml.Transcription.ParakeetModelPath,
                sp.GetRequiredService<IVadService>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ParakeetOnnxTranscriptionService>>(),
                sp.GetRequiredService<ILanguageDetector>()
            )
        );

        // Moteur Cohere (stub — requiert service Python gRPC)
        // Pattern à dupliquer pour ajouter un nouveau moteur de transcription.
        services.AddSingleton<CohereTranscriptionService>(sp =>
            new CohereTranscriptionService(
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<CohereTranscriptionService>>()
            )
        );

        // Registre multi-engine : ajouter une entrée ici pour enregistrer un nouveau moteur.
        services.AddSingleton<TranscriptionEngineRegistry>(sp =>
            new TranscriptionEngineRegistry(
                new Dictionary<string, ITranscriptionService>
                {
                    ["parakeet"] = sp.GetRequiredService<ITranscriptionService>(),
                    ["cohere"] = sp.GetRequiredService<CohereTranscriptionService>(),
                },
                defaultModel: config.Ml.Transcription.DefaultModel
            )
        );

        // ── Synthèse vocale (TTS) ──────────────────────────────────────────────
        // F5-TTS-ONNX : moteur principal, fine-tunes par langue, voice cloning zero-shot.
        if (config.Ml.Tts.Enabled)
        {
            services.AddSingleton<F5TtsOnnxService>(sp =>
                new F5TtsOnnxService(
                    config.Ml.Tts,
                    sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<F5TtsOnnxService>>()
                )
            );

            // Coqui XTTS-v2 — stub (pas d'export ONNX officiel ; pattern d'extension futur).
            services.AddSingleton<CoquiXttsTtsService>(sp =>
                new CoquiXttsTtsService(
                    sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<CoquiXttsTtsService>>()
                )
            );

            // Registry multi-engine. Ajouter une entrée ici pour brancher Zipvoice / Piper / etc.
            services.AddSingleton<TtsEngineRegistry>(sp =>
                new TtsEngineRegistry(
                    new Dictionary<string, ITtsService>
                    {
                        ["f5"] = sp.GetRequiredService<F5TtsOnnxService>(),
                        ["xtts"] = sp.GetRequiredService<CoquiXttsTtsService>(),
                    },
                    defaultEngine: config.Ml.Tts.DefaultEngine
                )
            );

            // Façade <see cref="ITtsService"/> = le moteur par défaut, pratique pour la CLI.
            services.AddSingleton<ITtsService>(sp =>
                sp.GetRequiredService<TtsEngineRegistry>().Get(config.Ml.Tts.DefaultEngine));
        }

        // Speaker recognition — sherpa-onnx (local, sans serveur Python)
        services.AddSingleton<ISpeakerIdentificationService>(sp =>
            new SherpaOnnxSpeakerService(
                config.Ml.SpeakerRecognition,
                sp.GetRequiredService<IDbContextFactory<VoxMindDbContext>>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SherpaOnnxSpeakerService>>()
            )
        );

        // Session
        services.AddSingleton<ISummaryGenerator, SummaryGenerator>();
        services.AddSingleton<ISessionManager>(sp =>
            new SessionManager(
                sp.GetRequiredService<IAudioCapture>(),
                sp.GetRequiredService<ITranscriptionService>(),
                sp.GetRequiredService<ISpeakerIdentificationService>(),
                sp.GetRequiredService<ISummaryGenerator>(),
                sp.GetRequiredService<IDbContextFactory<VoxMindDbContext>>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SessionManager>>(),
                config.Session.OutputFolder
            )
        );

        return services;
    }

    public static IServiceCollection AddVoxMindDatabase(this IServiceCollection services, string dbPath)
    {
        services.AddDbContextFactory<VoxMindDbContext>(options =>
            options.UseSqlite($"Data Source={dbPath}"));
        return services;
    }
}
