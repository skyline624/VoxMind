using Microsoft.Extensions.Logging;
using VoxMind.ClientLite.Configuration;

#if WINDOWS
using NAudio.Wave;
#else
using System.Runtime.InteropServices;
using PAStream = PortAudioSharp.Stream;
using PortAudioSharp;
#endif

namespace VoxMind.ClientLite.ClientServices.AudioCapture;

/// <summary>
/// Capture audio native : WaveInEvent/WASAPI sur Windows, PortAudioSharp2 sur Linux.
/// Émet les données PCM 16kHz 16-bit mono via <see cref="AudioDataAvailable"/>.
/// </summary>
public class LiveAudioCapture : IDisposable
{
    private readonly ClientConfiguration _config;
    private readonly ILogger<LiveAudioCapture> _logger;
    private bool _isCapturing;
    private bool _disposed;

    public event EventHandler<byte[]>? AudioDataAvailable;

#if WINDOWS
    private WaveInEvent? _waveIn;
    private WasapiLoopbackCapture? _loopback;
#else
    private PAStream? _paStream;
#endif

    public LiveAudioCapture(ClientConfiguration config, ILogger<LiveAudioCapture> logger)
    {
        _config = config;
        _logger = logger;
    }

    /// <summary>Démarre la capture avec la source définie dans la config.</summary>
    public Task StartAsync(CancellationToken ct = default) =>
        StartAsync(_config.AudioSource, ct);

    /// <summary>Démarre la capture avec une source explicite ("microphone" ou "system").</summary>
    public Task StartAsync(string audioSource, CancellationToken ct = default)
    {
        if (_isCapturing) return Task.CompletedTask;
#if WINDOWS
        return StartNAudioAsync(audioSource, ct);
#else
        return StartPortAudioAsync(audioSource, ct);
#endif
    }

    public async Task StopAsync()
    {
        if (!_isCapturing) return;
        _isCapturing = false;
#if WINDOWS
        await Task.Run(() =>
        {
            _waveIn?.StopRecording();
            _waveIn?.Dispose();
            _waveIn = null;
            _loopback?.StopRecording();
            _loopback?.Dispose();
            _loopback = null;
        });
#else
        await Task.Run(() =>
        {
            try
            {
                _paStream?.Stop();
                _paStream?.Close();
                _paStream?.Dispose();
                _paStream = null;
                PortAudio.Terminate();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Erreur lors de l'arrêt PortAudio.");
            }
        });
#endif
        _logger.LogInformation("Capture audio arrêtée.");
    }

#if WINDOWS
    private Task StartNAudioAsync(string audioSource, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            if (audioSource == "system")
            {
                _loopback = new WasapiLoopbackCapture();
                _loopback.DataAvailable += (_, e) =>
                {
                    if (e.BytesRecorded > 0)
                        AudioDataAvailable?.Invoke(this, e.Buffer.Take(e.BytesRecorded).ToArray());
                };
                _loopback.StartRecording();
                _logger.LogInformation("Capture WASAPI loopback (audio système) démarrée.");
            }
            else
            {
                _waveIn = new WaveInEvent { WaveFormat = new WaveFormat(16000, 16, 1) };
                _waveIn.DataAvailable += (_, e) =>
                {
                    if (e.BytesRecorded > 0)
                        AudioDataAvailable?.Invoke(this, e.Buffer.Take(e.BytesRecorded).ToArray());
                };
                _waveIn.StartRecording();
                _logger.LogInformation("Capture WaveIn microphone (16kHz/16-bit/mono) démarrée.");
            }
            _isCapturing = true;
        }, ct);
    }
#else
    private Task StartPortAudioAsync(string audioSource, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            PortAudio.Initialize();

            int deviceIndex = audioSource == "system"
                ? PortAudio.DefaultOutputDevice
                : PortAudio.DefaultInputDevice;

            if (deviceIndex < 0)
            {
                _logger.LogError("Aucun périphérique audio PortAudio disponible (source: {Source}).", audioSource);
                PortAudio.Terminate();
                return;
            }

            var deviceInfo = PortAudio.GetDeviceInfo(deviceIndex);
            _logger.LogDebug("Périphérique PortAudio : {Name} (index={Index})", deviceInfo.name, deviceIndex);

            var inParams = new StreamParameters
            {
                device = deviceIndex,
                channelCount = 1,
                sampleFormat = SampleFormat.Int16,
                suggestedLatency = deviceInfo.defaultLowInputLatency,
                hostApiSpecificStreamInfo = IntPtr.Zero
            };

            _paStream = new PAStream(
                inParams: inParams,
                outParams: null,
                sampleRate: 16000.0,
                framesPerBuffer: 256,
                streamFlags: StreamFlags.ClipOff,
                callback: OnPortAudioCallback,
                userData: IntPtr.Zero
            );

            _paStream.Start();
            _isCapturing = true;
            _logger.LogInformation("Capture PortAudio démarrée (source: {Source}, device: {Name}).",
                audioSource, deviceInfo.name);
        }, ct);
    }

    private StreamCallbackResult OnPortAudioCallback(
        IntPtr input, IntPtr output,
        uint frameCount,
        ref StreamCallbackTimeInfo timeInfo,
        StreamCallbackFlags statusFlags,
        IntPtr userData)
    {
        if (input == IntPtr.Zero || frameCount == 0)
            return StreamCallbackResult.Continue;

        var bytes = new byte[frameCount * 2]; // Int16 = 2 octets par sample
        Marshal.Copy(input, bytes, 0, bytes.Length);
        AudioDataAvailable?.Invoke(this, bytes);
        return StreamCallbackResult.Continue;
    }
#endif

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopAsync().GetAwaiter().GetResult();
    }
}
