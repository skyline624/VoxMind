using System.CommandLine;
using System.CommandLine.Invocation;
using Microsoft.Extensions.DependencyInjection;
using VoxMind.CLI.Interactive;
using VoxMind.Core.Tts;

namespace VoxMind.CLI.Commands;

/// <summary>
/// CLI : <c>voxmind speak "Bonjour" --language fr --output /tmp/out.wav</c>.
/// </summary>
public static class SpeakCommand
{
    public static Command Build(IServiceProvider services)
    {
        var textArg = new Argument<string>("text", "Texte à synthétiser");
        var languageOpt = new Option<string?>(
            new[] { "--language", "-l" },
            "Code ISO 639-1 (fr, en, …). Auto-détecté depuis le texte si absent.");
        var voiceOpt = new Option<FileInfo?>(
            "--voice",
            "Audio de référence (WAV) pour le voice cloning. Texte de référence requis avec --voice-text.");
        var voiceTextOpt = new Option<string?>(
            "--voice-text",
            "Transcription textuelle de l'audio fourni à --voice.");
        var outputOpt = new Option<string?>(
            new[] { "--output", "-o" },
            () => "speech.wav",
            "Fichier WAV de sortie (défaut: speech.wav)");

        var cmd = new Command("speak", "Synthétiser un texte en audio (TTS F5-TTS)")
        {
            textArg, languageOpt, voiceOpt, voiceTextOpt, outputOpt,
        };

        cmd.SetHandler(async (InvocationContext ctx) =>
        {
            var text = ctx.ParseResult.GetValueForArgument(textArg);
            var language = ctx.ParseResult.GetValueForOption(languageOpt);
            var voice = ctx.ParseResult.GetValueForOption(voiceOpt);
            var voiceText = ctx.ParseResult.GetValueForOption(voiceTextOpt);
            var output = ctx.ParseResult.GetValueForOption(outputOpt) ?? "speech.wav";
            var ct = ctx.GetCancellationToken();

            var tts = services.GetRequiredService<ITtsService>();
            if (!tts.Info.IsLoaded)
            {
                ColorConsole.WriteError(
                    $"Moteur TTS '{tts.Info.EngineName}' non chargé : aucun checkpoint trouvé.");
                ColorConsole.WriteInfo("Voir docs/F5TtsExport.md pour la procédure d'export des modèles.");
                Environment.Exit((int)ExitCode.TtsError);
                return;
            }

            byte[]? voiceBytes = null;
            if (voice is not null)
            {
                if (!voice.Exists)
                {
                    ColorConsole.WriteError($"Voice file introuvable : {voice.FullName}");
                    Environment.Exit((int)ExitCode.InvalidArguments);
                    return;
                }
                if (string.IsNullOrWhiteSpace(voiceText))
                {
                    ColorConsole.WriteError("--voice exige aussi --voice-text (transcription du WAV).");
                    Environment.Exit((int)ExitCode.InvalidArguments);
                    return;
                }
                voiceBytes = await File.ReadAllBytesAsync(voice.FullName, ct);
            }

            ColorConsole.WriteInfo($"Synthèse '{text[..Math.Min(text.Length, 60)]}...' (langue: {language ?? "auto"})");

            try
            {
                var result = await tts.SynthesizeAsync(text, language, voiceBytes, voiceText, ct);
                await File.WriteAllBytesAsync(output, result.ToWavBytes(), ct);

                ColorConsole.WriteSuccess($"WAV écrit : {output} ({result.Duration:s\\.f}s, {result.SynthesisLatency.TotalMilliseconds:F0} ms latence).");
                ColorConsole.WriteInfo($"Langue effective : {result.Language} | sample rate : {result.SampleRate} Hz");
            }
            catch (Exception ex)
            {
                ColorConsole.WriteError($"Erreur de synthèse : {ex.Message}");
                Environment.Exit((int)ExitCode.TtsError);
            }
        });

        return cmd;
    }
}
