using VoxMind.Core.Transcription;

namespace VoxMind.Core.Tts;

/// <summary>Métadonnées sur le moteur TTS chargé.</summary>
public class TtsModelInfo
{
    public string EngineName { get; init; } = string.Empty;
    public ComputeBackend Backend { get; init; }
    public bool IsLoaded { get; set; }

    /// <summary>Codes ISO 639-1 effectivement chargeables (selon les checkpoints disponibles).</summary>
    public IReadOnlyList<string> AvailableLanguages { get; init; } = Array.Empty<string>();

    /// <summary>Codes effectivement préchargés en cache à l'instant T.</summary>
    public IReadOnlyList<string> ResidentLanguages { get; set; } = Array.Empty<string>();
}
