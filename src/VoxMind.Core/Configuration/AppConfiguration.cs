using VoxMind.Core.Tts;

namespace VoxMind.Core.Configuration;

public class AppConfiguration
{
    public ApplicationConfig Application { get; set; } = new();
    public AudioConfig Audio { get; set; } = new();
    public MlConfig Ml { get; set; } = new();
    public DatabaseConfig Database { get; set; } = new();
    public SessionConfig Session { get; set; } = new();
    public BridgeConfig Bridge { get; set; } = new();
    public LoggingConfig Logging { get; set; } = new();
    public MetricsConfig Metrics { get; set; } = new();
    public RemoteClientsConfig RemoteClients { get; set; } = new();
    public ApiConfig Api { get; set; } = new();

    /// <summary>
    /// Gets the base data directory for VoxMind.
    /// Priority: VOXMIND_DATA_DIR env > walk up from executable > walk up from cwd > ./voice_data
    /// </summary>
    public static string GetDataDirectory()
    {
        // 1. VOXMIND_DATA_DIR env variable
        var env = System.Environment.GetEnvironmentVariable("VOXMIND_DATA_DIR");
        if (!string.IsNullOrEmpty(env) && System.IO.Directory.Exists(env))
            return env;

        // 2. Walk up from the executable
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            var candidate = System.IO.Path.Combine(dir.FullName, "voice_data");
            if (System.IO.Directory.Exists(candidate))
                return candidate;
        }

        // 3. Walk up from current working directory
        dir = new System.IO.DirectoryInfo(System.IO.Directory.GetCurrentDirectory());
        for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            var candidate = System.IO.Path.Combine(dir.FullName, "voice_data");
            if (System.IO.Directory.Exists(candidate))
                return candidate;
        }

        // 4. Default: ./voice_data relative to current directory (will be created)
        return System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "voice_data");
    }

    /// <summary>
    /// Helper to build a path relative to the data directory.
    /// </summary>
    public static string GetDefaultPath(params string[] parts)
        => System.IO.Path.Combine(GetDataDirectory(), System.IO.Path.Combine(parts));

    /// <summary>
    /// Finds the models directory by walking up from the executable until a models/ folder is found.
    /// Priority: VOXMIND_MODELS_DIR env > walk up from AppContext.BaseDirectory > walk up from cwd > ./models
    /// </summary>
    public static string GetModelsDirectory()
    {
        var env = System.Environment.GetEnvironmentVariable("VOXMIND_MODELS_DIR");
        if (!string.IsNullOrEmpty(env) && System.IO.Directory.Exists(env))
            return env;

        // Walk up from the executable (handles dotnet run, published builds, etc.)
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            var candidate = System.IO.Path.Combine(dir.FullName, "models");
            if (System.IO.Directory.Exists(candidate))
                return candidate;
        }

        // Walk up from current working directory
        dir = new System.IO.DirectoryInfo(System.IO.Directory.GetCurrentDirectory());
        for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            var candidate = System.IO.Path.Combine(dir.FullName, "models");
            if (System.IO.Directory.Exists(candidate))
                return candidate;
        }

        // Default: ./models relative to current directory (will be created/downloaded)
        return System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "models");
    }

    /// <summary>
    /// Helper to build a path relative to the models directory.
    /// </summary>
    public static string GetModelPath(params string[] parts)
        => System.IO.Path.Combine(GetModelsDirectory(), System.IO.Path.Combine(parts));
}

public class ApplicationConfig
{
    public string Name { get; set; } = "VoxMind";
    public string Version { get; set; } = "1.0.0";
    public string Environment { get; set; } = "development";
}

public class AudioConfig
{
    public int DefaultSampleRate { get; set; } = 16000;
    public int DefaultChunkDurationMs { get; set; } = 100;
    public Dictionary<string, AudioSourceConfig> Sources { get; set; } = new();
    public int MaxSilentDurationMs { get; set; } = 30000;
}

public class AudioSourceConfig
{
    public bool Enabled { get; set; } = true;
    public int DeviceIndex { get; set; } = -1;
    public string Name { get; set; } = "default";
}

public class MlConfig
{
    public TranscriptionConfig Transcription { get; set; } = new();
    public SpeakerRecognitionConfig SpeakerRecognition { get; set; } = new();
    public VadConfig Vad { get; set; } = new();
    public TtsConfig Tts { get; set; } = new();
}

public class TtsConfig
{
    /// <summary>Active le moteur TTS dans la composition. Désactivé → 503 sur /v1/audio/speech.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Identifiant du moteur par défaut dans la registry (<c>"f5"</c>).</summary>
    public string DefaultEngine { get; set; } = "f5";

    /// <summary>Code ISO 639-1 utilisé si la requête ne précise pas la langue.</summary>
    public string DefaultLanguage { get; set; } = "fr";

    /// <summary>Nombre de moteurs F5 chargés simultanément en RAM (FR + EN typiquement).</summary>
    public int CacheCapacity { get; set; } = 2;

    /// <summary>Nombre d'étapes Euler du flow-matching (32 par défaut, baisser = plus rapide).</summary>
    public int FlowMatchingSteps { get; set; } = 32;

    /// <summary>Checkpoints F5-TTS par code ISO 639-1.</summary>
    public Dictionary<string, F5LanguageCheckpoint> Languages { get; set; } = new()
    {
        ["fr"] = new F5LanguageCheckpoint
        {
            Language = "fr",
            PreprocessModelPath = AppConfiguration.GetModelPath("f5-tts", "fr", "F5_Preprocess.onnx"),
            TransformerModelPath = AppConfiguration.GetModelPath("f5-tts", "fr", "F5_Transformer.onnx"),
            DecodeModelPath = AppConfiguration.GetModelPath("f5-tts", "fr", "F5_Decode.onnx"),
            TokensPath = AppConfiguration.GetModelPath("f5-tts", "fr", "tokens.txt"),
            DefaultReferenceWav = AppConfiguration.GetModelPath("f5-tts", "fr", "reference.wav"),
            DefaultReferenceText = "Bonjour, je suis prête à vous parler.",
        },
        ["en"] = new F5LanguageCheckpoint
        {
            Language = "en",
            PreprocessModelPath = AppConfiguration.GetModelPath("f5-tts", "en", "F5_Preprocess.onnx"),
            TransformerModelPath = AppConfiguration.GetModelPath("f5-tts", "en", "F5_Transformer.onnx"),
            DecodeModelPath = AppConfiguration.GetModelPath("f5-tts", "en", "F5_Decode.onnx"),
            TokensPath = AppConfiguration.GetModelPath("f5-tts", "en", "tokens.txt"),
            DefaultReferenceWav = AppConfiguration.GetModelPath("f5-tts", "en", "reference.wav"),
            DefaultReferenceText = "Hello, I am ready to talk with you.",
        },
    };

    /// <summary>
    /// Configuration du moteur Kokoro (sherpa-onnx, non-autorégressif, voix prédéfinies).
    /// Modèle multilingue <c>kokoro-multi-lang-v1_0</c> : voix FR féminine <c>ff_siwis</c> (sid 30).
    /// </summary>
    public KokoroConfig Kokoro { get; set; } = new();
}

/// <summary>
/// Chemins et paramètres du moteur Kokoro via sherpa-onnx <see cref="VoxMind.Core.Tts.KokoroTtsService"/>.
/// Le modèle est multilingue ; la phonémisation passe par espeak-ng (<see cref="DataDir"/>).
/// </summary>
public class KokoroConfig
{
    /// <summary>
    /// Modèle ONNX Kokoro. On utilise la variante <b>fp32</b> (<c>model.onnx</c>) : sur CPU, les
    /// kernels GEMM fp32 optimisés (MLAS) sont nettement plus rapides que le chemin int8/MatMulInteger
    /// (mesuré RTF ~0.3 fp32 contre ~1.5 int8 sur ce parc), pour une qualité supérieure.
    /// </summary>
    public string ModelPath { get; set; } = AppConfiguration.GetModelPath("kokoro", "model.onnx");

    /// <summary>Embeddings de style des voix (<c>voices.bin</c>) — une voix par speaker id.</summary>
    public string VoicesPath { get; set; } = AppConfiguration.GetModelPath("kokoro", "voices.bin");

    /// <summary>Table tokens → ids (<c>tokens.txt</c>).</summary>
    public string TokensPath { get; set; } = AppConfiguration.GetModelPath("kokoro", "tokens.txt");

    /// <summary>Données espeak-ng pour la phonémisation (obligatoire).</summary>
    public string DataDir { get; set; } = AppConfiguration.GetModelPath("kokoro", "espeak-ng-data");

    /// <summary>Dossier dict jieba (segmentation chinoise). Inutile pour le FR → vide.</summary>
    public string DictDir { get; set; } = string.Empty;

    /// <summary>
    /// Lexiques de prononciation (chemins séparés par des virgules). Laissé VIDE pour le FR :
    /// tous les mots passent alors par espeak-ng en français (prononciation cohérente).
    /// </summary>
    public string Lexicon { get; set; } = string.Empty;

    /// <summary>Threads d'inférence ONNX (8 = bon compromis latence/charge ; au-delà, gains marginaux).</summary>
    public int NumThreads { get; set; } = 8;

    /// <summary>Provider d'exécution sherpa-onnx (<c>"cpu"</c>).</summary>
    public string Provider { get; set; } = "cpu";

    /// <summary>Facteur de longueur global (1.0 = vitesse nominale).</summary>
    public float LengthScale { get; set; } = 1.0f;

    /// <summary>Langue (code ISO 639-1) utilisée si la requête ne précise rien.</summary>
    public string DefaultLanguage { get; set; } = "fr";

    /// <summary>
    /// Voix Kokoro par code ISO 639-1. Par défaut le français féminin <c>ff_siwis</c> (speaker id 30
    /// du modèle <c>kokoro-multi-lang-v1_0</c>), phonémisé par espeak-ng en français (<c>"fr"</c>).
    /// </summary>
    public Dictionary<string, KokoroVoice> Voices { get; set; } = new()
    {
        ["fr"] = new KokoroVoice { Language = "fr", SpeakerId = 30, EspeakVoice = "fr", Speed = 1.0f },
    };
}

/// <summary>Voix Kokoro : association langue → (speaker id du modèle, voix espeak-ng, vitesse).</summary>
public class KokoroVoice
{
    /// <summary>Code ISO 639-1 (<c>"fr"</c>).</summary>
    public string Language { get; set; } = "fr";

    /// <summary>Speaker id dans <c>voices.bin</c> (FR féminin <c>ff_siwis</c> = 30).</summary>
    public int SpeakerId { get; set; } = 30;

    /// <summary>Code voix espeak-ng pour la phonémisation (<c>"fr"</c>, <c>"en-us"</c>, …).</summary>
    public string EspeakVoice { get; set; } = "fr";

    /// <summary>Vitesse de parole (1.0 = nominale ; &gt;1 plus rapide).</summary>
    public float Speed { get; set; } = 1.0f;
}

public class TranscriptionConfig
{
    public string Engine { get; set; } = "parakeet";
    public string ParakeetModelPath { get; set; } = AppConfiguration.GetModelPath("parakeet-tdt-0.6b-v3-int8");
    public string DefaultModel { get; set; } = "parakeet";
}

public class SpeakerRecognitionConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Seuil de similarité cosinus au-dessus duquel un profil est considéré identifié.
    /// 0.55 (défaut permissif validé en usage réel) : 0.65–0.7 fait rater le même
    /// locuteur selon le micro / la fatigue / la pièce, créant des profils en double.
    /// </summary>
    public float ConfidenceThreshold { get; set; } = 0.55f;

    /// <summary>
    /// Borne haute de la bande d'enrichissement passif : un match dont la similarité est
    /// dans [ConfidenceThreshold ; cette borne[ enrichit le profil (couverture acoustique
    /// élargie). Au-dessus, le match est « assez bon » → pas d'enrichissement.
    /// </summary>
    public float EnrichmentCosineUpperBound { get; set; } = 0.80f;

    /// <summary>
    /// Garde anti-doublon : un embedding dont la similarité avec un vecteur déjà stocké du
    /// profil dépasse ce seuil n'est pas réajouté (évite de gonfler le profil de quasi-
    /// doublons issus de la même condition de capture).
    /// </summary>
    public float DuplicateRejectionThreshold { get; set; } = 0.95f;

    /// <summary>Durée audio minimale (s) requise pour enrichir passivement un profil.</summary>
    public double MinEnrichmentDurationSeconds { get; set; } = 2.0;

    public SherpaOnnxConfig SherpaOnnx { get; set; } = new();
}

public class SherpaOnnxConfig
{
    public string SegmentationModelPath { get; set; }
        = AppConfiguration.GetModelPath("sherpa-onnx-pyannote-segmentation-3-0", "model.onnx");
    public string EmbeddingModelPath { get; set; }
        = AppConfiguration.GetModelPath("3dspeaker_speech_eres2net_sv_zh-cn_16k-common.onnx");
    public int NumThreads { get; set; } = 4;
    public float ClusteringThreshold { get; set; } = 0.5f;

    /// <summary>Pyannote diarizer: minimum speech duration to keep a segment "on" (seconds).</summary>
    public float MinDurationOn { get; set; } = 0.3f;

    /// <summary>Pyannote diarizer: minimum silence duration to consider a segment ended (seconds).</summary>
    public float MinDurationOff { get; set; } = 0.5f;
}

public class DatabaseConfig
{
    public string Path { get; set; } = AppConfiguration.GetDefaultPath("profiles", "database.sqlite");
    public bool BackupEnabled { get; set; } = true;
    public int BackupIntervalHours { get; set; } = 24;
    public string BackupPath { get; set; } = AppConfiguration.GetDefaultPath("profiles", "backups");
}

public class SessionConfig
{
    public string OutputFolder { get; set; } = AppConfiguration.GetDefaultPath("sessions");
    public int SummaryIntervalMinutes { get; set; } = 5;
    public int MaxSegmentDurationSeconds { get; set; } = 30;
    public bool SaveAudioCache { get; set; } = false;
    public string AudioCacheFormat { get; set; } = "wav";
}

public class BridgeConfig
{
    public string SharedFolder { get; set; } = AppConfiguration.GetDefaultPath("shared");
    public int PollIntervalMs { get; set; } = 500;
    public int CommandTimeoutSeconds { get; set; } = 30;
    public int StatusUpdateIntervalSeconds { get; set; } = 5;
}

public class LoggingConfig
{
    public string Level { get; set; } = "Information";
    public ConsoleLoggingConfig Console { get; set; } = new();
    public FileLoggingConfig File { get; set; } = new();
}

public class ConsoleLoggingConfig
{
    public bool Enabled { get; set; } = true;
    public string Format { get; set; } = "colored";
}

public class FileLoggingConfig
{
    public bool Enabled { get; set; } = true;
    public string Path { get; set; } = AppConfiguration.GetDefaultPath("logs", "voxmind_{date}.log");
    public string RollingInterval { get; set; } = "Day";
    public int RetainedFileCount { get; set; } = 30;
}

public class MetricsConfig
{
    public bool Enabled { get; set; } = false;
    public int Port { get; set; } = 9090;
    public string Endpoint { get; set; } = "/metrics";
}

public class VadConfig
{
    public bool Enabled { get; set; } = true;
    public string ModelPath { get; set; } = AppConfiguration.GetModelPath("silero_vad.onnx");
    public float Threshold { get; set; } = 0.5f;
    public float MinSilenceDurationSeconds { get; set; } = 0.5f;
    public float MinSpeechDurationSeconds { get; set; } = 0.25f;
    public float MaxSegmentDurationSeconds { get; set; } = 10.0f;
}

public class ApiConfig
{
    public int Port { get; set; } = 8000;
    public bool EnableSwagger { get; set; } = true;

    /// <summary>
    /// API key required in the X-Api-Key header. If null/empty, authentication is disabled
    /// (a warning is logged at startup). Override via voice_data/config/config.json.
    /// </summary>
    public string? ApiKey { get; set; }
}

public class RemoteClientsConfig
{
    public bool Enabled { get; set; } = false;
    public int Port { get; set; } = 50052;
    public string SharedToken { get; set; } = "changeme";
    public int HeartbeatTimeoutSeconds { get; set; } = 30;
}
