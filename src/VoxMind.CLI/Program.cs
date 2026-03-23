using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using System.CommandLine;
using VoxMind.CLI.Commands;
using VoxMind.CLI.Interactive;
using VoxMind.Core.Configuration;
using VoxMind.Core.Extensions;
using VoxMind.Core.Transcription;

namespace VoxMind.CLI;

internal class Program
{
    static async Task<int> Main(string[] args)
    {
        // Chargement de la configuration
        var config = ConfigurationLoader.LoadOrDefault();

        // Configuration Serilog
        var loggerConfig = new LoggerConfiguration()
            .MinimumLevel.Is(Serilog.Events.LogEventLevel.Information);

        if (config.Logging.Console.Enabled)
            loggerConfig.WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}");

        if (config.Logging.File.Enabled)
            loggerConfig.WriteTo.File(
                config.Logging.File.Path.Replace("{date}", DateTime.Now.ToString("yyyyMMdd")),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: config.Logging.File.RetainedFileCount);

        Log.Logger = loggerConfig.CreateLogger();

        // Gestion propre de SIGINT/SIGTERM
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
            Console.WriteLine("\nArrêt en cours...");
        };

        // Conteneur DI
        var host = Host.CreateDefaultBuilder()
            .UseSerilog()
            .ConfigureServices(services => services.AddVoxMind(config))
            .Build();

        // Initialiser la DB
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VoxMind.Core.Database.VoxMindDbContext>();
            await db.Database.EnsureCreatedAsync(cts.Token);
        }

        // Charger le modèle Whisper au démarrage
        var transcription = host.Services.GetRequiredService<VoxMind.Core.Transcription.ITranscriptionService>();
        var modelSize = config.Ml.Transcription.Model.ToLowerInvariant() switch
        {
            "tiny" => ModelSize.Tiny,
            "small" => ModelSize.Small,
            "medium" => ModelSize.Medium,
            "large" => ModelSize.Large,
            _ => ModelSize.Base
        };
        await transcription.LoadModelAsync(modelSize);

        // Si aucun argument : mode interactif
        if (args.Length == 0)
        {
            var interactive = new InteractiveMode(host.Services);
            return await interactive.RunAsync(cts.Token);
        }

        // Sinon : parser les arguments CLI
        var rootCommand = BuildRootCommand(host.Services);
        return await rootCommand.InvokeAsync(args);
    }

    private static RootCommand BuildRootCommand(IServiceProvider services)
    {
        var root = new RootCommand("VoxMind — Transcription vocale temps réel");

        root.AddCommand(StartCommand.Build(services));
        root.AddCommand(StopCommand.Build(services));
        root.AddCommand(StatusCommand.Build(services));
        root.AddCommand(PauseCommand.Build(services));
        root.AddCommand(ResumeCommand.Build(services));
        root.AddCommand(TranscribeCommand.Build(services));
        root.AddCommand(EnrollCommand.Build(services));
        root.AddCommand(ListSpeakersCommand.Build(services));
        root.AddCommand(SessionCommands.Build(services));

        return root;
    }
}
