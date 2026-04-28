using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using VoxMind.F5Tts;

namespace VoxMind.Core.Tts;

/// <summary>
/// Moteur F5-TTS chargé pour une langue donnée — encapsule les 3 sessions ONNX
/// + le tokenizer. Disposable car détient des <see cref="InferenceSession"/>.
/// </summary>
public sealed class F5LanguageEngine : IDisposable
{
    public string Language { get; }
    public F5TtsTokenizer Tokenizer { get; }
    public F5TtsPreprocessor Preprocessor { get; }
    public F5TtsTransformer Transformer { get; }
    public F5TtsDecoder Decoder { get; }
    public string DefaultReferenceWav { get; }
    public string DefaultReferenceText { get; }

    private bool _disposed;

    public F5LanguageEngine(F5LanguageCheckpoint checkpoint, ILogger? logger = null)
    {
        Language = checkpoint.Language;

        var opts = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };

        Tokenizer = new F5TtsTokenizer(checkpoint.TokensPath);
        Preprocessor = new F5TtsPreprocessor(checkpoint.PreprocessModelPath, opts);
        Transformer = new F5TtsTransformer(checkpoint.TransformerModelPath, opts);
        Decoder = new F5TtsDecoder(checkpoint.DecodeModelPath, opts);
        DefaultReferenceWav = checkpoint.DefaultReferenceWav;
        DefaultReferenceText = ResolveReferenceText(checkpoint, logger);

        logger?.LogInformation(
            "F5-TTS chargé pour {Lang} : preprocess={Prep}, transformer={Tx}, decode={Dec}.",
            Language, checkpoint.PreprocessModelPath, checkpoint.TransformerModelPath, checkpoint.DecodeModelPath);
    }

    /// <summary>
    /// Résout le texte associé à <c>reference.wav</c> :
    ///   1. Sidecar <c>reference.txt</c> à côté du WAV (priorité — permet d'updater la voix
    ///      de référence sans toucher à <c>appsettings.json</c>).
    ///   2. <see cref="F5LanguageCheckpoint.DefaultReferenceText"/> de la config.
    /// </summary>
    private static string ResolveReferenceText(F5LanguageCheckpoint checkpoint, ILogger? logger)
    {
        var dir = Path.GetDirectoryName(checkpoint.DefaultReferenceWav);
        if (!string.IsNullOrEmpty(dir))
        {
            var sidecar = Path.Combine(dir, "reference.txt");
            if (File.Exists(sidecar))
            {
                var text = File.ReadAllText(sidecar).Trim();
                if (!string.IsNullOrEmpty(text))
                {
                    logger?.LogInformation(
                        "F5-TTS [{Lang}] : reference.txt sidecar utilisé ({Chars} char).",
                        checkpoint.Language, text.Length);
                    return text;
                }
            }
        }
        return checkpoint.DefaultReferenceText;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Preprocessor.Dispose();
        Transformer.Dispose();
        Decoder.Dispose();
    }
}
