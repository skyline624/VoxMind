using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using VoxMind.CLI.Interactive;
using VoxMind.Core.Session;

namespace VoxMind.CLI.Commands;

public static class ResumeCommand
{
    public static Command Build(IServiceProvider services)
    {
        var cmd = new Command("resume", "Reprendre après une pause");
        cmd.SetHandler(async () =>
        {
            var manager = services.GetRequiredService<ISessionManager>();
            try
            {
                await manager.ResumeSessionAsync();
                ColorConsole.WriteInfo("Session reprise.");
            }
            catch (InvalidOperationException ex)
            {
                ColorConsole.WriteError(ex.Message);
            }
        });
        return cmd;
    }
}
