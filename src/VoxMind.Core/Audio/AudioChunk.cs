namespace VoxMind.Core.Audio;

public class AudioChunk
{
    public byte[] RawData { get; }
    public AudioSourceType Source { get; }
    public TimeSpan Timestamp { get; }
    public int SampleRate { get; }

    public TimeSpan Duration =>
        TimeSpan.FromSeconds((double)SamplesCount / SampleRate);

    public int SamplesCount =>
        RawData.Length / 2; // 16-bit PCM = 2 bytes par sample

    public AudioChunk(byte[] rawData, AudioSourceType source, TimeSpan timestamp, int sampleRate)
    {
        RawData = rawData ?? throw new ArgumentNullException(nameof(rawData));
        Source = source;
        Timestamp = timestamp;
        SampleRate = sampleRate;
    }
}

public class AudioChunkEventArgs : EventArgs
{
    public AudioChunk Chunk { get; }

    public AudioChunkEventArgs(AudioChunk chunk)
    {
        Chunk = chunk;
    }
}
