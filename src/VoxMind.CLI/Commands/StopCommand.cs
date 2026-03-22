using System.CommandLine;
using System.CommandLine.Invocation;
using Microsoft.Extensions.DependencyInjection;
using VoxMind.CLI.Interactive;
using VoxMind.Core.Session;

namespace VoxMind.CLI.Commands;

public static class StopCommand
{
    public static Command Build(IServiceProvider services)
    {
        var cmd = new Command("stop", "Arrêter la session en cours et générer le résumé");

        cmd.SetHandler(async (InvocationContext ctx) =>
        {
            var manager = services.GetRequiredService<ISessionManager>();
            try
            {
                var session = await manager.StopSessionAsync();
                ColorConsole.WriteSuccess($"Session '{session.Name}' terminée. Durée: {session.Duration:hh\\:mm\\:ss}");
                if (session.Summary?.GeneratedSummary is not null)
                    Console.WriteLine($"\nRésumé: {session.Summary.GeneratedSummary}");
            }
            catch (InvalidOperationException ex)
            {
                ColorConsole.WriteError(ex.Message);
                Environment.Exit((int)ExitCode.NotListening);
            }
        });

        return cmd;
    }
}
