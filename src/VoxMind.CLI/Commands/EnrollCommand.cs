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
            var audio = services.GetRequiredService<IAudioCapture>();

            if (!await speakerService.CheckHealthAsync())
            {
                ColorConsole.WriteError("Le service de reconnaissance vocale n'est pas disponible (modèle sherpa-onnx manquant).");
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

            var embedding = await speakerService.ExtractEmbeddingAsync(combinedAudio, ct);
            if (embedding is null)
            {
                ColorConsole.WriteError("Extraction d'embedding échouée.");
                return;
            }

            var durationSeconds = (float)combinedAudio.Length / (16000 * 2);
            var profile = await speakerService.EnrollSpeakerAsync(name, embedding, 1.0f, (int)durationSeconds);
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
