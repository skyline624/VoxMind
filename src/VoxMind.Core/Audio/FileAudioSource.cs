using FFMpegCore;
using FFMpegCore.Pipes;
using Microsoft.Extensions.Logging;

namespace VoxMind.Core.Audio;

/// <summary>
/// Source audio fichier : décode n'importe quel format (MP3, WAV, OGG, Opus, WebM...)
/// en PCM 16kHz 16-bit mono via FFMpegCore, et émet les données via <see cref="AudioChunkReceived"/>.
/// </summary>
public class FileAudioSource : IAudioCapture
{
    private readonly string _filePath;
    private readonly ILogger<FileAudioSource> _logger;
    private AudioConfiguration? _config;
    private bool _isCapturing;
    private bool _disposed;
    private CancellationTokenSource? _cts;
    private Task? _decodeTask;

    public bool IsCapturing => _isCapturing;
    public bool IsLive => false;
    public AudioConfiguration? CurrentConfig => _config;

    public event EventHandler<AudioChunkEventArgs>? AudioChunkReceived;

    /// <summary>Déclenché quand la lecture du fichier est terminée (EOF).</summary>
    public event EventHandler? PlaybackCompleted;

    public FileAudioSource(string filePath, ILogger<FileAudioSource> logger)
    {
        _filePath = filePath;
        _logger = logger;
    }

    public Task<IReadOnlyList<AudioDeviceInfo>> GetAvailableSourcesAsync()
    {
        var info = new AudioDeviceInfo
        {
            DeviceIndex = 0,
            Name = Path.GetFileName(_filePath),
            Type = AudioSourceType.File,
            IsDefault = true
        };
        return Task.FromResult<IReadOnlyList<AudioDeviceInfo>>(new[] { info });
    }

    public Task StartCaptureAsync(AudioConfiguration config, CancellationToken ct = default)
    {
        if (_isCapturing) return Task.CompletedTask;

        if (!File.Exists(_filePath))
            throw new FileNotFoundException($"Audio file not found: {_filePath}");

        _config = config;
        _isCapturing = true;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        _decodeTask = Task.Run(() => DecodeAndEmitAsync(_cts.Token), _cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopCaptureAsync()
    {
        if (!_isCapturing) return;
        _isCapturing = false;

        _cts?.Cancel();
        if (_decodeTask is not null)
        {
            try { await _decodeTask; }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _logger.LogDebug(ex, "Erreur à l'arrêt de FileAudioSource."); }
        }
        _logger.LogInformation("FileAudioSource: lecture arrêtée ({File}).", Path.GetFileName(_filePath));
    }

    private async Task DecodeAndEmitAsync(CancellationToken ct)
    {
        var sampleRate = _config?.SampleRate ?? 16000;
        // 100ms de PCM 16-bit mono = sampleRate/10 samples × 2 bytes
        int chunkSize = sampleRate / 10 * 2;

        var elapsed = TimeSpan.Zero;
        var chunkingStream = new PcmChunkingStream(chunkSize, (pcmData) =>
        {
            var chunk = new AudioChunk(pcmData, AudioSourceType.File, elapsed, sampleRate);
            // Avancer le timestamp de la durée du chunk
            elapsed += chunk.Duration;
            AudioChunkReceived?.Invoke(this, new AudioChunkEventArgs(chunk));
        });

        try
        {
            _logger.LogInformation("FileAudioSource: décodage de {File} (ffmpeg → PCM {Rate}Hz mono).",
                Path.GetFileName(_filePath), sampleRate);

            await FFMpegArguments
                .FromFileInput(_filePath)
                .OutputToPipe(new StreamPipeSink(chunkingStream), options => options
                    .WithAudioSamplingRate(sampleRate)
                    .WithCustomArgument("-ac 1 -acodec pcm_s16le")
                    .ForceFormat("s16le"))
                .CancellableThrough(ct)
                .ProcessAsynchronously(throwOnError: false);

            // Émettre le dernier chunk partiel s'il reste des données
            chunkingStream.FlushRemaining(elapsed, (pcmData) =>
            {
                var chunk = new AudioChunk(pcmData, AudioSourceType.File, elapsed, sampleRate);
                AudioChunkReceived?.Invoke(this, new AudioChunkEventArgs(chunk));
            });

            if (!ct.IsCancellationRequested)
            {
                _logger.LogInformation("FileAudioSource: lecture terminée ({File}).", Path.GetFileName(_filePath));
                _isCapturing = false;
                PlaybackCompleted?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("FileAudioSource: lecture annulée.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FileAudioSource: erreur lors du décodage de {File}.", _filePath);
            _isCapturing = false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopCaptureAsync().GetAwaiter().GetResult();
        _cts?.Dispose();
    }

    // ── Stream auxiliaire qui découpe le flux PCM brut en chunks de taille fixe ──

    private sealed class PcmChunkingStream : Stream
    {
        private readonly int _chunkSize;
        private readonly Action<byte[]> _onChunk;
        private readonly byte[] _buffer;
        private int _pos;

        public PcmChunkingStream(int chunkSize, Action<byte[]> onChunk)
        {
            _chunkSize = chunkSize;
            _onChunk = onChunk;
            _buffer = new byte[chunkSize];
        }

        public override void Write(byte[] data, int offset, int count)
        {
            int src = offset;
            int end = offset + count;
            while (src < end)
            {
                int toCopy = Math.Min(_chunkSize - _pos, end - src);
                Array.Copy(data, src, _buffer, _pos, toCopy);
                _pos += toCopy;
                src += toCopy;

                if (_pos >= _chunkSize)
                {
                    _onChunk(_buffer.ToArray());
                    _pos = 0;
                }
            }
        }

        /// <summary>Émet le dernier chunk partiel (< chunkSize) restant dans le buffer.</summary>
        public void FlushRemaining(TimeSpan currentTimestamp, Action<byte[]> onLastChunk)
        {
            if (_pos > 0)
            {
                onLastChunk(_buffer[.._pos].ToArray());
                _pos = 0;
            }
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
