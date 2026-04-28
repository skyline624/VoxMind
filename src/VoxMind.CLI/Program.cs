using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
        var dataDir = VoxMindDirectories.EnsureDirectories();
        Console.WriteLine($"Data directory: {dataDir}");

        var config = ConfigurationLoader.LoadOrDefault();

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

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
            Console.WriteLine("\nArrêt en cours...");
        };

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddSerilog());
        services.AddVoxMind(config);
        var sp = services.BuildServiceProvider();

        // Initialiser la DB via le factory (DbContext n'est plus enregistré directement)
        var dbFactory = sp.GetRequiredService<IDbContextFactory<VoxMind.Core.Database.VoxMindDbContext>>();
        await using (var db = await dbFactory.CreateDbContextAsync(cts.Token))
            await db.Database.EnsureCreatedAsync(cts.Token);

        // Charger le modèle de transcription
        var transcription = sp.GetRequiredService<ITranscriptionService>();
        await transcription.LoadModelAsync(ModelSize.Small);

        if (args.Length == 0)
        {
            var interactive = new InteractiveMode(sp);
            return await interactive.RunAsync(cts.Token);
        }

        var rootCommand = BuildRootCommand(sp);
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
        root.AddCommand(SpeakCommand.Build(services));
        root.AddCommand(EnrollCommand.Build(services));
        root.AddCommand(ListSpeakersCommand.Build(services));
        root.AddCommand(SessionCommands.Build(services));

        return root;
    }
}
