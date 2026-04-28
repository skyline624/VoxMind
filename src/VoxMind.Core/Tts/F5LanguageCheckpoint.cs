namespace VoxMind.Core.Tts;

/// <summary>
/// Chemins ONNX d'un fine-tune F5-TTS pour une langue donnée.
///
/// Référence d'export : <see href="https://github.com/DakeQQ/F5-TTS-ONNX"/>.
/// Le pipeline produit toujours trois fichiers ONNX par checkpoint, plus
/// un <c>tokens.txt</c> sentencepiece BPE et une voix de référence par défaut.
/// </summary>
public sealed class F5LanguageCheckpoint
{
    /// <summary>Code ISO 639-1 (<c>"fr"</c>, <c>"en"</c>, …).</summary>
    public string Language { get; set; } = string.Empty;

    /// <summary>Préprocesseur (<c>F5_Preprocess.onnx</c>) — texte+audio ref → embeddings d'entrée.</summary>
    public string PreprocessModelPath { get; set; } = string.Empty;

    /// <summary>Transformeur DiT (<c>F5_Transformer.onnx</c>) — boucle de flow-matching.</summary>
    public string TransformerModelPath { get; set; } = string.Empty;

    /// <summary>Décodeur (<c>F5_Decode.onnx</c>) — vocoder Vocos 24 kHz : mel → PCM.</summary>
    public string DecodeModelPath { get; set; } = string.Empty;

    /// <summary>Vocabulaire phonémique BPE (<c>tokens.txt</c>).</summary>
    public string TokensPath { get; set; } = string.Empty;

    /// <summary>Audio de référence par défaut pour le cloning (PCM 24 kHz mono, &lt; 30 s).</summary>
    public string DefaultReferenceWav { get; set; } = string.Empty;

    /// <summary>Transcription textuelle exacte de <see cref="DefaultReferenceWav"/>.</summary>
    public string DefaultReferenceText { get; set; } = string.Empty;
}
