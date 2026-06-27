using System.Diagnostics;
using Microsoft.Extensions.Logging;
using VoxMind.Chatterbox;
using VoxMind.Core.Audio;
using VoxMind.Core.Configuration;
using VoxMind.Core.Transcription;

namespace VoxMind.Core.Tts;

/// <summary>
/// Implémentation d'<see cref="ITtsService"/> basée sur Chatterbox multilingue ONNX
/// (<c>onnx-community/chatterbox-multilingual-ONNX</c>), voice cloning zero-shot.
///
/// Calque de <see cref="F5TtsOnnxService"/> : résolution de langue, cache LRU des
/// pipelines, audio de référence par défaut configurable. Contrairement à F5 (un
/// checkpoint par langue), Chatterbox est multilingue — le jeu de modèles est
/// partagé entre langues et n'est chargé qu'une fois (le <c>conditional_decoder</c>
/// met ~78 s à charger), d'où un cache indexé par dossier de modèle.
///
/// Les modèles ONNX ne sont pas embarqués dans le repo : voir la config
/// <see cref="TtsConfig.ChatterboxLanguages"/> pour les chemins attendus.
/// </summary>
public sealed class ChatterboxTtsService : ITtsService
{
    private const int SampleRate = 24000;

    private readonly TtsConfig _config;
    private readonly ILogger<ChatterboxTtsService> _logger;
    private readonly LruEngineCache<ChatterboxPipeline> _cache;
    private readonly TtsModelInfo _info;

    public TtsModelInfo Info
    {
        get
        {
            _info.ResidentLanguages = _cache.ResidentKeys;
            return _info;
        }
    }

    public ChatterboxTtsService(TtsConfig config, ILogger<ChatterboxTtsService> logger)
    {
        _config = config;
        _logger = logger;
        _cache = new LruEngineCache<ChatterboxPipeline>(config.CacheCapacity);

        var available = config.ChatterboxLanguages
            .Where(kv => CheckpointExists(kv.Value))
            .Select(kv => kv.Key)
            .ToArray();

        _info = new TtsModelInfo
        {
            EngineName = "chatterbox",
            Backend = ComputeBackend.CPU,
            IsLoaded = available.Length > 0,
            AvailableLanguages = available,
        };

        if (available.Length == 0)
        {
            _logger.LogWarning(
                "Chatterbox : aucun jeu de modèles ONNX trouvé ({N} langue(s) déclarée(s)). " +
                "Synthèse désactivée — voir TtsConfig.ChatterboxLanguages pour les chemins attendus.",
                config.ChatterboxLanguages.Count);
        }
        else
        {
            _logger.LogInformation(
                "Chatterbox : {N} langue(s) disponible(s) à la demande : {Langs}.",
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
        if (!_config.ChatterboxLanguages.TryGetValue(lang, out var checkpoint))
            throw new NotSupportedException(
                $"Chatterbox : langue '{lang}' non configurée. Langues disponibles : {string.Join(", ", _info.AvailableLanguages)}.");

        if (!CheckpointExists(checkpoint))
            throw new NotSupportedException(
                $"Chatterbox : modèle pour '{lang}' introuvable sur disque ({checkpoint.SpeechEncoderModelPath}).");

        var sw = Stopwatch.StartNew();

        // Chatterbox multilingue : un seul jeu de modèles sert toutes les langues. On cache
        // par dossier de modèle (et non par langue) pour ne charger qu'une fois (decoder ~78 s).
        var modelKey = Path.GetDirectoryName(checkpoint.SpeechEncoderModelPath) ?? lang;
        var pipeline = _cache.GetOrLoad(modelKey, () =>
        {
            _logger.LogInformation(
                "Chatterbox : chargement du pipeline ONNX depuis {Dir} (variant {Variant}) — peut prendre ~78 s…",
                modelKey, checkpoint.LmVariant);
            return new ChatterboxPipeline(modelKey, checkpoint.TokenizerPath, checkpoint.LmVariant);
        });

        // Audio de référence (voice cloning) : fourni, sinon défaut configuré pour la langue.
        // Le speech_encoder attend du float mono 24 kHz.
        float[] refPcm;
        if (referenceWav is not null)
        {
            refPcm = WavReader.ReadMono(referenceWav, SampleRate);
        }
        else
        {
            if (!File.Exists(checkpoint.DefaultReferenceWav))
                throw new FileNotFoundException(
                    $"Voix de référence par défaut introuvable : {checkpoint.DefaultReferenceWav}.",
                    checkpoint.DefaultReferenceWav);
            var raw = await File.ReadAllBytesAsync(checkpoint.DefaultReferenceWav, ct).ConfigureAwait(false);
            refPcm = WavReader.ReadMono(raw, SampleRate);
        }

        // Inférence (CPU-bound) — déléguée au thread pool pour ne pas bloquer le request thread.
        var pcm = await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            return pipeline.Generate(text, lang, refPcm, checkpoint.Exaggeration);
        }, ct).ConfigureAwait(false);

        sw.Stop();
        _logger.LogInformation(
            "Chatterbox synthèse {Lang} : {Chars} char → {Samples} samples ({Duration:F2}s) en {Latency} ms.",
            lang, text.Length, pcm.Length, pcm.Length / (double)SampleRate, sw.ElapsedMilliseconds);

        return new TtsResult
        {
            Pcm = pcm,
            SampleRate = SampleRate,
            Language = lang,
            SynthesisLatency = sw.Elapsed,
        };
    }

    private string ResolveLanguage(string? requested)
    {
        if (!string.IsNullOrWhiteSpace(requested) && _config.ChatterboxLanguages.ContainsKey(requested))
            return requested;
        if (!string.IsNullOrWhiteSpace(requested))
        {
            _logger.LogWarning(
                "Chatterbox : langue '{Req}' non disponible, fallback sur '{Default}'.",
                requested, _config.DefaultLanguage);
        }
        return _config.DefaultLanguage;
    }

    private static bool CheckpointExists(ChatterboxLanguageCheckpoint c)
        => File.Exists(c.SpeechEncoderModelPath)
        && File.Exists(c.EmbedTokensModelPath)
        && File.Exists(c.LanguageModelPath)
        && File.Exists(c.ConditionalDecoderModelPath)
        && File.Exists(c.TokenizerPath);

    public void Dispose() => _cache.Dispose();
}
