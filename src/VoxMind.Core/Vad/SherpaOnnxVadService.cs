using Microsoft.Extensions.Logging;
using SherpaOnnx;
using VoxMind.Core.Configuration;

namespace VoxMind.Core.Vad;

/// <summary>
/// Implémentation VAD via sherpa-onnx VoiceActivityDetector + modèle silero_vad.onnx.
/// Détecte les segments de parole dans un flux PCM float32 16kHz.
/// </summary>
public class SherpaOnnxVadService : IVadService
{
    private readonly VadModelConfig _config;
    private readonly ILogger<SherpaOnnxVadService> _logger;
    private readonly float _maxSegmentSeconds;

    public bool IsAvailable { get; }

    public SherpaOnnxVadService(VadConfig config, ILogger<SherpaOnnxVadService> logger)
    {
        _logger = logger;
        _maxSegmentSeconds = config.MaxSegmentDurationSeconds;
        _config = new VadModelConfig(); // init par défaut (struct)

        if (!File.Exists(config.ModelPath))
        {
            _logger.LogWarning("Modèle Silero VAD introuvable : {Path}. VAD désactivé.", config.ModelPath);
            IsAvailable = false;
            return;
        }

        _config.SileroVad.Model             = config.ModelPath;
        _config.SileroVad.Threshold         = config.Threshold;
        _config.SileroVad.MinSilenceDuration = config.MinSilenceDurationSeconds;
        _config.SileroVad.MinSpeechDuration  = config.MinSpeechDurationSeconds;
        _config.SileroVad.WindowSize         = 512;
        _config.SampleRate                   = 16000;
        _config.NumThreads                   = 1;

        IsAvailable = true;
        _logger.LogInformation("Silero VAD chargé depuis {Path}.", config.ModelPath);
    }

    public IReadOnlyList<VadSegment> DetectSpeech(float[] samples, int sampleRate = 16000)
    {
        if (!IsAvailable)
            return Array.Empty<VadSegment>();

        float bufferSecs = Math.Max(120f, samples.Length / (float)sampleRate + 30f);
        using var vad = new VoiceActivityDetector(_config, bufferSizeInSeconds: bufferSecs);

        // Envoyer en chunks de WindowSize pour un comportement identique au mode streaming
        int windowSize = _config.SileroVad.WindowSize > 0 ? _config.SileroVad.WindowSize : 512;
        int offset = 0;
        while (offset + windowSize <= samples.Length)
        {
            vad.AcceptWaveform(samples[offset..(offset + windowSize)]);
            offset += windowSize;
        }
        // Envoyer le reste
        if (offset < samples.Length)
            vad.AcceptWaveform(samples[offset..]);
        vad.Flush();

        var rawSegments = new List<(float start, float dur, float[] s)>();
        while (!vad.IsEmpty())
        {
            var seg = vad.Front();
            vad.Pop();
            float startSec = seg.Start / (float)sampleRate;
            float duration = seg.Samples.Length / (float)sampleRate;
            rawSegments.Add((startSec, duration, seg.Samples));
        }

        var segments = new List<VadSegment>();
        foreach (var (startSec, duration, rawSamples) in rawSegments)
        {
            float endSec = startSec + duration;
            if (duration <= _maxSegmentSeconds)
            {
                segments.Add(new VadSegment(startSec, endSec, rawSamples));
            }
            else
            {
                int chunkSamples = (int)(_maxSegmentSeconds * sampleRate);
                int chunkOffset = 0;
                while (chunkOffset < rawSamples.Length)
                {
                    int len          = Math.Min(chunkSamples, rawSamples.Length - chunkOffset);
                    float[] chunk    = rawSamples[chunkOffset..(chunkOffset + len)];
                    float chunkStart = startSec + chunkOffset / (float)sampleRate;
                    float chunkEnd   = chunkStart + len / (float)sampleRate;
                    segments.Add(new VadSegment(chunkStart, chunkEnd, chunk));
                    chunkOffset += len;
                }
            }
        }

        _logger.LogDebug("VAD : {Count} segments sur {TotalSec:F1}s d'audio.",
            segments.Count, samples.Length / (float)sampleRate);

        return segments;
    }
}
