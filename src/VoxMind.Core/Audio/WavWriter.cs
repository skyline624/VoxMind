using System.Buffers.Binary;

namespace VoxMind.Core.Audio;

/// <summary>
/// Helper minimal d'écriture WAV PCM 16 bits.
///
/// Pourquoi pas NAudio ? <c>NAudio</c> n'est référencé que sous Windows
/// (<see href="../VoxMind.Core.csproj"/>) ; on a besoin d'une sortie WAV
/// cross-plateforme pour le module TTS. Le format RIFF/WAVE PCM est trivial
/// (~30 LOC), pas la peine d'ajouter une dépendance.
/// </summary>
public static class WavWriter
{
    /// <summary>
    /// Écrit un fichier WAV PCM 16 bits dans <paramref name="destination"/>.
    /// </summary>
    /// <param name="destination">Stream de sortie (laissé ouvert après écriture).</param>
    /// <param name="pcm">Samples PCM normalisés [-1, 1] en float32.</param>
    /// <param name="sampleRate">Fréquence d'échantillonnage en Hz (24000 pour F5-TTS).</param>
    /// <param name="channels">Nombre de canaux (1 pour le TTS de Seren).</param>
    public static void Write(
        Stream destination,
        ReadOnlySpan<float> pcm,
        int sampleRate,
        int channels = 1)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));

        const int bitsPerSample = 16;
        int byteRate = sampleRate * channels * bitsPerSample / 8;
        int blockAlign = channels * bitsPerSample / 8;
        int dataSize = pcm.Length * 2;
        int riffSize = 36 + dataSize;

        Span<byte> header = stackalloc byte[44];
        // "RIFF"
        header[0] = (byte)'R'; header[1] = (byte)'I'; header[2] = (byte)'F'; header[3] = (byte)'F';
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(4, 4), riffSize);
        // "WAVE"
        header[8] = (byte)'W'; header[9] = (byte)'A'; header[10] = (byte)'V'; header[11] = (byte)'E';
        // "fmt "
        header[12] = (byte)'f'; header[13] = (byte)'m'; header[14] = (byte)'t'; header[15] = (byte)' ';
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(16, 4), 16); // PCM chunk size
        BinaryPrimitives.WriteInt16LittleEndian(header.Slice(20, 2), 1);  // PCM format
        BinaryPrimitives.WriteInt16LittleEndian(header.Slice(22, 2), (short)channels);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(24, 4), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(28, 4), byteRate);
        BinaryPrimitives.WriteInt16LittleEndian(header.Slice(32, 2), (short)blockAlign);
        BinaryPrimitives.WriteInt16LittleEndian(header.Slice(34, 2), bitsPerSample);
        // "data"
        header[36] = (byte)'d'; header[37] = (byte)'a'; header[38] = (byte)'t'; header[39] = (byte)'a';
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(40, 4), dataSize);

        destination.Write(header);
        WritePcm16(destination, pcm);
    }

    /// <summary>
    /// Écrit un en-tête WAV « streaming » (44 octets) : identique à celui de <see cref="Write"/> mais avec
    /// les tailles RIFF et <c>data</c> positionnées à la sentinelle <c>0xFFFFFFFF</c> (« lire jusqu'à EOF »).
    ///
    /// À utiliser quand on émet le PCM au fil de l'eau — synthèse phrase par phrase — et qu'on ne connaît
    /// donc pas la longueur totale à l'avance : on écrit l'en-tête une fois, puis chaque segment via
    /// <see cref="WritePcm16"/>. Les lecteurs progressifs (navigateurs, &lt;audio&gt;, ffmpeg) reconnaissent
    /// la sentinelle et lisent jusqu'à la fin du flux ; un client qui bufferise tout puis sauvegarde obtient
    /// un WAV jouable, au prix d'une durée éventuellement mal estimée par un parseur strict (préférer alors
    /// le format <c>pcm</c>, sans en-tête). C'est l'approche du mode streaming d'OpenAI /v1/audio/speech.
    /// </summary>
    public static void WriteStreamingHeader(Stream destination, int sampleRate, int channels = 1)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));

        const int bitsPerSample = 16;
        int byteRate = sampleRate * channels * bitsPerSample / 8;
        int blockAlign = channels * bitsPerSample / 8;

        Span<byte> header = stackalloc byte[44];
        header[0] = (byte)'R'; header[1] = (byte)'I'; header[2] = (byte)'F'; header[3] = (byte)'F';
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(4, 4), 0xFFFFFFFFu); // taille totale inconnue (streaming)
        header[8] = (byte)'W'; header[9] = (byte)'A'; header[10] = (byte)'V'; header[11] = (byte)'E';
        header[12] = (byte)'f'; header[13] = (byte)'m'; header[14] = (byte)'t'; header[15] = (byte)' ';
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(16, 4), 16); // PCM chunk size
        BinaryPrimitives.WriteInt16LittleEndian(header.Slice(20, 2), 1);  // PCM format
        BinaryPrimitives.WriteInt16LittleEndian(header.Slice(22, 2), (short)channels);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(24, 4), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(28, 4), byteRate);
        BinaryPrimitives.WriteInt16LittleEndian(header.Slice(32, 2), (short)blockAlign);
        BinaryPrimitives.WriteInt16LittleEndian(header.Slice(34, 2), bitsPerSample);
        header[36] = (byte)'d'; header[37] = (byte)'a'; header[38] = (byte)'t'; header[39] = (byte)'a';
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(40, 4), 0xFFFFFFFFu); // taille data inconnue (streaming)

        destination.Write(header);
    }

    /// <summary>
    /// Écrit le corps PCM 16 bits (sans en-tête) de <paramref name="pcm"/> dans
    /// <paramref name="destination"/> : clamp [-1, 1] puis scale 32767, little-endian. Utilisé pour le
    /// format brut <c>pcm</c> et pour chaque segment émis après <see cref="WriteStreamingHeader"/>.
    /// </summary>
    public static void WritePcm16(Stream destination, ReadOnlySpan<float> pcm)
    {
        if (pcm.IsEmpty) return;

        var buffer = new byte[pcm.Length * 2];
        for (int i = 0; i < pcm.Length; i++)
        {
            float clamped = Math.Clamp(pcm[i], -1f, 1f);
            short s16 = (short)(clamped * 32767f);
            BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(i * 2, 2), s16);
        }
        destination.Write(buffer, 0, buffer.Length);
    }

    /// <summary>
    /// Variante mémoire — retourne un <c>byte[]</c> contenant le WAV complet.
    /// Pratique pour l'API qui doit envoyer un blob HTTP.
    /// </summary>
    public static byte[] ToBytes(ReadOnlySpan<float> pcm, int sampleRate, int channels = 1)
    {
        using var ms = new MemoryStream(44 + pcm.Length * 2);
        Write(ms, pcm, sampleRate, channels);
        return ms.ToArray();
    }
}
