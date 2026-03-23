using Microsoft.Extensions.Logging;
using Whisper.net;
using Whisper.net.Ggml;

namespace VoxMind.Core.Transcription;

public class WhisperService : ITranscriptionService
{
    private WhisperFactory? _factory;
    private WhisperProcessor? _processor;
    private readonly ILogger<WhisperService> _logger;
    private readonly string _modelCacheDir;
    private ModelInfo _info = new() { IsLoaded = false };
    private bool _disposed;

    public ModelInfo Info => _info;

    public WhisperService(ILogger<WhisperService> logger, string? modelCacheDir = null)
    {
        _logger = logger;
        _modelCacheDir = modelCacheDir ?? Path.Combine(Path.GetTempPath(), "voxmind_whisper");
        Directory.CreateDirectory(_modelCacheDir);
    }

    public async Task LoadModelAsync(ModelSize size, ComputeBackend backend = ComputeBackend.Auto)
    {
        var resolvedBackend = backend == ComputeBackend.Auto
            ? ComputeBackendDetector.DetectBestAvailable()
            : backend;

        var ggmlType = ToGgmlType(size);
        _logger.LogInformation("Chargement du modèle Whisper {Size} sur {Backend}...", size, resolvedBackend);

        // Télécharger le modèle dans le dossier cache si absent
        var modelPath = Path.Combine(_modelCacheDir, $"ggml-{size.ToString().ToLower()}.bin");
        if (!File.Exists(modelPath))
        {
            _logger.LogInformation("Téléchargement du modèle Whisper {Size}...", size);
            using var modelStream = await WhisperGgmlDownloader.GetGgmlModelAsync(ggmlType);
            using var fileStream = File.Create(modelPath);
            await modelStream.CopyToAsync(fileStream);
        }

        _factory = WhisperFactory.FromPath(modelPath);

        _processor = _factory.CreateBuilder()
            .WithLanguage("auto")
            .Build();

        _info = new ModelInfo
        {
            ModelName = $"whisper-{size.ToString().ToLower()}",
            Size = size,
            Backend = resolvedBackend,
            IsLoaded = true
        };

        _logger.LogInformation("Modèle Whisper {Size} chargé.", size);
    }

    public async Task<TranscriptionResult> TranscribeFileAsync(string filePath, CancellationToken ct = default)
    {
        if (!_info.IsLoaded || _processor is null)
            throw new InvalidOperationException("Le modèle Whisper n'est pas chargé. Appeler LoadModelAsync().");

        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Fichier audio introuvable : {filePath}");

        _logger.LogDebug("Transcription du fichier {File}...", filePath);

        var segments = new List<TranscriptionSegment>();
        var fullText = new System.Text.StringBuilder();
        int segId = 0;

        using var audioStream = File.OpenRead(filePath);
        await foreach (var segment in _processor.ProcessAsync(audioStream, ct))
        {
            var tSeg = new TranscriptionSegment
            {
                Id = segId++,
                Start = segment.Start,
                End = segment.End,
                Text = segment.Text.Trim(),
                Confidence = 1.0f  // Whisper.net ne fournit pas de score par segment
            };
            segments.Add(tSeg);
            fullText.Append(segment.Text);
        }

        return new TranscriptionResult
        {
            Text = fullText.ToString().Trim(),
            Language = "fr",
            Confidence = 0.9f,
            Duration = segments.Count > 0 ? segments.Last().End : TimeSpan.Zero,
            Segments = segments,
            ProcessedAt = DateTime.UtcNow
        };
    }

    public async Task<TranscriptionResult> TranscribeChunkAsync(byte[] audioData, CancellationToken ct = default)
    {
        if (!_info.IsLoaded || _processor is null)
            throw new InvalidOperationException("Le modèle Whisper n'est pas chargé. Appeler LoadModelAsync().");

        // Sauvegarder dans un fichier temporaire WAV pour Whisper.net
        var tmpFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.wav");
        try
        {
            // audioData est attendu en format WAV PCM 16kHz mono
            await File.WriteAllBytesAsync(tmpFile, audioData, ct);
            return await TranscribeFileAsync(tmpFile, ct);
        }
        finally
        {
            if (File.Exists(tmpFile)) File.Delete(tmpFile);
        }
    }

    public async Task<string> DetectLanguageAsync(byte[] audioData)
    {
        // Whisper.net détecte la langue automatiquement lors de la transcription
        // Cette méthode retourne "auto" par défaut
        await Task.CompletedTask;
        return "auto";
    }

    private static GgmlType ToGgmlType(ModelSize size) => size switch
    {
        ModelSize.Tiny => GgmlType.Tiny,
        ModelSize.Base => GgmlType.Base,
        ModelSize.Small => GgmlType.Small,
        ModelSize.Medium => GgmlType.Medium,
        ModelSize.Large => GgmlType.LargeV3,
        _ => GgmlType.Base
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _processor?.Dispose();
        _factory?.Dispose();
    }
}
