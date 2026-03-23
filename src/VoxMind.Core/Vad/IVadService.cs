namespace VoxMind.Core.Vad;

/// <summary>
/// Segment de parole détecté par le VAD.
/// </summary>
public record VadSegment(float StartSeconds, float EndSeconds, float[] Samples);

/// <summary>
/// Service de détection d'activité vocale (Voice Activity Detection).
/// </summary>
public interface IVadService
{
    bool IsAvailable { get; }

    /// <summary>
    /// Détecte les segments de parole dans le tableau de samples PCM float32 16kHz.
    /// Retourne une liste de segments avec timestamps en secondes.
    /// </summary>
    IReadOnlyList<VadSegment> DetectSpeech(float[] audioSamples, int sampleRate = 16000);
}

/// <summary>
/// Implémentation nulle — utilisée quand VAD est désactivé par configuration.
/// </summary>
public sealed class DisabledVadService : IVadService
{
    public bool IsAvailable => false;
    public IReadOnlyList<VadSegment> DetectSpeech(float[] audioSamples, int sampleRate = 16000)
        => Array.Empty<VadSegment>();
}
