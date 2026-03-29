using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VoxMind.Core.Audio;
using VoxMind.Core.Configuration;
using VoxMind.Core.Database;
using VoxMind.Core.Session;
using VoxMind.Core.SpeakerRecognition;
using VoxMind.Core.Transcription;
using VoxMind.Core.Vad;
using System.Runtime.InteropServices;

namespace VoxMind.Core.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddVoxMind(this IServiceCollection services, AppConfiguration config)
    {
        // Configuration
        services.AddSingleton(config);

        // Base de données — Singleton pour compatibilité avec les services Singleton (SessionManager, etc.)
        services.AddDbContext<VoxMindDbContext>(options =>
            options.UseSqlite($"Data Source={config.Database.Path}"),
            ServiceLifetime.Singleton);

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

        // Moteur Parakeet ONNX (local, sans serveur Python)
        services.AddSingleton<ITranscriptionService>(sp =>
            new ParakeetOnnxTranscriptionService(
                config.Ml.Transcription.ParakeetModelPath,
                sp.GetRequiredService<IVadService>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ParakeetOnnxTranscriptionService>>()
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
                    ["cohere"]   = sp.GetRequiredService<CohereTranscriptionService>(),
                },
                defaultModel: config.Ml.Transcription.DefaultModel
            )
        );

        // Speaker recognition — sherpa-onnx (local, sans serveur Python)
        services.AddSingleton<ISpeakerIdentificationService>(sp =>
            new SherpaOnnxSpeakerService(
                config.Ml.SpeakerRecognition,
                sp.GetRequiredService<VoxMindDbContext>(),
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
                sp.GetRequiredService<VoxMindDbContext>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SessionManager>>(),
                config.Session.OutputFolder
            )
        );

        return services;
    }

    public static IServiceCollection AddVoxMindDatabase(this IServiceCollection services, string dbPath)
    {
        services.AddDbContext<VoxMindDbContext>(options =>
            options.UseSqlite($"Data Source={dbPath}"),
            ServiceLifetime.Singleton);
        return services;
    }
}
