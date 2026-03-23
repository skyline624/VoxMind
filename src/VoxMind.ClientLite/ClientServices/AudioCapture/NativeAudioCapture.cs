using Microsoft.Extensions.Logging;
using VoxMind.ClientLite.Configuration;

namespace VoxMind.ClientLite.ClientServices.AudioCapture;

/// <summary>
/// Capture audio native : NAudio sur Windows, PortAudio sur Linux.
/// Fournit les données PCM 16kHz mono en événement.
/// </summary>
public class NativeAudioCapture : IDisposable
{
    private readonly ClientConfiguration _config;
    private readonly ILogger<NativeAudioCapture> _logger;
    private bool _disposed;

    public event EventHandler<byte[]>? AudioDataAvailable;

    public NativeAudioCapture(ClientConfiguration config, ILogger<NativeAudioCapture> logger)
    {
        _config = config;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken ct = default)
    {
#if WINDOWS
        return StartNAudioAsync(ct);
#else
        return StartPortAudioAsync(ct);
#endif
    }

    public Task StopAsync()
    {
        _logger.LogInformation("Capture audio arrêtée.");
        return Task.CompletedTask;
    }

#if WINDOWS
    private Task StartNAudioAsync(CancellationToken ct)
    {
        _logger.LogInformation("Capture NAudio démarrée (source: {Source}).", _config.AudioSource);
        // NAudio WaveInEvent / WasapiCapture selon _config.AudioSource
        // Implémentation complète fournie séparément à l'intégration
        _ = ct;
        return Task.CompletedTask;
    }
#else
    private Task StartPortAudioAsync(CancellationToken ct)
    {
        _logger.LogInformation("Capture PortAudio démarrée (source: {Source}).", _config.AudioSource);
        // PortAudioSharp2 : PortAudio.Initialize() + PortAudio.OpenDefaultStream(...)
        // Implémentation complète fournie séparément à l'intégration
        _ = ct;
        return Task.CompletedTask;
    }
#endif

    protected virtual void OnAudioDataAvailable(byte[] data) =>
        AudioDataAvailable?.Invoke(this, data);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }
}
