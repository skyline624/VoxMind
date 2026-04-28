using System.Diagnostics;
using Microsoft.Extensions.Logging;
using VoxMind.Core.Configuration;
using VoxMind.Core.Transcription;

namespace VoxMind.Core.Tts;

/// <summary>
/// Implémentation principale d'<see cref="ITtsService"/> basée sur F5-TTS-ONNX.
///
/// Charge un moteur F5 par langue à la demande (cache LRU). Les checkpoints
/// physiques ne sont pas embarqués dans le repo : voir <c>docs/F5TtsExport.md</c>
/// pour le pipeline d'export depuis les fine-tunes communautaires
/// (RASPIAUDIO/F5-French-MixedSpeakers-reduced pour FR, base SWivid pour EN).
/// </summary>
public sealed class F5TtsOnnxService : ITtsService
{
    private readonly TtsConfig _config;
    private readonly ILogger<F5TtsOnnxService> _logger;
    private readonly LruEngineCache<F5LanguageEngine> _cache;
    private readonly TtsModelInfo _info;

    public TtsModelInfo Info
    {
        get
        {
            _info.ResidentLanguages = _cache.ResidentKeys;
            return _info;
        }
    }

    public F5TtsOnnxService(TtsConfig config, ILogger<F5TtsOnnxService> logger)
    {
        _config = config;
        _logger = logger;
        _cache = new LruEngineCache<F5LanguageEngine>(config.CacheCapacity);

        var available = config.Languages
            .Where(kv => CheckpointExists(kv.Value))
            .Select(kv => kv.Key)
            .ToArray();

        _info = new TtsModelInfo
        {
            EngineName = "f5-tts-onnx",
            Backend = ComputeBackend.CPU,
            IsLoaded = available.Length > 0,
            AvailableLanguages = available,
        };

        if (available.Length == 0)
        {
            _logger.LogWarning(
                "F5-TTS : aucun checkpoint trouvé dans la configuration ({N} langue(s) déclarée(s)). " +
                "Synthèse désactivée — voir docs/F5TtsExport.md pour la procédure d'export.",
                config.Languages.Count);
        }
        else
        {
            _logger.LogInformation(
                "F5-TTS : {N} langue(s) disponible(s) à la demande : {Langs}.",
                available.Length, string.Join(", ", available));
        }
    }

    public async Task<TtsResult> SynthesizeAsync(
        string text,
        string? language = null,
        byte[]? referenceWav = null,
        string? referenceText = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Le texte à synthétiser est vide.", nameof(text));

        var lang = ResolveLanguage(language);
        if (!_config.Languages.TryGetValue(lang, out var checkpoint))
            throw new NotSupportedException(
                $"F5-TTS : langue '{lang}' non configurée. Langues disponibles : {string.Join(", ", _info.AvailableLanguages)}.");

        if (!CheckpointExists(checkpoint))
            throw new NotSupportedException(
                $"F5-TTS : checkpoint pour '{lang}' introuvable sur disque ({checkpoint.PreprocessModelPath}). " +
                $"Voir docs/F5TtsExport.md pour l'exporter.");

        var sw = Stopwatch.StartNew();
        var engine = _cache.GetOrLoad(lang, () => new F5LanguageEngine(checkpoint, _logger));

        // Audio de référence : soit fourni, soit défaut configuré pour la langue
        float[] refPcm;
        string refText;
        if (referenceWav is not null)
        {
            refPcm = ReadWavTo24kHzMono(referenceWav);
            refText = referenceText ?? throw new ArgumentException(
                "F5-TTS exige une transcription du voice prompt fourni (referenceText null).",
                nameof(referenceText));
        }
        else
        {
            if (!File.Exists(engine.DefaultReferenceWav))
                throw new FileNotFoundException(
                    $"Voice de référence par défaut introuvable : {engine.DefaultReferenceWav}.",
                    engine.DefaultReferenceWav);
            var raw = await File.ReadAllBytesAsync(engine.DefaultReferenceWav, ct).ConfigureAwait(false);
            refPcm = ReadWavTo24kHzMono(raw);
            refText = engine.DefaultReferenceText;
        }

        var promptIds = engine.Tokenizer.Encode(refText);
        var targetIds = engine.Tokenizer.Encode(text);

        // Inférence (CPU-bound) — déléguée au thread pool pour ne pas bloquer le request thread
        var pcm = await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var conditioning = engine.Preprocessor.Run(refPcm, promptIds, targetIds);
            var mel = engine.Transformer.Sample(conditioning, _config.FlowMatchingSteps);
            return engine.Decoder.Decode(mel);
        }, ct).ConfigureAwait(false);

        sw.Stop();
        _logger.LogInformation(
            "F5-TTS synthèse {Lang} : {Chars} char → {Samples} samples ({Duration:F2}s) en {Latency} ms.",
            lang, text.Length, pcm.Length, pcm.Length / 24000.0, sw.ElapsedMilliseconds);

        return new TtsResult
        {
            Pcm = pcm,
            SampleRate = 24000,
            Language = lang,
            SynthesisLatency = sw.Elapsed,
        };
    }

    private string ResolveLanguage(string? requested)
    {
        if (!string.IsNullOrWhiteSpace(requested) && _config.Languages.ContainsKey(requested))
            return requested;
        if (!string.IsNullOrWhiteSpace(requested))
        {
            _logger.LogWarning(
                "F5-TTS : langue '{Req}' non disponible, fallback sur '{Default}'.",
                requested, _config.DefaultLanguage);
        }
        return _config.DefaultLanguage;
    }

    private static bool CheckpointExists(F5LanguageCheckpoint c)
        => File.Exists(c.PreprocessModelPath)
        && File.Exists(c.TransformerModelPath)
        && File.Exists(c.DecodeModelPath)
        && File.Exists(c.TokensPath);

    /// <summary>
    /// Lit un buffer WAV (PCM 16 bits) en samples float32 mono normalisés [-1, 1].
    /// Si le WAV n'est pas en 24 kHz mono, on rééchantillonne grossièrement par
    /// nearest-neighbor — suffisant pour des voice prompts courts ; à raffiner avec
    /// un resampler propre (linear ou sinc) si la qualité du cloning en pâtit.
    /// </summary>
    private static float[] ReadWavTo24kHzMono(byte[] wav)
    {
        if (wav.Length < 44) return Array.Empty<float>();

        int sampleRate = BitConverter.ToInt32(wav, 24);
        short channels = BitConverter.ToInt16(wav, 22);
        short bitsPerSample = BitConverter.ToInt16(wav, 34);

        // Locate "data" chunk
        int dataOffset = 44;
        for (int i = 12; i < Math.Min(wav.Length - 8, 256); i++)
        {
            if (wav[i] == 'd' && wav[i + 1] == 'a' && wav[i + 2] == 't' && wav[i + 3] == 'a')
            {
                dataOffset = i + 8;
                break;
            }
        }

        if (bitsPerSample != 16)
            throw new NotSupportedException($"WAV : seul PCM 16 bits supporté, reçu {bitsPerSample}.");

        int bytesPerFrame = channels * 2;
        int totalFrames = (wav.Length - dataOffset) / bytesPerFrame;
        if (totalFrames <= 0) return Array.Empty<float>();

        // 1. Décodage int16 → float mono (mixage si stéréo)
        var mono = new float[totalFrames];
        for (int i = 0; i < totalFrames; i++)
        {
            int sum = 0;
            for (int c = 0; c < channels; c++)
                sum += BitConverter.ToInt16(wav, dataOffset + i * bytesPerFrame + c * 2);
            mono[i] = (sum / channels) / 32768.0f;
        }

        if (sampleRate == 24000) return mono;

        // 2. Resample naïf vers 24 kHz (nearest neighbor — à améliorer si besoin)
        int targetLen = (int)((long)mono.Length * 24000 / sampleRate);
        var resampled = new float[targetLen];
        for (int i = 0; i < targetLen; i++)
        {
            int src = (int)((long)i * sampleRate / 24000);
            if (src >= mono.Length) src = mono.Length - 1;
            resampled[i] = mono[src];
        }
        return resampled;
    }

    public void Dispose() => _cache.Dispose();
}
