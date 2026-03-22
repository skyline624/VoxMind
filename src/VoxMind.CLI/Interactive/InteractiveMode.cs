using Microsoft.Extensions.DependencyInjection;
using VoxMind.Core.Session;
using VoxMind.Core.SpeakerRecognition;

namespace VoxMind.CLI.Interactive;

public class InteractiveMode
{
    private readonly IServiceProvider _services;

    public InteractiveMode(IServiceProvider services)
    {
        _services = services;
    }

    public async Task<int> RunAsync(CancellationToken ct)
    {
        ColorConsole.WriteHeader("VoxMind v1.0.0 — Mode Interactif");
        ColorConsole.WriteInfo("Tapez 'help' pour la liste des commandes, 'exit' pour quitter.\n");

        while (!ct.IsCancellationRequested)
        {
            ColorConsole.WritePrompt();
            string? line;
            try
            {
                line = Console.ReadLine();
            }
            catch (IOException)
            {
                break;
            }

            if (ct.IsCancellationRequested) break;
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.Trim().ToLowerInvariant() is "exit" or "quit") break;

            await ExecuteAsync(line.Trim(), ct);
        }

        return 0;
    }

    private async Task ExecuteAsync(string line, CancellationToken ct)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var command = parts[0].ToLowerInvariant();

        var sessionManager = _services.GetRequiredService<ISessionManager>();
        var speakerService = _services.GetRequiredService<ISpeakerIdentificationService>();

        try
        {
            switch (command)
            {
                case "help":
                    PrintHelp();
                    break;

                case "start":
                    var name = parts.Length > 1 ? string.Join(" ", parts[1..]) : null;
                    var session = await sessionManager.StartSessionAsync(name, ct);
                    ColorConsole.WriteSuccess($"Session '{session.Name}' démarrée (ID: {session.Id})");
                    break;

                case "stop":
                    var stopped = await sessionManager.StopSessionAsync();
                    ColorConsole.WriteSuccess($"Session terminée. Durée: {stopped.Duration:hh\\:mm\\:ss}");
                    if (stopped.Summary is not null)
                    {
                        Console.WriteLine($"\nRésumé: {stopped.Summary.GeneratedSummary}");
                        if (stopped.Summary.Decisions.Any())
                        {
                            Console.WriteLine("\nDécisions:");
                            foreach (var d in stopped.Summary.Decisions) Console.WriteLine($"  - {d}");
                        }
                    }
                    break;

                case "status":
                    if (sessionManager.Status == SessionStatus.Idle)
                        ColorConsole.WriteInfo("Statut: En veille");
                    else
                    {
                        var cur = sessionManager.CurrentSession!;
                        ColorConsole.WriteSessionStatus(
                            cur.Name ?? "sans nom",
                            cur.Duration.ToString(@"hh\:mm\:ss"),
                            Array.Empty<string>(),
                            cur.SegmentCount
                        );
                    }
                    break;

                case "pause":
                    await sessionManager.PauseSessionAsync();
                    ColorConsole.WriteInfo("Session mise en pause.");
                    break;

                case "resume":
                    await sessionManager.ResumeSessionAsync();
                    ColorConsole.WriteInfo("Session reprise.");
                    break;

                case "speakers":
                case "list-speakers":
                    var profiles = await speakerService.GetAllProfilesAsync();
                    if (!profiles.Any())
                        ColorConsole.WriteInfo("Aucun locuteur enregistré.");
                    else
                        foreach (var p in profiles)
                            Console.WriteLine($"  [{p.Id}] {p.Name} — {p.DetectionCount} détections, vu le {p.LastSeenAt?.ToString("dd/MM/yyyy") ?? "jamais"}");
                    break;

                default:
                    ColorConsole.WriteWarning($"Commande inconnue : '{command}'. Tapez 'help'.");
                    break;
            }
        }
        catch (InvalidOperationException ex)
        {
            ColorConsole.WriteError(ex.Message);
        }
        catch (Exception ex)
        {
            ColorConsole.WriteError($"Erreur inattendue: {ex.Message}");
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("\nCommandes disponibles:");
        Console.WriteLine("  start [nom]     Démarrer une session d'écoute");
        Console.WriteLine("  stop            Arrêter la session (génère le résumé)");
        Console.WriteLine("  status          Afficher le statut actuel");
        Console.WriteLine("  pause           Mettre en pause");
        Console.WriteLine("  resume          Reprendre après pause");
        Console.WriteLine("  speakers        Lister les profils de locuteurs");
        Console.WriteLine("  help            Afficher cette aide");
        Console.WriteLine("  exit            Quitter VoxMind\n");
    }
}
