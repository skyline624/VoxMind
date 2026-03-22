using System.CommandLine;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using VoxMind.Core.Configuration;

namespace VoxMind.CLI.Commands;

public static class SessionCommands
{
    public static Command Build(IServiceProvider services)
    {
        var cmd = new Command("session", "Gérer les sessions enregistrées");

        // session list
        var listCmd = new Command("list", "Lister toutes les sessions");
        listCmd.SetHandler(() =>
        {
            var config = services.GetRequiredService<AppConfiguration>();
            var folder = config.Session.OutputFolder;
            if (!Directory.Exists(folder))
            {
                Console.WriteLine("Aucune session enregistrée.");
                return;
            }

            var sessionDirs = Directory.GetDirectories(folder).OrderByDescending(d => d).ToList();
            if (!sessionDirs.Any())
            {
                Console.WriteLine("Aucune session enregistrée.");
                return;
            }

            Console.WriteLine($"\n{"ID",-36}  {"Nom",-25}  {"Durée",8}");
            Console.WriteLine(new string('-', 75));
            foreach (var dir in sessionDirs.Take(20))
            {
                var jsonFile = Path.Combine(dir, "session.json");
                if (!File.Exists(jsonFile)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(jsonFile));
                    var root = doc.RootElement;
                    var id = root.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : Path.GetFileName(dir);
                    var name = root.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "";
                    var duration = root.TryGetProperty("duration_seconds", out var durEl) ? $"{durEl.GetInt32() / 60}min" : "?";
                    Console.WriteLine($"{id,-36}  {name,-25}  {duration,8}");
                }
                catch { /* Ignorer les fichiers corrompus */ }
            }
        });

        // session summary <id>
        var idArg = new Argument<string>("id", "ID de la session");
        var summaryCmd = new Command("summary", "Afficher le résumé d'une session") { idArg };
        summaryCmd.SetHandler((string id) =>
        {
            var config = services.GetRequiredService<AppConfiguration>();
            var jsonFile = Path.Combine(config.Session.OutputFolder, id, "session.json");
            if (!File.Exists(jsonFile))
            {
                Console.Error.WriteLine($"Session {id} introuvable.");
                return;
            }
            using var doc = JsonDocument.Parse(File.ReadAllText(jsonFile));
            var root = doc.RootElement;
            Console.WriteLine($"\nRésumé de la session '{id}':");
            if (root.TryGetProperty("summary", out var sum)) Console.WriteLine(sum.GetString());
            if (root.TryGetProperty("decisions", out var decs))
            {
                Console.WriteLine("\nDécisions:");
                foreach (var d in decs.EnumerateArray()) Console.WriteLine($"  - {d.GetString()}");
            }
            if (root.TryGetProperty("action_items", out var acts))
            {
                Console.WriteLine("\nActions:");
                foreach (var a in acts.EnumerateArray()) Console.WriteLine($"  - {a.GetString()}");
            }
        }, idArg);

        cmd.AddCommand(listCmd);
        cmd.AddCommand(summaryCmd);
        return cmd;
    }
}
