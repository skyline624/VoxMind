namespace VoxMind.Core.Tts;

/// <summary>
/// Résultat d'une synthèse vocale F5-TTS.
/// </summary>
public sealed class TtsResult
{
    /// <summary>Samples PCM mono 24 kHz, normalisés [-1, 1].</summary>
    public required float[] Pcm { get; init; }

    /// <summary>Fréquence d'échantillonnage du PCM (24000 pour F5-TTS).</summary>
    public required int SampleRate { get; init; }

    /// <summary>Durée audio totale.</summary>
    public TimeSpan Duration => TimeSpan.FromSeconds((double)Pcm.Length / SampleRate);

    /// <summary>Code ISO 639-1 de la langue effectivement utilisée pour la synthèse.</summary>
    public required string Language { get; init; }

    /// <summary>Latence observée (mesurée côté service, hors I/O HTTP).</summary>
    public TimeSpan SynthesisLatency { get; init; }

    /// <summary>Encode le PCM en WAV PCM16 mono prêt à servir via HTTP.</summary>
    public byte[] ToWavBytes() => Audio.WavWriter.ToBytes(Pcm, SampleRate, channels: 1);
}
