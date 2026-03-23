using Microsoft.Extensions.Logging;
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
    private ModelInfo _info;
    private AudioPreprocessor? _preprocessor;
    private ParakeetEncoder? _encoder;
    private ParakeetDecoderJoint? _decoder;
    private TokenDecoder? _tokenDecoder;
    private bool _disposed;

    public ModelInfo Info => _info;

    public ParakeetOnnxTranscriptionService(string modelDir, ILogger<ParakeetOnnxTranscriptionService> logger)
    {
        _logger = logger;
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

    public async Task<TranscriptionResult> TranscribeChunkAsync(byte[] audioData, CancellationToken ct = default)
    {
        if (_preprocessor is null || _encoder is null || _decoder is null || _tokenDecoder is null)
            return new TranscriptionResult { Text = string.Empty };

        try
        {
            return await Task.Run(() => RunInference(audioData), ct);
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

    private TranscriptionResult RunInference(byte[] audioData)
    {
        float[] samples = ConvertWavToFloat32(audioData);
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

    public async Task<TranscriptionResult> TranscribeFileAsync(string filePath, CancellationToken ct = default)
    {
        var audioData = await File.ReadAllBytesAsync(filePath, ct);
        return await TranscribeChunkAsync(audioData, ct);
    }

    public Task<string> DetectLanguageAsync(byte[] audioData) => Task.FromResult("en");

    public Task LoadModelAsync(ModelSize size, ComputeBackend backend = ComputeBackend.Auto)
    {
        // Size/Backend are init-only; recreate ModelInfo to apply new values
        _info = new ModelInfo
        {
            ModelName = _info.ModelName,
            Size = size,
            Backend = backend == ComputeBackend.Auto ? ComputeBackend.CPU : backend,
            IsLoaded = _info.IsLoaded
        };
        return Task.CompletedTask;
    }

    /// <summary>
    /// Convert WAV PCM 16-bit bytes to normalized float32 samples.
    /// Parses WAV header to find data chunk; falls back to offset 44 for standard PCM WAV.
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
                dataOffset = i + 8; // "data" (4 bytes) + chunk size (4 bytes)
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _preprocessor?.Dispose();
        _encoder?.Dispose();
        _decoder?.Dispose();
    }
}
