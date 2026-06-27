namespace VoxMind.Core.Audio;

/// <summary>
/// Lecteur WAV PCM minimal (mono, float [-1, 1]) — complément de <see cref="WavWriter"/>.
///
/// Porté depuis le POC Chatterbox (<c>WavIo.LoadMono</c>) : décode un buffer RIFF/WAVE
/// PCM 16 bits ou float 32 bits, mixe les canaux si stéréo, et rééchantillonne
/// (nearest-neighbor) vers la fréquence cible si nécessaire. Utilisé côté TTS pour
/// préparer l'audio de référence du voice cloning (Chatterbox / speech_encoder attend
/// du float mono 24 kHz).
/// </summary>
public static class WavReader
{
    /// <summary>Décode un buffer WAV en float mono [-1, 1] à sa fréquence native (sans rééchantillonnage).</summary>
    public static (float[] Samples, int SampleRate) DecodeMono(ReadOnlySpan<byte> wav)
    {
        if (wav.Length < 12)
            throw new InvalidDataException("WAV : en-tête trop court.");

        int pos = 12; // saute "RIFF"<size>"WAVE"
        int channels = 1, sampleRate = 24000, bits = 16, dataOff = -1, dataLen = 0;
        while (pos + 8 <= wav.Length)
        {
            string id = System.Text.Encoding.ASCII.GetString(wav.Slice(pos, 4));
            int size = BitConverter.ToInt32(wav.Slice(pos + 4, 4));
            int body = pos + 8;
            if (id == "fmt ")
            {
                channels = BitConverter.ToInt16(wav.Slice(body + 2, 2));
                sampleRate = BitConverter.ToInt32(wav.Slice(body + 4, 4));
                bits = BitConverter.ToInt16(wav.Slice(body + 14, 2));
            }
            else if (id == "data")
            {
                dataOff = body; dataLen = size; break;
            }
            pos = body + size + (size & 1);
        }
        if (dataOff < 0)
            throw new InvalidDataException("WAV : chunk data introuvable.");

        // Borne la longueur déclarée aux octets réellement disponibles (robustesse fichiers tronqués).
        dataLen = Math.Min(dataLen, wav.Length - dataOff);

        int bytesPerSample = bits / 8;
        if (bytesPerSample <= 0 || channels <= 0)
            throw new NotSupportedException($"WAV : format invalide (bits={bits}, channels={channels}).");

        int frames = dataLen / (bytesPerSample * channels);
        var outp = new float[frames];
        for (int i = 0; i < frames; i++)
        {
            float acc = 0;
            for (int c = 0; c < channels; c++)
            {
                int off = dataOff + (i * channels + c) * bytesPerSample;
                float v = bits switch
                {
                    16 => BitConverter.ToInt16(wav.Slice(off, 2)) / 32768f,
                    32 => BitConverter.ToSingle(wav.Slice(off, 4)), // float32
                    _ => throw new NotSupportedException($"WAV : bits={bits} non supporté.")
                };
                acc += v;
            }
            outp[i] = acc / channels;
        }
        return (outp, sampleRate);
    }

    /// <summary>
    /// Décode un WAV en float mono à <paramref name="targetSampleRate"/> Hz.
    /// Rééchantillonnage naïf (nearest-neighbor) — suffisant pour des voice prompts courts.
    /// </summary>
    public static float[] ReadMono(ReadOnlySpan<byte> wav, int targetSampleRate)
    {
        var (mono, sr) = DecodeMono(wav);
        if (sr == targetSampleRate || mono.Length == 0)
            return mono;

        int targetLen = (int)((long)mono.Length * targetSampleRate / sr);
        var resampled = new float[targetLen];
        for (int i = 0; i < targetLen; i++)
        {
            int src = (int)((long)i * sr / targetSampleRate);
            if (src >= mono.Length) src = mono.Length - 1;
            resampled[i] = mono[src];
        }
        return resampled;
    }
}
