using Microsoft.Extensions.Logging;

namespace VoxMind.Core.Audio;

/// <summary>Capture audio via PortAudio pour Linux. Requiert PortAudioSharp2 NuGet.</summary>
public class PortAudioCapture : IAudioCapture
{
    private readonly ILogger<PortAudioCapture> _logger;
    private AudioConfiguration? _config;
    private bool _isCapturing;
    private bool _disposed;

    public bool IsCapturing => _isCapturing;
    public bool IsLive => true;
    public AudioConfiguration? CurrentConfig => _config;
    public event EventHandler<AudioChunkEventArgs>? AudioChunkReceived;

    public PortAudioCapture(ILogger<PortAudioCapture> logger)
    {
        _logger = logger;
    }

    public Task<IReadOnlyList<AudioDeviceInfo>> GetAvailableSourcesAsync()
    {
        // TODO: Implémenter via PortAudioSharp2.PortAudio.DeviceCount
        _logger.LogDebug("GetAvailableSourcesAsync — PortAudio");
        return Task.FromResult<IReadOnlyList<AudioDeviceInfo>>(new List<AudioDeviceInfo>
        {
            new() { DeviceIndex = -1, Name = "Default Input", Type = AudioSourceType.Microphone, IsDefault = true }
        });
    }

    public Task StartCaptureAsync(AudioConfiguration config, CancellationToken ct = default)
    {
        _config = config;
        _isCapturing = true;
        _logger.LogInformation("PortAudioCapture: capture démarrée (TODO: implémenter PortAudioSharp2).");
        // TODO: Initialiser PortAudio, créer un stream, démarrer la capture
        // Exemple : PortAudioSharp.Stream.Open(...) avec callback qui déclenche AudioChunkReceived
        return Task.CompletedTask;
    }

    public Task StopCaptureAsync()
    {
        _isCapturing = false;
        _logger.LogInformation("PortAudioCapture: capture arrêtée.");
        // TODO: Fermer le stream PortAudio
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _isCapturing = false;
        // TODO: Libérer les ressources PortAudio
    }
}
