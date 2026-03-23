using Microsoft.Extensions.Logging;
using Whisper.net;
using Whisper.net.Ggml;

namespace VoxMind.Core.Transcription;

/// <summary>
/// Service de transcription via Whisper.net (whisper.cpp bindings, modèle GGML local).
/// Requiert un fichier .bin téléchargé depuis HuggingFace ou via GgmlDownloader.
/// </summary>
public class WhisperNetTranscriptionService : ITranscriptionService
{
    private readonly ILogger<WhisperNetTranscriptionService> _logger;
    private WhisperFactory? _factory;
    private ModelInfo _info;
    private bool _disposed;

    public ModelInfo Info => _info;

    public WhisperNetTranscriptionService(string modelPath, ILogger<WhisperNetTranscriptionService> logger)
    {
        _logger = logger;
        _info = new ModelInfo
        {
            ModelName = "whisper",
            Size = ModelSize.Medium,
            Backend = ComputeBackend.CPU,
            IsLoaded = false
        };

        if (!File.Exists(modelPath))
        {
            _logger.LogWarning("Modèle Whisper introuvable : {Path}. Transcription Whisper désactivée.", modelPath);
            return;
        }

        TryLoadModel(modelPath);
    }

    private void TryLoadModel(string modelPath)
    {
        try
        {
            _factory = WhisperFactory.FromPath(modelPath);
            _info.IsLoaded = true;
            _logger.LogInformation("Whisper.net chargé depuis {Path}.", modelPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Impossible de charger le modèle Whisper depuis {Path}.", modelPath);
        }
    }

    public async Task<TranscriptionResult> TranscribeFileAsync(string filePath, CancellationToken ct = default)
    {
        if (_factory is null)
            return new TranscriptionResult { Text = string.Empty };

        await using var processor = _factory.CreateBuilder()
            .WithLanguage("auto")
            .Build();

        await using var fileStream = File.OpenRead(filePath);
        return await RunProcessorAsync(processor, fileStream, ct);
    }

    public async Task<TranscriptionResult> TranscribeChunkAsync(byte[] audioData, CancellationToken ct = default)
    {
        if (_factory is null)
            return new TranscriptionResult { Text = string.Empty };

        await using var processor = _factory.CreateBuilder()
            .WithLanguage("auto")
            .Build();

        // Whisper.net attend un stream WAV ; audioData est du PCM 16-bit 16kHz mono brut
        // On encapsule dans un header WAV minimal
        await using var wavStream = WrapPcmInWav(audioData, sampleRate: 16000, channels: 1, bitsPerSample: 16);
        return await RunProcessorAsync(processor, wavStream, ct);
    }

    private static async Task<TranscriptionResult> RunProcessorAsync(
        WhisperProcessor processor, Stream audioStream, CancellationToken ct)
    {
        var segments = new List<TranscriptionSegment>();
        var fullText = new System.Text.StringBuilder();

        await foreach (var seg in processor.ProcessAsync(audioStream, ct))
        {
            fullText.Append(seg.Text);
            segments.Add(new TranscriptionSegment
            {
                Id = segments.Count,
                Start = seg.Start,
                End = seg.End,
                Text = seg.Text.Trim(),
                Confidence = 0.9f
            });
        }

        var duration = segments.Count > 0 ? segments[^1].End : TimeSpan.Zero;
        return new TranscriptionResult
        {
            Text = fullText.ToString().Trim(),
            Language = "auto",
            Confidence = 0.9f,
            Duration = duration,
            Segments = segments
        };
    }

    public Task<string> DetectLanguageAsync(byte[] audioData) => Task.FromResult("auto");

    public Task LoadModelAsync(ModelSize size, ComputeBackend backend = ComputeBackend.Auto)
    {
        _info = new ModelInfo
        {
            ModelName = _info.ModelName,
            Size = size,
            Backend = backend == ComputeBackend.Auto ? ComputeBackend.CPU : backend,
            IsLoaded = _info.IsLoaded
        };
        return Task.CompletedTask;
    }

    /// <summary>Encapsule des bytes PCM bruts dans un header WAV minimal en mémoire.</summary>
    private static MemoryStream WrapPcmInWav(byte[] pcmData, int sampleRate, int channels, int bitsPerSample)
    {
        var ms = new MemoryStream();
        var writer = new BinaryWriter(ms);
        int byteRate = sampleRate * channels * bitsPerSample / 8;
        int blockAlign = channels * bitsPerSample / 8;

        writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + pcmData.Length);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);           // chunk size
        writer.Write((short)1);     // PCM
        writer.Write((short)channels);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write((short)blockAlign);
        writer.Write((short)bitsPerSample);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        writer.Write(pcmData.Length);
        writer.Write(pcmData);

        ms.Position = 0;
        return ms;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _factory?.Dispose();
    }
}
