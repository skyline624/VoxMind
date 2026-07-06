using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using SherpaOnnx;
using VoxMind.Core.Configuration;
using VoxMind.Core.Transcription;

namespace VoxMind.Core.Tts;

/// <summary>
/// Implémentation d'<see cref="ITtsService"/> basée sur <b>Kokoro</b> (modèle TTS 82M
/// non-autorégressif, style-based) servi via <c>sherpa-onnx</c> (<see cref="OfflineTts"/>).
///
/// Contrairement à Chatterbox/F5 (autorégressifs, lents), Kokoro est non-autorégressif :
/// une seule passe ONNX par phrase → RTF très bas (~0.05 sur CPU), idéal pour le conteneur
/// CPU sans GPU. Pas de voice cloning : la voix est une voix prédéfinie du modèle
/// (FR féminine <c>ff_siwis</c>, speaker id 30 dans <c>kokoro-multi-lang-v1_0</c>).
///
/// La phonémisation passe par espeak-ng (dossier <c>DataDir</c>). Pour le français on
/// force <c>Lang="fr"</c> (la meta du modèle vaut <c>en-us</c> par défaut) et on laisse
/// le lexique vide pour que TOUS les mots soient phonémisés en français par espeak.
///
/// <para><see cref="OfflineTts"/> est mis en cache par voix espeak (un par langue) et chargé
/// à la demande ; le chargement Kokoro est rapide (~1–2 s).</para>
/// </summary>
public sealed class KokoroTtsService : ITtsService
{
    private const int SampleRate = 24000;   // Kokoro génère du 24 kHz mono.

    private readonly KokoroConfig _config;
    private readonly ILogger<KokoroTtsService> _logger;
    private readonly TtsModelInfo _info;

    // Un OfflineTts par voix espeak (Lang est global au modèle chargé). Pour le FR seul,
    // il n'y en a qu'un. Chargé paresseusement et réutilisé (thread-safe).
    private readonly ConcurrentDictionary<string, Lazy<OfflineTts>> _engines = new();
    private readonly object _genLock = new();   // sérialise Generate (l'API native n'est pas garantie réentrante)
    private bool _disposed;

    public TtsModelInfo Info => _info;

    public KokoroTtsService(KokoroConfig config, ILogger<KokoroTtsService> logger)
    {
        _config = config;
        _logger = logger;

        // On n'expose QUE les langues dont la voix espeak existe réellement dans DataDir/lang :
        // passer une voix espeak inconnue à sherpa fait planter le PROCESS natif (std::terminate,
        // « Failed to set eSpeak-ng voice ») — non rattrapable côté managé. On filtre donc en amont.
        var modelOk = ModelFilesExist();
        var available = config.Voices
            .Where(kv => modelOk && EspeakVoiceAvailable(config.Voices[kv.Key].EspeakVoice))
            .Select(kv => kv.Key)
            .ToArray();

        var rejected = config.Voices.Keys.Except(available).ToArray();

        _info = new TtsModelInfo
        {
            EngineName = "kokoro",
            Backend = ComputeBackend.CPU,
            IsLoaded = available.Length > 0,
            AvailableLanguages = available,
        };

        if (!modelOk)
        {
            _logger.LogWarning(
                "Kokoro : fichiers modèle introuvables (model={Model}, voices={Voices}, tokens={Tokens}, dataDir={DataDir}). " +
                "Synthèse désactivée — voir TtsConfig.Kokoro pour les chemins attendus.",
                config.ModelPath, config.VoicesPath, config.TokensPath, config.DataDir);
        }
        else
        {
            _logger.LogInformation(
                "Kokoro : {N} langue(s) disponible(s) ({Langs}), modèle {Model}.",
                available.Length, string.Join(", ", available), config.ModelPath);
            if (rejected.Length > 0)
                _logger.LogWarning(
                    "Kokoro : {N} langue(s) IGNORÉE(S) (voix espeak introuvable dans {DataDir}/lang) : {Langs}.",
                    rejected.Length, config.DataDir, string.Join(", ", rejected));
        }
    }

    public async Task<TtsResult> SynthesizeAsync(
        string text,
        string? language = null,
        byte[]? referenceWav = null,        // ignoré : Kokoro ne fait pas de voice cloning
        string? referenceText = null,       // ignoré
        string? instructions = null,        // ignoré : Kokoro n'a pas de contrôle d'émotion
        string? requestedVoice = null,      // ignoré : voix Kokoro fixée par la langue
        CancellationToken ct = default)
    {
        var (lang, voice, cleaned) = PrepareSynthesis(text, language);

        var sw = Stopwatch.StartNew();
        var pcm = await Task.Run(() => GenerateOne(voice, cleaned, ct), ct).ConfigureAwait(false);
        sw.Stop();

        double durationSec = pcm.Length / (double)SampleRate;
        double rtf = durationSec > 0 ? sw.Elapsed.TotalSeconds / durationSec : 0;
        _logger.LogInformation(
            "Kokoro synthèse {Lang} (voix {Voice}, sid {Sid}) : {Chars} char → {Samples} samples " +
            "({Duration:F2}s) en {Latency} ms (RTF {Rtf:F3}).",
            lang, voice.EspeakVoice, voice.SpeakerId, cleaned.Length, pcm.Length,
            durationSec, sw.ElapsedMilliseconds, rtf);

        return new TtsResult
        {
            Pcm = pcm,
            SampleRate = SampleRate,
            Language = lang,
            SynthesisLatency = sw.Elapsed,
        };
    }

    /// <summary>
    /// Variante streaming : Kokoro étant non-autorégressif (une passe ONNX par phrase), on synthétise et on
    /// émet <b>phrase par phrase</b>. Le premier son part dès la 1ʳᵉ phrase au lieu d'attendre toute la
    /// réponse ; l'endpoint pousse chaque segment en chunked transfer. Le RTF Kokoro étant ~0.05 sur CPU,
    /// la synthèse devance largement la lecture côté client → enchaînement sans blanc.
    /// </summary>
    public async IAsyncEnumerable<TtsResult> SynthesizeStreamAsync(
        string text,
        string? language = null,
        string? instructions = null,        // ignoré : Kokoro n'a pas de contrôle d'émotion
        string? requestedVoice = null,      // ignoré : voix Kokoro fixée par la langue
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var (lang, voice, cleaned) = PrepareSynthesis(text, language);

        var sw = Stopwatch.StartNew();
        int sentences = 0;
        long totalSamples = 0;

        foreach (var sentence in TtsTextSegmenter.SplitSentences(cleaned))
        {
            ct.ThrowIfCancellationRequested();
            var pcm = await Task.Run(() => GenerateOne(voice, sentence, ct), ct).ConfigureAwait(false);
            if (pcm.Length == 0)
                continue;

            sentences++;
            totalSamples += pcm.Length;
            yield return new TtsResult
            {
                Pcm = pcm,
                SampleRate = SampleRate,
                Language = lang,
                SynthesisLatency = sw.Elapsed,
            };
        }

        sw.Stop();
        double durationSec = totalSamples / (double)SampleRate;
        double rtf = durationSec > 0 ? sw.Elapsed.TotalSeconds / durationSec : 0;
        _logger.LogInformation(
            "Kokoro synthèse streaming {Lang} (voix {Voice}, sid {Sid}) : {Chars} char → {Sentences} segment(s), " +
            "{Samples} samples ({Duration:F2}s) en {Latency} ms (RTF {Rtf:F3}).",
            lang, voice.EspeakVoice, voice.SpeakerId, cleaned.Length, sentences, totalSamples,
            durationSec, sw.ElapsedMilliseconds, rtf);
    }

    /// <summary>
    /// Valide la requête (texte non vide, modèle présent, voix espeak disponible), résout la langue et la
    /// voix, et nettoie le texte. Factorisé entre la synthèse bufferisée et la synthèse streaming.
    /// </summary>
    private (string Lang, KokoroVoice Voice, string Cleaned) PrepareSynthesis(string text, string? language)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Le texte à synthétiser est vide.", nameof(text));

        if (!ModelFilesExist())
            throw new NotSupportedException(
                $"Kokoro : modèle introuvable sur disque ({_config.ModelPath}).");

        var lang = ResolveLanguage(language);
        var voice = _config.Voices[lang];

        // Garde-fou : une voix espeak inconnue ferait planter le process natif (cf. constructeur).
        if (!EspeakVoiceAvailable(voice.EspeakVoice))
            throw new NotSupportedException(
                $"Kokoro : voix espeak '{voice.EspeakVoice}' introuvable dans {_config.DataDir}/lang (langue '{lang}').");

        var cleaned = TtsTextSegmenter.CleanText(text);
        if (string.IsNullOrWhiteSpace(cleaned))
            cleaned = text.Trim();

        return (lang, voice, cleaned);
    }

    /// <summary>Synthétise un fragment de texte en PCM mono float32 (passe Generate native sérialisée).</summary>
    private float[] GenerateOne(KokoroVoice voice, string text, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var engine = GetEngine(voice);
        // L'API native n'est pas garantie réentrante : on sérialise les générations.
        lock (_genLock)
        {
            // OfflineTtsGeneratedAudio expose Dispose() mais n'implémente pas IDisposable :
            // on libère le buffer natif explicitement en finally.
            var audio = engine.Generate(text, voice.Speed, voice.SpeakerId);
            try
            {
                var samples = audio.Samples;
                var copy = new float[samples.Length];
                Array.Copy(samples, copy, samples.Length);
                return copy;
            }
            finally
            {
                audio.Dispose();
            }
        }
    }

    /// <summary>
    /// Charge (ou réutilise) l'<see cref="OfflineTts"/> Kokoro pour une voix donnée. Le lexique et le
    /// dossier dict sont GLOBAUX à l'instance native : le chinois (mandarin) requiert
    /// <c>lexicon-zh.txt</c> + le dict jieba (sinon les hanzi sont OOV → audio vide), tandis que les
    /// langues espeak-only utilisent un lexique vide. On cache donc une instance par triplet
    /// (voix espeak, lexique, dict).
    /// </summary>
    private OfflineTts GetEngine(KokoroVoice voice)
    {
        var lexicon = string.IsNullOrEmpty(voice.Lexicon) ? (_config.Lexicon ?? string.Empty) : voice.Lexicon;
        var dictDir = string.IsNullOrEmpty(voice.DictDir) ? (_config.DictDir ?? string.Empty) : voice.DictDir;
        var key = $"{voice.EspeakVoice}|{lexicon}|{dictDir}";
        return _engines.GetOrAdd(key, _ => new Lazy<OfflineTts>(
            () => BuildEngine(voice.EspeakVoice, lexicon, dictDir),
            LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    private OfflineTts BuildEngine(string espeakVoice, string lexicon, string dictDir)
    {
        _logger.LogInformation(
            "Kokoro : chargement du modèle (espeak '{Lang}', {Threads} threads, lexicon='{Lex}', dict='{Dict}')…",
            espeakVoice, _config.NumThreads, lexicon, dictDir);

        var config = new OfflineTtsConfig();
        config.Model.Kokoro.Model = _config.ModelPath;
        config.Model.Kokoro.Voices = _config.VoicesPath;
        config.Model.Kokoro.Tokens = _config.TokensPath;
        config.Model.Kokoro.DataDir = _config.DataDir;
        config.Model.Kokoro.DictDir = dictDir;
        config.Model.Kokoro.Lexicon = lexicon;
        config.Model.Kokoro.Lang = espeakVoice;
        config.Model.Kokoro.LengthScale = _config.LengthScale;
        config.Model.NumThreads = _config.NumThreads;
        config.Model.Provider = _config.Provider;
        config.Model.Debug = 0;

        var sw = Stopwatch.StartNew();
        var tts = new OfflineTts(config);
        sw.Stop();
        _logger.LogInformation(
            "Kokoro chargé en {Ms} ms ({Speakers} voix, {Rate} Hz).",
            sw.ElapsedMilliseconds, tts.NumSpeakers, tts.SampleRate);
        return tts;
    }

    private string ResolveLanguage(string? requested)
    {
        if (!string.IsNullOrWhiteSpace(requested) && _config.Voices.ContainsKey(requested))
            return requested;
        if (!string.IsNullOrWhiteSpace(requested) && !_config.Voices.ContainsKey(requested))
            _logger.LogWarning("Kokoro : langue '{Req}' non configurée, fallback sur '{Default}'.",
                requested, _config.DefaultLanguage);
        return _config.DefaultLanguage;
    }

    private bool ModelFilesExist()
        => File.Exists(_config.ModelPath)
        && File.Exists(_config.VoicesPath)
        && File.Exists(_config.TokensPath)
        && Directory.Exists(_config.DataDir);

    // Ensemble des identifiants de voix espeak-ng disponibles = noms de fichiers sous DataDir/lang
    // (ex. "fr", "en-US", "en-GB-x-rp", "pt-BR", "cmn"). espeak résout SetVoiceByName sur ce nom de
    // fichier (insensible à la casse), PAS sur les tags "language" internes — d'où la validation ici.
    private HashSet<string>? _espeakVoices;
    private bool EspeakVoiceAvailable(string espeakVoice)
    {
        if (string.IsNullOrWhiteSpace(espeakVoice)) return false;
        _espeakVoices ??= LoadEspeakVoiceNames(_config.DataDir);
        return _espeakVoices.Contains(espeakVoice);
    }

    private static HashSet<string> LoadEspeakVoiceNames(string dataDir)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var langDir = Path.Combine(dataDir, "lang");
            if (Directory.Exists(langDir))
                foreach (var f in Directory.EnumerateFiles(langDir, "*", SearchOption.AllDirectories))
                    set.Add(Path.GetFileName(f));
        }
        catch { /* best effort : en cas d'échec on laisse l'ensemble vide → langues ignorées proprement */ }
        return set;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var lazy in _engines.Values)
            if (lazy.IsValueCreated) lazy.Value.Dispose();
        _engines.Clear();
    }
}
