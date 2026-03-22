namespace VoxMind.Core.Audio;

public class AudioConfiguration
{
    /// <summary>Fréquence d'échantillonnage — optimal pour Whisper : 16000 Hz</summary>
    public int SampleRate { get; set; } = 16000;

    public int BitDepth { get; set; } = 16;

    /// <summary>Mono obligatoire pour Whisper et PyAnnote</summary>
    public int Channels { get; set; } = 1;

    /// <summary>Durée d'un chunk en ms — 100ms donne une latence minimale</summary>
    public int ChunkDurationMs { get; set; } = 100;

    /// <summary>Nombre de samples par chunk : SampleRate * ChunkDurationMs / 1000</summary>
    public int BufferSize => SampleRate * ChunkDurationMs / 1000;

    public List<AudioSourceType> EnabledSources { get; set; } = new() { AudioSourceType.Microphone };

    /// <summary>Si true, mélange toutes les sources en un seul flux</summary>
    public bool MixSources { get; set; } = true;
}
