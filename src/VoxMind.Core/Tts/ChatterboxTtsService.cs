using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
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
                "Chatterbox : chargement du pipeline ONNX depuis {Dir} (variant {Variant}, device {Device}, sampling {Sampling}, temp {Temp}, topK {TopK}) — peut prendre ~78 s…",
                modelKey, checkpoint.LmVariant, checkpoint.Device, checkpoint.UseSampling, checkpoint.Temperature, checkpoint.TopK);
            return new ChatterboxPipeline(modelKey, checkpoint.TokenizerPath, checkpoint.LmVariant,
                                          checkpoint.Device, checkpoint.UseSampling,
                                          checkpoint.Temperature, checkpoint.TopK);
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

        // Nettoyage (emojis/markdown/normalisation) PUIS découpage en phrases : indispensable pour
        // éviter les bruits parasites et la dérive du modèle (mots inventés, autres langues, timbre
        // infidèle) sur les réponses LLM longues truffées de markdown/emojis. S'applique aux deux
        // modes (CPU greedy et GPU sampling).
        var cleaned = CleanText(text);
        if (string.IsNullOrWhiteSpace(cleaned))
            cleaned = text.Trim();   // garde-fou : si le nettoyage a tout retiré, on garde le brut
        var segments = SplitIntoSegments(cleaned);
        if (segments.Count == 0)
            segments.Add(cleaned);

        // Inférence (CPU-bound) — déléguée au thread pool pour ne pas bloquer le request thread.
        // Chaque segment est synthétisé séparément (même voix de référence, mêmes params) puis les
        // PCM sont concaténés avec un court silence pour reconstituer la réponse complète.
        var pcm = await Task.Run(() =>
        {
            const int gapSamples = 2880;   // ~0.12 s de silence à 24 kHz entre segments

            // Voix de référence encodée UNE seule fois (speech_encoder) et réutilisée pour tous les
            // segments — évite de relancer l'encodeur de locuteur à chaque segment.
            var reference = pipeline.EncodeReference(refPcm);

            var parts = new List<float[]>(segments.Count);
            long total = 0;
            for (int i = 0; i < segments.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                // Plafond de génération proportionnel à la longueur du segment : empêche la
                // sur-génération (traîne de tokens qui se décode en silence/parasites).
                int maxNew = Math.Clamp((int)(segments[i].Length * 1.6) + 32, 64, 400);
                var part = pipeline.Generate(segments[i], lang, reference, checkpoint.Exaggeration, maxNew);
                part = TrimSilence(part);   // rogne silences de tête/queue + compacte les blancs internes
                parts.Add(part);
                total += part.Length;
                if (i < segments.Count - 1) total += gapSamples;
            }
            var buf = new float[total];   // zéro-initialisé → les intervalles entre segments sont du silence
            int off = 0;
            for (int i = 0; i < parts.Count; i++)
            {
                Array.Copy(parts[i], 0, buf, off, parts[i].Length);
                off += parts[i].Length;
                if (i < parts.Count - 1) off += gapSamples;
            }
            return buf;
        }, ct).ConfigureAwait(false);

        sw.Stop();
        _logger.LogInformation(
            "Chatterbox synthèse {Lang} : {Chars} char nettoyés en {Segments} segment(s) → {Samples} samples ({Duration:F2}s) en {Latency} ms.",
            lang, cleaned.Length, segments.Count, pcm.Length, pcm.Length / (double)SampleRate, sw.ElapsedMilliseconds);

        return new TtsResult
        {
            Pcm = pcm,
            SampleRate = SampleRate,
            Language = lang,
            SynthesisLatency = sw.Elapsed,
        };
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Nettoyage du texte & découpage en phrases (préparation avant synthèse).
    // ─────────────────────────────────────────────────────────────────────────────────────────

    private const int MaxSegmentChars = 140;   // plafond STRICT par segment (cible ~120) ; au-delà : redécoupe sur , ; : puis par longueur
    private const int MinMergeChars   = 40;    // fusionner avec la phrase suivante seulement si la phrase courante est très courte

    /// <summary>
    /// Prépare le texte pour la synthèse : retire emojis et balisage markdown, puis normalise
    /// retours à la ligne, espaces et ponctuation. Appliqué AVANT le découpage en phrases.
    /// </summary>
    internal static string CleanText(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        // 1) Emojis & pictogrammes.
        text = RemoveEmojis(text);

        // 2) Code : blocs ``` (la clôture et l'éventuel langage sont retirés, le contenu conservé) puis code inline.
        text = Regex.Replace(text, "```[^\n]*", "");
        text = Regex.Replace(text, "`([^`]*)`", "$1");

        // 3) Images puis liens : on ne garde que le texte (alt / libellé).
        text = Regex.Replace(text, @"!\[([^\]]*)\]\([^)]*\)", "$1");
        text = Regex.Replace(text, @"\[([^\]]*)\]\([^)]*\)", "$1");

        // 4) Préfixes de ligne : titres #, citations >, règles horizontales, puces -/*/+.
        text = Regex.Replace(text, @"(?m)^[ \t]{0,3}#{1,6}[ \t]*", "");
        text = Regex.Replace(text, @"(?m)^[ \t]{0,3}>+[ \t]?", "");
        text = Regex.Replace(text, @"(?m)^[ \t]{0,3}([-*_])([ \t]*\1){2,}[ \t]*$", "");   // --- *** ___
        text = Regex.Replace(text, @"(?m)^[ \t]{0,3}[-*+][ \t]+", "");

        // 5) Emphase & barré.
        text = Regex.Replace(text, @"\*\*([^*]+)\*\*", "$1");
        text = Regex.Replace(text, @"__([^_]+)__", "$1");
        text = Regex.Replace(text, @"~~([^~]+)~~", "$1");
        text = Regex.Replace(text, @"\*([^*\n]+)\*", "$1");
        text = Regex.Replace(text, @"(?<![A-Za-z0-9])_([^_\n]+)_(?![A-Za-z0-9])", "$1");

        // 6) Retours à la ligne → frontières de phrase (". " ou simple espace si déjà ponctué).
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

        // 7) Normalisation : espaces multiples, espace avant ponctuation, ponctuation dédoublonnée.
        text = Regex.Replace(text, @"[ \t]+", " ");
        text = Regex.Replace(text, @"\s+([.,!?…;:])", "$1");
        text = Regex.Replace(text, @"([!?])\1+", "$1");
        text = Regex.Replace(text, @"\.{2,}", ".");
        return text.Trim();
    }

    /// <summary>
    /// Retire emojis et pictogrammes par plage de code-point (sans caractères Unicode littéraux
    /// dans le source, donc insensible à l'encodage du fichier) : tout le plan supplémentaire
    /// (paires de substitution, dont U+1F000–U+1FAFF) + plages BMP de symboles, flèches, dingbats,
    /// formes géométriques, sélecteurs de variante, ZWJ et combineur keycap.
    /// </summary>
    private static string RemoveEmojis(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            int cp = rune.Value;
            bool pictograph =
                cp >= 0x10000 ||                     // plan supplémentaire (emoji ≥ U+1F000, paires de substitution)
                (cp >= 0x2190 && cp <= 0x21FF) ||    // flèches
                (cp >= 0x2300 && cp <= 0x23FF) ||    // symboles techniques divers (⌚⏰⏳…)
                (cp >= 0x25A0 && cp <= 0x25FF) ||    // formes géométriques
                (cp >= 0x2600 && cp <= 0x27BF) ||    // symboles divers + dingbats
                (cp >= 0x2B00 && cp <= 0x2BFF) ||    // symboles & flèches divers
                (cp >= 0xFE00 && cp <= 0xFE0F) ||    // sélecteurs de variante
                cp == 0x200D || cp == 0x20E3 || cp == 0x2122 || cp == 0x2139;  // ZWJ, keycap, ™, ℹ
            if (!pictograph) sb.Append(rune.ToString());
        }
        return sb.ToString();
    }

    /// <summary>
    /// Découpe le texte nettoyé en segments synthétisables : <b>1 phrase par segment</b> (cible
    /// ~120 car., plafond strict ~140). Une phrase &gt; ~140 car. est redécoupée sur la ponctuation
    /// faible (<c>, ; :</c>) puis, en dernier recours, par longueur sur les espaces. Deux phrases ne
    /// sont fusionnées que si la première est très courte (&lt; ~40 car.). Ce découpage fin limite la
    /// dérive du modèle (sur-génération, code-switching) observée sur les segments longs.
    /// </summary>
    internal static List<string> SplitIntoSegments(string text)
    {
        // 1) Une phrase = un morceau ; une phrase trop longue est redécoupée sous le plafond strict.
        var pieces = new List<string>();
        foreach (var sentence in SplitSentences(text))
        {
            if (sentence.Length <= MaxSegmentChars) { pieces.Add(sentence); continue; }
            pieces.AddRange(SplitLongSentence(sentence));
        }

        // 2) Un segment = une phrase. On ne fusionne avec la phrase suivante QUE si la phrase courante
        //    est très courte (< MinMergeChars) ET que le résultat reste sous le plafond strict.
        var segments = new List<string>();
        var cur = new StringBuilder();
        foreach (var p in pieces)
        {
            if (cur.Length == 0) { cur.Append(p); continue; }
            if (cur.Length < MinMergeChars && cur.Length + 1 + p.Length <= MaxSegmentChars)
                cur.Append(' ').Append(p);
            else
            { segments.Add(cur.ToString()); cur.Clear(); cur.Append(p); }
        }
        if (cur.Length > 0) segments.Add(cur.ToString());
        return segments;
    }

    /// <summary>Découpe en phrases sur la ponctuation forte (<c>.!?…</c>), ponctuation incluse.</summary>
    private static IEnumerable<string> SplitSentences(string text)
    {
        var sentences = new List<string>();
        var sb = new StringBuilder();
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            sb.Append(c);
            if (c is '.' or '!' or '?' or '…')
            {
                // Absorber la ponctuation / les guillemets de clôture qui suivent immédiatement.
                while (i + 1 < text.Length &&
                       (text[i + 1] is '.' or '!' or '?' or '…' or '"' or '»' or ')' or ']' or '”'))
                    sb.Append(text[++i]);
                // Fin de phrase si on est en fin de texte ou suivi d'un espace.
                if (i + 1 >= text.Length || char.IsWhiteSpace(text[i + 1]))
                {
                    var s = sb.ToString().Trim();
                    if (s.Length > 0) sentences.Add(s);
                    sb.Clear();
                }
            }
        }
        var tail = sb.ToString().Trim();
        if (tail.Length > 0) sentences.Add(tail);
        return sentences;
    }

    /// <summary>Redécoupe une phrase trop longue : d'abord sur la ponctuation faible (<c>, ; :</c>,
    /// conservée en fin de morceau), sinon par longueur sur les espaces. Chaque morceau ≤ plafond.</summary>
    private static IEnumerable<string> SplitLongSentence(string sentence)
    {
        var result = new List<string>();
        var cur = new StringBuilder();
        foreach (var raw in Regex.Split(sentence, @"(?<=[,;:])\s+"))
        {
            var clause = raw.Trim();
            if (clause.Length == 0) continue;
            if (cur.Length == 0) cur.Append(clause);
            else if (cur.Length + 1 + clause.Length <= MaxSegmentChars) { cur.Append(' ').Append(clause); }
            else { result.Add(cur.ToString()); cur.Clear(); cur.Append(clause); }
        }
        if (cur.Length > 0) result.Add(cur.ToString());

        // Dernier recours : un morceau encore trop long est coupé par longueur sur les espaces.
        var final = new List<string>();
        foreach (var c in result)
        {
            if (c.Length <= MaxSegmentChars) { final.Add(c); continue; }
            final.AddRange(HardWrap(c, MaxSegmentChars));
        }
        return final;
    }

    /// <summary>Coupe une chaîne sur les espaces en morceaux d'au plus <paramref name="max"/> caractères.</summary>
    private static IEnumerable<string> HardWrap(string s, int max)
    {
        var cur = new StringBuilder();
        foreach (var w in s.Split(' '))
        {
            if (w.Length == 0) continue;
            if (cur.Length == 0) cur.Append(w);
            else if (cur.Length + 1 + w.Length <= max) { cur.Append(' ').Append(w); }
            else { yield return cur.ToString(); cur.Clear(); cur.Append(w); }
        }
        if (cur.Length > 0) yield return cur.ToString();
    }

    /// <summary>
    /// Rogne les silences de tête et de queue d'un segment PCM (gate RMS par fenêtre de ~25 ms, seuil
    /// ≈1.5 % du pic du segment) en gardant ~50 ms de marge, et compacte tout silence INTERNE &gt; ~0.4 s
    /// à ~0.15 s. Supprime les zones mortes générées par le modèle autour/entre les mots (le « trou »
    /// observé en fin de segment qui dérive). Appliqué aux deux modes (greedy CPU et sampling GPU).
    /// </summary>
    private static float[] TrimSilence(float[] pcm)
    {
        int n = pcm.Length;
        if (n == 0) return pcm;

        int win = SampleRate / 40;            // 25 ms
        int nwin = n / win;
        if (nwin == 0) return pcm;

        float peak = 0f;
        for (int i = 0; i < n; i++) { float a = pcm[i] < 0 ? -pcm[i] : pcm[i]; if (a > peak) peak = a; }
        if (peak <= 1e-5f) return Array.Empty<float>();   // segment entièrement silencieux
        float thr = Math.Max(0.006f, 0.015f * peak);      // seuil RMS (≈1.5 % du pic, plancher absolu)

        var voiced = new bool[nwin];
        for (int w = 0; w < nwin; w++)
        {
            int s = w * win;
            double acc = 0;
            for (int i = s; i < s + win; i++) acc += (double)pcm[i] * pcm[i];
            voiced[w] = Math.Sqrt(acc / win) >= thr;
        }

        int first = 0; while (first < nwin && !voiced[first]) first++;
        if (first == nwin) return Array.Empty<float>();
        int last = nwin - 1; while (last > first && !voiced[last]) last--;

        const int marginWin = 2;             // ~50 ms de marge conservés
        const int maxInternalSilWin = 16;    // 0.40 s : seuil de compaction d'un silence interne
        const int keepInternalSilWin = 6;    // 0.15 s : durée conservée pour un silence interne compacté

        var outBuf = new List<float>(n);
        void Append(int w) { int s = w * win; for (int i = s; i < s + win; i++) outBuf.Add(pcm[i]); }

        // Marge initiale (court silence avant la première fenêtre voisée).
        for (int w = Math.Max(0, first - marginWin); w < first; w++) Append(w);
        // Corps : fenêtres first..last, en compactant les longs silences internes.
        int wc = first;
        while (wc <= last)
        {
            if (voiced[wc]) { Append(wc); wc++; continue; }
            int run = wc; while (run <= last && !voiced[run]) run++;
            int silLen = run - wc;
            int keep = silLen > maxInternalSilWin ? keepInternalSilWin : silLen;
            for (int k = 0; k < keep; k++) Append(wc + k);
            wc = run;
        }
        // Marge finale (court silence après la dernière fenêtre voisée).
        for (int w = last + 1; w < Math.Min(nwin, last + 1 + marginWin); w++) Append(w);

        return outBuf.ToArray();
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
