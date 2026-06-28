using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
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
        CancellationToken ct = default)
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

        var cleaned = CleanText(text);
        if (string.IsNullOrWhiteSpace(cleaned))
            cleaned = text.Trim();

        var sw = Stopwatch.StartNew();

        var pcm = await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var engine = GetEngine(voice);
            // L'API native n'est pas garantie réentrante : on sérialise les générations.
            lock (_genLock)
            {
                // OfflineTtsGeneratedAudio expose Dispose() mais n'implémente pas IDisposable :
                // on libère le buffer natif explicitement en finally.
                var audio = engine.Generate(cleaned, voice.Speed, voice.SpeakerId);
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
        }, ct).ConfigureAwait(false);

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

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Nettoyage du texte : retire emojis et balisage markdown avant la synthèse (les réponses LLM
    // sont truffées de markdown/emojis qui parasitent la phonémisation). Kokoro/sherpa découpe
    // ensuite le texte en phrases en interne, donc on passe le texte nettoyé entier.
    // ─────────────────────────────────────────────────────────────────────────────────────────
    internal static string CleanText(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        text = RemoveEmojis(text);

        // Code (blocs ``` puis inline `…`).
        text = Regex.Replace(text, "```[^\n]*", "");
        text = Regex.Replace(text, "`([^`]*)`", "$1");

        // Images puis liens markdown : ne garder que le libellé.
        text = Regex.Replace(text, @"!\[([^\]]*)\]\([^)]*\)", "$1");
        text = Regex.Replace(text, @"\[([^\]]*)\]\([^)]*\)", "$1");

        // Préfixes de ligne (titres, citations, règles, puces).
        text = Regex.Replace(text, @"(?m)^[ \t]{0,3}#{1,6}[ \t]*", "");
        text = Regex.Replace(text, @"(?m)^[ \t]{0,3}>+[ \t]?", "");
        text = Regex.Replace(text, @"(?m)^[ \t]{0,3}([-*_])([ \t]*\1){2,}[ \t]*$", "");
        text = Regex.Replace(text, @"(?m)^[ \t]{0,3}[-*+][ \t]+", "");

        // Emphase & barré.
        text = Regex.Replace(text, @"\*\*([^*]+)\*\*", "$1");
        text = Regex.Replace(text, @"__([^_]+)__", "$1");
        text = Regex.Replace(text, @"~~([^~]+)~~", "$1");
        text = Regex.Replace(text, @"\*([^*\n]+)\*", "$1");
        text = Regex.Replace(text, @"(?<![A-Za-z0-9])_([^_\n]+)_(?![A-Za-z0-9])", "$1");

        // Retours à la ligne → frontières de phrase.
        var sb = new StringBuilder();
        foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (sb.Length > 0)
            {
                char last = sb[sb.Length - 1];
                sb.Append(".!?…:;".IndexOf(last) >= 0 ? " " : ". ");
            }
            sb.Append(line);
        }
        text = sb.ToString();

        // Normalisation espaces / ponctuation.
        text = Regex.Replace(text, @"[ \t]+", " ");
        text = Regex.Replace(text, @"\s+([.,!?…;:])", "$1");
        text = Regex.Replace(text, @"([!?])\1+", "$1");
        text = Regex.Replace(text, @"\.{2,}", ".");
        return text.Trim();
    }

    /// <summary>Retire emojis/pictogrammes par plage de code-point (insensible à l'encodage du source).</summary>
    private static string RemoveEmojis(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            int cp = rune.Value;
            bool pictograph =
                cp >= 0x10000 ||
                (cp >= 0x2190 && cp <= 0x21FF) ||
                (cp >= 0x2300 && cp <= 0x23FF) ||
                (cp >= 0x25A0 && cp <= 0x25FF) ||
                (cp >= 0x2600 && cp <= 0x27BF) ||
                (cp >= 0x2B00 && cp <= 0x2BFF) ||
                (cp >= 0xFE00 && cp <= 0xFE0F) ||
                cp == 0x200D || cp == 0x20E3 || cp == 0x2122 || cp == 0x2139;
            if (!pictograph) sb.Append(rune.ToString());
        }
        return sb.ToString();
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
