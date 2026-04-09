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
    public float ConfidenceThreshold { get; set; } = 0.7f;
    public SherpaOnnxConfig SherpaOnnx { get; set; } = new();
}

public class SherpaOnnxConfig
{
    public string SegmentationModelPath { get; set; }
        = AppConfiguration.GetModelPath("sherpa-onnx-pyannote-segmentation-3-0", "model.onnx");
    public string EmbeddingModelPath { get; set; }
        = AppConfiguration.GetModelPath("3dspeaker_speech_eres2net_base_sv_zh-cn_3dspeaker_16k.onnx");
    public int NumThreads { get; set; } = 4;
    public float ClusteringThreshold { get; set; } = 0.5f;
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
