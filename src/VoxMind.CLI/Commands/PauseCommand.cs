using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using VoxMind.CLI.Interactive;
using VoxMind.Core.Session;

namespace VoxMind.CLI.Commands;

public static class PauseCommand
{
    public static Command Build(IServiceProvider services)
    {
        var cmd = new Command("pause", "Mettre en pause la session");
        cmd.SetHandler(async () =>
        {
            var manager = services.GetRequiredService<ISessionManager>();
            try
            {
                await manager.PauseSessionAsync();
                ColorConsole.WriteInfo("Session mise en pause.");
            }
            catch (InvalidOperationException ex)
            {
                ColorConsole.WriteError(ex.Message);
            }
        });
        return cmd;
    }
}
