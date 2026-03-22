namespace VoxMind.Core.Audio;

public class AudioDeviceInfo
{
    public int DeviceIndex { get; set; }
    public string Name { get; set; } = string.Empty;
    public AudioSourceType Type { get; set; }
    public int MaxChannels { get; set; }
    public int DefaultSampleRate { get; set; }
    public bool IsDefault { get; set; }
}
