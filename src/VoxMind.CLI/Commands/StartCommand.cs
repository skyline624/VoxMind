using System.CommandLine;
using System.CommandLine.Invocation;
using Microsoft.Extensions.DependencyInjection;
using VoxMind.CLI.Interactive;
using VoxMind.Core.Session;

namespace VoxMind.CLI.Commands;

public static class StartCommand
{
    public static Command Build(IServiceProvider services)
    {
        var nameOpt = new Option<string?>("--name", "Nom de la session");
        var cmd = new Command("start", "Démarrer une session d'écoute") { nameOpt };

        cmd.SetHandler(async (InvocationContext ctx) =>
        {
            var name = ctx.ParseResult.GetValueForOption(nameOpt);
            var ct = ctx.GetCancellationToken();
            var manager = services.GetRequiredService<ISessionManager>();
            try
            {
                var session = await manager.StartSessionAsync(name, "live", null, ct);
                ColorConsole.WriteSuccess($"Session '{session.Name}' démarrée (ID: {session.Id})");
            }
            catch (InvalidOperationException ex)
            {
                ColorConsole.WriteError(ex.Message);
            }
        });

        return cmd;
    }
}
