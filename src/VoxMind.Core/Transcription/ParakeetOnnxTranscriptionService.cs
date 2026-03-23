using System.Text;
using FFMpegCore;
using FFMpegCore.Pipes;
using Microsoft.Extensions.Logging;
using VoxMind.Core.Vad;
using VoxMind.Parakeet;

namespace VoxMind.Core.Transcription;

/// <summary>
/// Transcription service using Parakeet TDT ONNX models (100% local, no Python).
/// Uses smcleod/parakeet-tdt-0.6b-v3-int8 from HuggingFace.
///
/// Required model files in modelDir:
///   - nemo128.onnx               (mel spectrogram feature extractor)
///   - encoder-model.int8.onnx    (Parakeet encoder)
///   - decoder_joint-model.int8.onnx (TDT decoder-joint)
///   - vocab.txt                  (vocabulary)
/// </summary>
public class ParakeetOnnxTranscriptionService : ITranscriptionService
{
    private readonly ILogger<ParakeetOnnxTranscriptionService> _logger;
    private readonly IVadService _vadService;
    private ModelInfo _info;
    private AudioPreprocessor? _preprocessor;
    private ParakeetEncoder? _encoder;
    private ParakeetDecoderJoint? _decoder;
    private TokenDecoder? _tokenDecoder;
    private bool _disposed;

    public ModelInfo Info => _info;

    public ParakeetOnnxTranscriptionService(
        string modelDir,
        IVadService vadService,
        ILogger<ParakeetOnnxTranscriptionService> logger)
    {
        _logger = logger;
        _vadService = vadService;
        _info = new ModelInfo
        {
            ModelName = "parakeet-tdt-0.6b-v3-int8",
            Size = ModelSize.Small,
            Backend = ComputeBackend.CPU,
            IsLoaded = false
        };

        if (!Directory.Exists(modelDir))
        {
            _logger.LogWarning("Répertoire du modèle Parakeet introuvable : {Path}. Transcription désactivée.", modelDir);
            return;
        }

        TryLoadModels(modelDir);
    }

    private void TryLoadModels(string modelDir)
    {
        try
        {
            _tokenDecoder = new TokenDecoder(Path.Combine(modelDir, "vocab.txt"));
            _preprocessor = new AudioPreprocessor(Path.Combine(modelDir, "nemo128.onnx"));
            _encoder = new ParakeetEncoder(Path.Combine(modelDir, "encoder-model.int8.onnx"));
            _decoder = new ParakeetDecoderJoint(Path.Combine(modelDir, "decoder_joint-model.int8.onnx"), _tokenDecoder);

            _info.IsLoaded = true;
            _logger.LogInformation("Parakeet ONNX chargé depuis {Path}. Vocab: {Vocab} tokens.", modelDir, _tokenDecoder.VocabSize);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Impossible de charger les modèles Parakeet ONNX depuis {Path}.", modelDir);
        }
    }

    /// <summary>
    /// Transcrire un chunk court (&lt;12.5s) de PCM WAV 16kHz mono en mémoire (API live).
    /// </summary>
    public async Task<TranscriptionResult> TranscribeChunkAsync(byte[] audioData, CancellationToken ct = default)
    {
        if (_preprocessor is null || _encoder is null || _decoder is null || _tokenDecoder is null)
            return new TranscriptionResult { Text = string.Empty };

        try
        {
            float[] samples = ConvertWavToFloat32(audioData);
            return await Task.Run(() => RunInferenceOnSamples(samples), ct);
        }
        catch (OperationCanceledException)
        {
            return new TranscriptionResult { Text = string.Empty };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Erreur lors de la transcription Parakeet ONNX.");
            return new TranscriptionResult { Text = string.Empty };
        }
    }

    /// <summary>
    /// Transcrire un fichier audio (tout format) via VAD + Parakeet.
    /// Le fichier est décodé en PCM float32, segmenté par le VAD, puis
    /// chaque segment est transcrit individuellement avec son timestamp.
    /// </summary>
    public async Task<TranscriptionResult> TranscribeFileAsync(string filePath, CancellationToken ct = default)
    {
        if (_preprocessor is null || _encoder is null || _decoder is null || _tokenDecoder is null)
            return new TranscriptionResult { Text = string.Empty };

        float[] allSamples = await DecodeToFloat32Async(filePath, ct);
        if (allSamples.Length == 0)
            return new TranscriptionResult { Text = string.Empty };

        IReadOnlyList<VadSegment> segments = _vadService.IsAvailable
            ? _vadService.DetectSpeech(allSamples)
            : FallbackChunks(allSamples);

        _logger.LogInformation("Transcription de {File} : {Segs} segments VAD sur {Total:F1}s d'audio.",
            Path.GetFileName(filePath), segments.Count, allSamples.Length / 16000.0);

        var resultSegments = new List<TranscriptionSegment>();
        var sb = new StringBuilder();
        int idx = 0;

        foreach (var seg in segments)
        {
            ct.ThrowIfCancellationRequested();

            TranscriptionResult r;
            try
            {
                r = await Task.Run(() => RunInferenceOnSamples(seg.Samples), ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Segment {Idx} ignoré ({Start:F1}s→{End:F1}s) suite à une erreur d'inférence.", idx, seg.StartSeconds, seg.EndSeconds);
                idx++;
                continue;
            }

            if (string.IsNullOrWhiteSpace(r.Text))
            {
                idx++;
                continue;
            }

            sb.Append(r.Text.Trim()).Append(' ');
            resultSegments.Add(new TranscriptionSegment
            {
                Id = idx++,
                Start = TimeSpan.FromSeconds(seg.StartSeconds),
                End = TimeSpan.FromSeconds(seg.EndSeconds),
                Text = r.Text.Trim(),
                Confidence = r.Confidence,
            });
        }

        return new TranscriptionResult
        {
            Text = sb.ToString().Trim(),
            Segments = resultSegments,
            Duration = TimeSpan.FromSeconds(allSamples.Length / 16000.0),
            Language = "en",
            Confidence = resultSegments.Count > 0
                ? resultSegments.Average(s => s.Confidence) : 0f,
        };
    }

    private TranscriptionResult RunInferenceOnSamples(float[] samples)
    {
        if (samples.Length == 0)
            return new TranscriptionResult { Text = string.Empty };

        var (melFeatures, melFrames) = _preprocessor!.ComputeMelSpectrogram(samples);
        if (melFrames == 0)
            return new TranscriptionResult { Text = string.Empty };

        var (encoderOutput, encodedFrames, hiddenDim) = _encoder!.Encode(melFeatures, melFrames);
        if (encodedFrames == 0)
            return new TranscriptionResult { Text = string.Empty };

        int[] tokenIds = _decoder!.DecodeGreedy(encoderOutput, encodedFrames, hiddenDim);
        string text = _tokenDecoder!.DecodeTokens(tokenIds);

        return new TranscriptionResult
        {
            Text = text,
            Language = "en",
            Confidence = 0.9f,
            Duration = TimeSpan.FromSeconds(samples.Length / 16000.0),
            Segments = new List<TranscriptionSegment>
            {
                new() { Id = 0, Text = text, Confidence = 0.9f }
            }
        };
    }

    /// <summary>
    /// Décode un fichier audio (tout format) en samples PCM float32 16kHz mono.
    /// </summary>
    private static async Task<float[]> DecodeToFloat32Async(string filePath, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var ext = Path.GetExtension(filePath).ToLowerInvariant();

        if (ext == ".wav")
        {
            var raw = await File.ReadAllBytesAsync(filePath, ct);
            return ConvertWavToFloat32(raw);
        }

        await FFMpegArguments
            .FromFileInput(filePath)
            .OutputToPipe(new StreamPipeSink(ms), opts => opts
                .WithAudioSamplingRate(16000)
                .WithCustomArgument("-ac 1 -acodec pcm_s16le")
                .ForceFormat("wav"))
            .CancellableThrough(ct)
            .ProcessAsynchronously(throwOnError: true);

        return ConvertWavToFloat32(ms.ToArray());
    }

    /// <summary>
    /// Chunking fixe de 8s sans overlap — fallback si VAD non disponible.
    /// </summary>
    private static IReadOnlyList<VadSegment> FallbackChunks(float[] samples, int sampleRate = 16000)
    {
        const int chunkSamples = 8 * 16000;
        var result = new List<VadSegment>();
        int offset = 0;
        while (offset < samples.Length)
        {
            int len = Math.Min(chunkSamples, samples.Length - offset);
            float[] chunk = samples[offset..(offset + len)];
            result.Add(new VadSegment(
                offset / (float)sampleRate,
                (offset + len) / (float)sampleRate,
                chunk));
            offset += chunkSamples;
        }
        return result;
    }

    /// <summary>
    /// Convertit un buffer WAV PCM 16-bit signé en samples float32 normalisés [-1, 1].
    /// </summary>
    private static float[] ConvertWavToFloat32(byte[] wavData)
    {
        if (wavData.Length < 8) return Array.Empty<float>();

        int dataOffset = 44; // standard PCM WAV header size

        // Scan for "data" chunk marker
        for (int i = 0; i < Math.Min(wavData.Length - 8, 512); i++)
        {
            if (wavData[i] == 'd' && wavData[i + 1] == 'a' && wavData[i + 2] == 't' && wavData[i + 3] == 'a')
            {
                dataOffset = i + 8;
                break;
            }
        }

        int nSamples = (wavData.Length - dataOffset) / 2;
        if (nSamples <= 0) return Array.Empty<float>();

        var samples = new float[nSamples];
        for (int i = 0; i < nSamples; i++)
            samples[i] = BitConverter.ToInt16(wavData, dataOffset + i * 2) / 32768.0f;

        return samples;
    }

    public Task<string> DetectLanguageAsync(byte[] audioData) => Task.FromResult("en");

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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _preprocessor?.Dispose();
        _encoder?.Dispose();
        _decoder?.Dispose();
    }
}
