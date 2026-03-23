using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using VoxMind.Core.Audio;
using VoxMind.Core.Bridge;
using VoxMind.Core.Configuration;
using VoxMind.Core.Database;
using VoxMind.Core.RemoteClients;
using VoxMind.Core.Session;
using VoxMind.Core.SpeakerRecognition;
using VoxMind.Core.Transcription;
using System.Runtime.InteropServices;

namespace VoxMind.Core.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddVoxMind(this IServiceCollection services, AppConfiguration config)
    {
        // Configuration
        services.AddSingleton(config);

        // Base de données
        services.AddDbContext<VoxMindDbContext>(options =>
            options.UseSqlite($"Data Source={config.Database.Path}"));

        // Audio (sélection par plateforme)
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            services.AddSingleton<IAudioCapture, NAudioCapture>();
        else
            services.AddSingleton<IAudioCapture, PortAudioCapture>();

        // Services ML — transcription Parakeet ONNX (local, sans serveur Python)
        services.AddSingleton<ITranscriptionService>(sp =>
            new ParakeetOnnxTranscriptionService(
                config.Ml.Transcription.ParakeetModelPath,
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ParakeetOnnxTranscriptionService>>()
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

        // Clients distants
        services.AddSingleton<IRemoteClientRegistry>(sp =>
            new RemoteClientRegistry(
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<RemoteClientRegistry>>(),
                config.RemoteClients.HeartbeatTimeoutSeconds
            )
        );

        // Bridge
        services.AddSingleton<IExternalBridge>(sp =>
            new FileBridge(
                config.Bridge.SharedFolder,
                sp.GetRequiredService<ISessionManager>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<FileBridge>>(),
                sp.GetService<IHostApplicationLifetime>(),
                config.Bridge.PollIntervalMs,
                config.Bridge.StatusUpdateIntervalSeconds,
                sp.GetRequiredService<IRemoteClientRegistry>()
            )
        );

        return services;
    }

    public static IServiceCollection AddVoxMindDatabase(this IServiceCollection services, string dbPath)
    {
        services.AddDbContext<VoxMindDbContext>(options => options.UseSqlite($"Data Source={dbPath}"));
        return services;
    }
}
