using System.CommandLine;
using System.CommandLine.Invocation;
using Microsoft.Extensions.DependencyInjection;
using VoxMind.Core.SpeakerRecognition;

namespace VoxMind.CLI.Commands;

public static class ListSpeakersCommand
{
    public static Command Build(IServiceProvider services)
    {
        var cmd = new Command("list-speakers", "Lister tous les profils de locuteurs");

        cmd.SetHandler(async (InvocationContext ctx) =>
        {
            var service = services.GetRequiredService<ISpeakerIdentificationService>();
            var profiles = await service.GetAllProfilesAsync();

            if (!profiles.Any())
            {
                Console.WriteLine("Aucun locuteur enregistré.");
                return;
            }

            Console.WriteLine($"\n{"ID",36}  {"Nom",-20}  {"Détections",10}  {"Vu le",-12}");
            Console.WriteLine(new string('-', 85));
            foreach (var p in profiles)
            {
                var lastSeen = p.LastSeenAt?.ToString("dd/MM/yyyy") ?? "jamais";
                Console.WriteLine($"{p.Id}  {p.Name,-20}  {p.DetectionCount,10}  {lastSeen,-12}");
            }
            Console.WriteLine($"\nTotal: {profiles.Count} locuteur(s)");
        });

        return cmd;
    }
}
