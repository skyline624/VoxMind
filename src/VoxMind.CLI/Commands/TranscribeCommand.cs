using System.CommandLine;
using System.CommandLine.Invocation;
using Microsoft.Extensions.DependencyInjection;
using VoxMind.CLI.Interactive;
using VoxMind.Core.Transcription;

namespace VoxMind.CLI.Commands;

public static class TranscribeCommand
{
    public static Command Build(IServiceProvider services)
    {
        var fileArg = new Argument<FileInfo>("file", "Fichier audio à transcrire");
        var outputOpt = new Option<string?>("--output", "Fichier de sortie (stdout si absent)");

        var cmd = new Command("transcribe", "Transcrire un fichier audio") { fileArg, outputOpt };

        cmd.SetHandler(async (InvocationContext ctx) =>
        {
            var file = ctx.ParseResult.GetValueForArgument(fileArg);
            var output = ctx.ParseResult.GetValueForOption(outputOpt);
            var ct = ctx.GetCancellationToken();

            if (!file.Exists)
            {
                ColorConsole.WriteError($"Fichier introuvable : {file.FullName}");
                Environment.Exit(1);
                return;
            }

            var service = services.GetRequiredService<ITranscriptionService>();
            ColorConsole.WriteInfo($"Transcription de '{file.Name}'...");

            try
            {
                var result = await service.TranscribeFileAsync(file.FullName, ct);

                if (output is not null)
                {
                    await File.WriteAllTextAsync(output, result.Text, ct);
                    ColorConsole.WriteSuccess($"Transcription sauvegardée dans '{output}'");
                }
                else
                {
                    Console.WriteLine(result.Text);
                }

                ColorConsole.WriteInfo($"Langue: {result.Language} | Confiance: {result.Confidence:P0} | Durée: {result.Duration:hh\\:mm\\:ss}");
            }
            catch (Exception ex)
            {
                ColorConsole.WriteError($"Erreur de transcription: {ex.Message}");
                Environment.Exit((int)ExitCode.TranscriptionError);
            }
        });

        return cmd;
    }
}
