namespace VoxMind.Core.Tts;

/// <summary>
/// Chemins ONNX d'un checkpoint Chatterbox multilingue, déclinés par langue.
///
/// Référence d'export : <c>onnx-community/chatterbox-multilingual-ONNX</c>.
/// Le modèle est multilingue : les quatre ONNX (<c>speech_encoder</c>, <c>embed_tokens</c>,
/// <c>language_model</c>, <c>conditional_decoder</c>) et le <c>tokenizer.json</c> sont
/// partagés entre toutes les langues ; seules la voix de référence et sa transcription
/// changent d'une entrée à l'autre. Le pipeline ne charge donc qu'une fois le jeu de
/// modèles (cache par dossier).
/// </summary>
public sealed class ChatterboxLanguageCheckpoint
{
    /// <summary>Code ISO 639-1 (<c>"fr"</c>, <c>"en"</c>, …) — passé au tokenizer en préfixe <c>[lang]</c>.</summary>
    public string Language { get; set; } = string.Empty;

    /// <summary>Encodeur de locuteur (<c>speech_encoder.onnx</c>) — audio ref → features + tokens + embeddings.</summary>
    public string SpeechEncoderModelPath { get; set; } = string.Empty;

    /// <summary>Embeddings d'entrée (<c>embed_tokens.onnx</c>) — input_ids + position_ids + exaggeration.</summary>
    public string EmbedTokensModelPath { get; set; } = string.Empty;

    /// <summary>LLM autorégressif (<c>language_model_{variant}.onnx</c>) — boucle KV-cache greedy.</summary>
    public string LanguageModelPath { get; set; } = string.Empty;

    /// <summary>Décodeur (<c>conditional_decoder.onnx</c>) — speech_tokens → waveform 24 kHz.</summary>
    public string ConditionalDecoderModelPath { get; set; } = string.Empty;

    /// <summary>Tokenizer BPE HuggingFace (<c>tokenizer.json</c>).</summary>
    public string TokenizerPath { get; set; } = string.Empty;

    /// <summary>Audio de référence par défaut pour le cloning (PCM mono, &lt; 30 s — rééchantillonné en 24 kHz).</summary>
    public string DefaultReferenceWav { get; set; } = string.Empty;

    /// <summary>Transcription textuelle de <see cref="DefaultReferenceWav"/> (informative, non requise par le pipeline).</summary>
    public string DefaultReferenceText { get; set; } = string.Empty;

    /// <summary>
    /// Variante de quantification du language_model. <c>"q4"</c> par défaut → <c>language_model_q4.onnx</c>
    /// (4 bits, requiert ORT ≥ 1.22). <c>"fp32"</c> → <c>language_model.onnx</c>.
    /// </summary>
    public string LmVariant { get; set; } = "q4";

    /// <summary>Niveau d'exagération prosodique passé au pipeline (0.5 par défaut).</summary>
    public float Exaggeration { get; set; } = 0.5f;

    /// <summary>
    /// Backend d'exécution ONNX : <c>"cpu"</c> (défaut) ou <c>"cuda"</c>. Sur <c>"cuda"</c>, le pipeline
    /// ajoute le <c>CUDAExecutionProvider</c> aux sessions (conteneur GPU dédié, package
    /// <c>Microsoft.ML.OnnxRuntime.Gpu</c> + runtime CUDA/cuDNN requis).
    /// </summary>
    public string Device { get; set; } = "cpu";

    /// <summary>
    /// Mode de décodage du language_model. <c>false</c> (défaut) = greedy/argmax (stable sur CPU).
    /// <c>true</c> = sampling (repetition penalty → softmax/temp → top-k) — <b>obligatoire sur GPU</b>,
    /// où le greedy déraille (babillage de 1001 tokens sans STOP).
    /// </summary>
    public bool UseSampling { get; set; } = false;
}
