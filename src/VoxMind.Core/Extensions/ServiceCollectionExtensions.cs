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

        // gRPC PyAnnote
        services.AddSingleton<IPyAnnoteClient>(sp =>
            new PyAnnoteGrpcClient(
                config.Ml.SpeakerRecognition.PyannoteEndpoint,
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<PyAnnoteGrpcClient>>()
            )
        );

        // Services ML
        services.AddSingleton<ITranscriptionService, WhisperService>();
        services.AddSingleton<ISpeakerIdentificationService>(sp =>
            new SpeakerIdentificationService(
                sp.GetRequiredService<VoxMindDbContext>(),
                sp.GetRequiredService<IPyAnnoteClient>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SpeakerIdentificationService>>(),
                config.Ml.SpeakerRecognition.ConfidenceThreshold
            )
        );

        // Session
        services.AddSingleton<ISummaryGenerator, SummaryGenerator>();
        services.AddSingleton<ISessionManager>(sp =>
            new SessionManager(
                sp.GetRequiredService<IAudioCapture>(),
                sp.GetRequiredService<ITranscriptionService>(),
                sp.GetRequiredService<ISpeakerIdentificationService>(),
                sp.GetRequiredService<IPyAnnoteClient>(),
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
