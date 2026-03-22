using System.CommandLine;
using System.CommandLine.Invocation;
using Microsoft.Extensions.DependencyInjection;
using VoxMind.CLI.Interactive;
using VoxMind.Core.Audio;
using VoxMind.Core.SpeakerRecognition;

namespace VoxMind.CLI.Commands;

public static class EnrollCommand
{
    public static Command Build(IServiceProvider services)
    {
        var nameArg = new Argument<string>("name", "Nom du locuteur à enregistrer");
        var cmd = new Command("enroll", "Enregistrer une nouvelle empreinte vocale") { nameArg };

        cmd.SetHandler(async (InvocationContext ctx) =>
        {
            var name = ctx.ParseResult.GetValueForArgument(nameArg);
            var ct = ctx.GetCancellationToken();

            var speakerService = services.GetRequiredService<ISpeakerIdentificationService>();
            var pyAnnote = services.GetRequiredService<IPyAnnoteClient>();
            var audio = services.GetRequiredService<IAudioCapture>();

            if (!await pyAnnote.PingAsync(ct))
            {
                ColorConsole.WriteError("Le service PyAnnote n'est pas disponible. Démarrer pyannote_server.py d'abord.");
                Environment.Exit((int)ExitCode.PyAnnoteError);
                return;
            }

            ColorConsole.WriteInfo($"Enregistrement de la voix de '{name}'...");
            ColorConsole.WriteInfo("Parlez pendant 10 secondes après le signal...");
            await Task.Delay(1000, ct);
            Console.WriteLine("GO!");

            var chunks = new List<byte[]>();
            var config = new AudioConfiguration { ChunkDurationMs = 100 };
            var captureComplete = new TaskCompletionSource();

            audio.AudioChunkReceived += (_, e) => chunks.Add(e.Chunk.RawData);
            await audio.StartCaptureAsync(config, ct);

            await Task.WhenAny(Task.Delay(10000, ct), captureComplete.Task);
            await audio.StopCaptureAsync();
            audio.AudioChunkReceived -= (_, _) => { };

            if (!chunks.Any())
            {
                ColorConsole.WriteError("Aucun audio capturé.");
                return;
            }

            var combinedAudio = CombineChunks(chunks);
            ColorConsole.WriteInfo($"Audio capturé: {combinedAudio.Length / (16000 * 2)} secondes. Extraction de l'embedding...");

            var embResult = await pyAnnote.ExtractEmbeddingAsync(combinedAudio, ct);
            if (!embResult.Success)
            {
                ColorConsole.WriteError($"Extraction d'embedding échouée: {embResult.Error}");
                return;
            }

            var profile = await speakerService.EnrollSpeakerAsync(name, embResult.Embedding, embResult.DurationUsed);
            ColorConsole.WriteSuccess($"Locuteur '{name}' enregistré avec succès (ID: {profile.Id})");
        });

        return cmd;
    }

    private static byte[] CombineChunks(List<byte[]> chunks)
    {
        var totalLength = chunks.Sum(c => c.Length);
        var result = new byte[totalLength];
        int offset = 0;
        foreach (var chunk in chunks)
        {
            Buffer.BlockCopy(chunk, 0, result, offset, chunk.Length);
            offset += chunk.Length;
        }
        return result;
    }
}
