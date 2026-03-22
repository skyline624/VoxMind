using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using VoxMind.CLI.Interactive;
using VoxMind.Core.Session;

namespace VoxMind.CLI.Commands;

public static class StatusCommand
{
    public static Command Build(IServiceProvider services)
    {
        var cmd = new Command("status", "Afficher le statut actuel");

        cmd.SetHandler(() =>
        {
            var manager = services.GetRequiredService<ISessionManager>();
            if (manager.Status == SessionStatus.Idle)
            {
                ColorConsole.WriteInfo("Statut: En veille (aucune session active)");
                return;
            }

            var session = manager.CurrentSession!;
            ColorConsole.WriteSessionStatus(
                session.Name ?? "sans nom",
                session.Duration.ToString(@"hh\:mm\:ss"),
                Array.Empty<string>(),
                session.SegmentCount
            );
            Console.WriteLine($"\nStatut: {manager.Status}");
        });

        return cmd;
    }
}
