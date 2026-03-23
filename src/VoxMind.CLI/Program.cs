using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using System.CommandLine;
using VoxMind.CLI.Commands;
using VoxMind.CLI.Interactive;
using VoxMind.Core.Configuration;
using VoxMind.Core.Extensions;
using VoxMind.Core.RemoteClients;
using VoxMind.Core.Transcription;

namespace VoxMind.CLI;

internal class Program
{
    static async Task<int> Main(string[] args)
    {
        // Ensure directories exist before any I/O
        var dataDir = VoxMindDirectories.EnsureDirectories();
        Console.WriteLine($"Data directory: {dataDir}");

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

        // Builder WebApplication (remplace Host.CreateDefaultBuilder — nécessaire pour gRPC)
        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder(args);
        builder.Host.UseSerilog();
        builder.Services.AddVoxMind(config);

        // gRPC côté serveur (port 50052) si activé
        if (config.RemoteClients.Enabled)
        {
            builder.Services.AddGrpc();
            builder.WebHost.ConfigureKestrel(opts =>
            {
                // HTTP/2 uniquement sur le port gRPC
                opts.ListenAnyIP(config.RemoteClients.Port, lo =>
                    lo.Protocols = HttpProtocols.Http2);
            });
        }

        var app = builder.Build();

        if (config.RemoteClients.Enabled)
            app.MapGrpcService<AudioStreamReceiverService>();

        // Initialiser la DB
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VoxMind.Core.Database.VoxMindDbContext>();
            await db.Database.EnsureCreatedAsync(cts.Token);
        }

        // Initialiser le service de transcription Parakeet ONNX
        var transcription = app.Services.GetRequiredService<ITranscriptionService>();
        await transcription.LoadModelAsync(ModelSize.Small);

        // Si aucun argument : mode interactif
        if (args.Length == 0)
        {
            var interactive = new InteractiveMode(app.Services);
            var interactiveTask = interactive.RunAsync(cts.Token);
            if (config.RemoteClients.Enabled)
                await app.StartAsync(cts.Token);
            return await interactiveTask;
        }

        // Sinon : parser les arguments CLI
        var rootCommand = BuildRootCommand(app.Services);
        var exitCode = await rootCommand.InvokeAsync(args);
        if (config.RemoteClients.Enabled)
            await app.StopAsync();
        return exitCode;
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
