using Microsoft.Extensions.Logging;

namespace VoxMind.Core.Audio;

/// <summary>Capture audio via NAudio pour Windows. Requiert NAudio NuGet.</summary>
public class NAudioCapture : IAudioCapture
{
    private readonly ILogger<NAudioCapture> _logger;
    private AudioConfiguration? _config;
    private bool _isCapturing;
    private bool _disposed;
    // NAudio.Wave.WaveInEvent _waveIn; — instancié à la demande

    public bool IsCapturing => _isCapturing;
    public bool IsLive => true;
    public AudioConfiguration? CurrentConfig => _config;
    public event EventHandler<AudioChunkEventArgs>? AudioChunkReceived;

    public NAudioCapture(ILogger<NAudioCapture> logger)
    {
        _logger = logger;
    }

    public Task<IReadOnlyList<AudioDeviceInfo>> GetAvailableSourcesAsync()
    {
        // TODO: Itérer sur NAudio.Wave.WaveIn.GetCapabilities()
        _logger.LogDebug("GetAvailableSourcesAsync — NAudio");
        return Task.FromResult<IReadOnlyList<AudioDeviceInfo>>(new List<AudioDeviceInfo>
        {
            new() { DeviceIndex = -1, Name = "Default Input", Type = AudioSourceType.Microphone, IsDefault = true }
        });
    }

    public Task StartCaptureAsync(AudioConfiguration config, CancellationToken ct = default)
    {
        _config = config;
        _isCapturing = true;
        _logger.LogInformation("NAudioCapture: capture démarrée (TODO: implémenter NAudio WaveInEvent).");
        // TODO:
        // _waveIn = new NAudio.Wave.WaveInEvent();
        // _waveIn.WaveFormat = new NAudio.Wave.WaveFormat(config.SampleRate, config.BitDepth, config.Channels);
        // _waveIn.BufferMilliseconds = config.ChunkDurationMs;
        // _waveIn.DataAvailable += (s, e) => {
        //     var chunk = new AudioChunk(e.Buffer[..e.BytesRecorded], AudioSourceType.Microphone, ...);
        //     AudioChunkReceived?.Invoke(this, new AudioChunkEventArgs(chunk));
        // };
        // _waveIn.StartRecording();
        return Task.CompletedTask;
    }

    public Task StopCaptureAsync()
    {
        _isCapturing = false;
        _logger.LogInformation("NAudioCapture: capture arrêtée.");
        // TODO: _waveIn?.StopRecording(); _waveIn?.Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _isCapturing = false;
        // TODO: _waveIn?.Dispose();
    }
}
