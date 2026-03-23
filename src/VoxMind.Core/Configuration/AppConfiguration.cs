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
}

public class TranscriptionConfig
{
    public string Engine { get; set; } = "parakeet";
    public string ParakeetModelPath { get; set; } = "./models/parakeet-tdt-0.6b-v3-int8";
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
        = "./models/sherpa-onnx-pyannote-segmentation-3-0/model.onnx";
    public string EmbeddingModelPath { get; set; }
        = "./models/3dspeaker_speech_eres2net_base_sv_zh-cn_3dspeaker_16k.onnx";
    public int NumThreads { get; set; } = 4;
    public float ClusteringThreshold { get; set; } = 0.5f;
}

public class DatabaseConfig
{
    public string Path { get; set; } = "/home/pc/voice_data/profiles/database.sqlite";
    public bool BackupEnabled { get; set; } = true;
    public int BackupIntervalHours { get; set; } = 24;
    public string BackupPath { get; set; } = "/home/pc/voice_data/profiles/backups";
}

public class SessionConfig
{
    public string OutputFolder { get; set; } = "/home/pc/voice_data/sessions";
    public int SummaryIntervalMinutes { get; set; } = 5;
    public int MaxSegmentDurationSeconds { get; set; } = 30;
    public bool SaveAudioCache { get; set; } = false;
    public string AudioCacheFormat { get; set; } = "wav";
}

public class BridgeConfig
{
    public string SharedFolder { get; set; } = "/home/pc/voice_data/shared";
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
    public string Path { get; set; } = "/home/pc/voice_data/logs/voxmind_{date}.log";
    public string RollingInterval { get; set; } = "Day";
    public int RetainedFileCount { get; set; } = 30;
}

public class MetricsConfig
{
    public bool Enabled { get; set; } = false;
    public int Port { get; set; } = 9090;
    public string Endpoint { get; set; } = "/metrics";
}

public class RemoteClientsConfig
{
    public bool Enabled { get; set; } = false;
    public int Port { get; set; } = 50052;
    public string SharedToken { get; set; } = "changeme";
    public int HeartbeatTimeoutSeconds { get; set; } = 30;
}
