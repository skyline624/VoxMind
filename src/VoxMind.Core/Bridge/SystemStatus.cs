using System.Text.Json.Serialization;

namespace VoxMind.Core.Bridge;

public class SystemStatus
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = "idle";   // idle, listening, paused

    [JsonPropertyName("uptime_seconds")]
    public double UptimeSeconds { get; set; }

    [JsonPropertyName("current_session")]
    public CurrentSessionStatus? CurrentSession { get; set; }

    [JsonPropertyName("compute")]
    public ComputeStatus Compute { get; set; } = new();

    [JsonPropertyName("models")]
    public ModelsStatus Models { get; set; } = new();

    [JsonPropertyName("last_activity")]
    public DateTime? LastActivity { get; set; }

    [JsonPropertyName("voxmind_version")]
    public string VoxMindVersion { get; set; } = "1.0.0";
}

public class CurrentSessionStatus
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("started_at")]
    public DateTime StartedAt { get; set; }

    [JsonPropertyName("elapsed_seconds")]
    public double ElapsedSeconds { get; set; }

    [JsonPropertyName("segments_processed")]
    public int SegmentsProcessed { get; set; }

    [JsonPropertyName("participants")]
    public List<string> Participants { get; set; } = new();
}

public class ComputeStatus
{
    [JsonPropertyName("backend")]
    public string Backend { get; set; } = "CPU";

    [JsonPropertyName("gpu_name")]
    public string? GpuName { get; set; }

    [JsonPropertyName("gpu_memory_used_mb")]
    public long? GpuMemoryUsedMb { get; set; }

    [JsonPropertyName("gpu_memory_total_mb")]
    public long? GpuMemoryTotalMb { get; set; }
}

public class ModelsStatus
{
    [JsonPropertyName("whisper")]
    public WhisperModelStatus Whisper { get; set; } = new();

    [JsonPropertyName("pyannote")]
    public PyAnnoteModelStatus PyAnnote { get; set; } = new();
}

public class WhisperModelStatus
{
    [JsonPropertyName("size")]
    public string Size { get; set; } = string.Empty;

    [JsonPropertyName("loaded")]
    public bool Loaded { get; set; }
}

public class PyAnnoteModelStatus
{
    [JsonPropertyName("loaded")]
    public bool Loaded { get; set; }
}
