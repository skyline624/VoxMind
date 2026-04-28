using VoxMind.Core.Audio;
using Xunit;

namespace VoxMind.Tests.Unit.Audio;

public class WavWriterTests
{
    [Fact]
    public void Write_ProducesRiffHeaderWithCorrectFields()
    {
        var pcm = new[] { 0.0f, 0.5f, -0.5f, 1.0f, -1.0f };
        var bytes = WavWriter.ToBytes(pcm, sampleRate: 24000, channels: 1);

        // En-tête RIFF
        Assert.Equal((byte)'R', bytes[0]);
        Assert.Equal((byte)'I', bytes[1]);
        Assert.Equal((byte)'F', bytes[2]);
        Assert.Equal((byte)'F', bytes[3]);
        Assert.Equal((byte)'W', bytes[8]);
        Assert.Equal((byte)'A', bytes[9]);
        Assert.Equal((byte)'V', bytes[10]);
        Assert.Equal((byte)'E', bytes[11]);
        Assert.Equal((byte)'f', bytes[12]);
        Assert.Equal((byte)'m', bytes[13]);
        Assert.Equal((byte)'t', bytes[14]);

        // sample rate (offset 24, little-endian)
        int sampleRate = BitConverter.ToInt32(bytes, 24);
        Assert.Equal(24000, sampleRate);

        // bits per sample (offset 34)
        short bps = BitConverter.ToInt16(bytes, 34);
        Assert.Equal(16, bps);

        // channels (offset 22)
        short channels = BitConverter.ToInt16(bytes, 22);
        Assert.Equal(1, channels);

        // Data chunk = 5 samples × 2 bytes
        int dataSize = BitConverter.ToInt32(bytes, 40);
        Assert.Equal(10, dataSize);
        Assert.Equal(44 + 10, bytes.Length);
    }

    [Fact]
    public void Write_RoundTripsPcmData()
    {
        // 1 kHz sine wave, 0.1 s à 24 kHz
        int sr = 24000;
        var pcm = new float[sr / 10];
        for (int i = 0; i < pcm.Length; i++)
            pcm[i] = (float)Math.Sin(2 * Math.PI * 1000 * i / sr) * 0.5f;

        var wav = WavWriter.ToBytes(pcm, sr);

        // Décodage int16 LE depuis offset 44, conversion en float
        var roundTrip = new float[pcm.Length];
        for (int i = 0; i < pcm.Length; i++)
            roundTrip[i] = BitConverter.ToInt16(wav, 44 + i * 2) / 32767f;

        for (int i = 0; i < pcm.Length; i++)
            Assert.InRange(roundTrip[i] - pcm[i], -0.001f, 0.001f);
    }

    [Theory]
    [InlineData(2.0f)]   // au-dessus du clip
    [InlineData(-2.0f)]  // en dessous du clip
    public void Write_ClipsOutOfRangeSamples(float sample)
    {
        var bytes = WavWriter.ToBytes(new[] { sample }, sampleRate: 24000);
        short s16 = BitConverter.ToInt16(bytes, 44);
        // Sample = ±1.0 après clamp → ±32767 (avec rounding to int16)
        Assert.InRange(Math.Abs(s16), 32766, 32767);
    }
}
