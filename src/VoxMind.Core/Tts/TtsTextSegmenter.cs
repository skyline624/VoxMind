using System.Text;
using System.Text.RegularExpressions;

namespace VoxMind.Core.Tts;

/// <summary>
/// Helpers partagés de préparation de texte pour la synthèse vocale, communs aux moteurs autorégressifs
/// (Qwen3-TTS) et non-autorégressifs (Kokoro) :
/// <list type="bullet">
/// <item><see cref="CleanText"/> : retire emojis et balisage markdown (les réponses LLM en sont truffées,
/// ce qui parasite la phonémisation / la génération de tokens audio).</item>
/// <item><see cref="SplitSentences"/> : découpe le texte nettoyé en segments « phrase » pour la synthèse
/// streaming — le premier son part dès la 1ʳᵉ phrase au lieu d'attendre toute la réponse.</item>
/// </list>
/// Extrait de <see cref="KokoroTtsService"/> pour éviter la duplication entre moteurs.
/// </summary>
public static class TtsTextSegmenter
{
    /// <summary>Taille maximale (en caractères) d'un segment sans ponctuation de fin avant coupe forcée.</summary>
    public const int DefaultMaxChars = 200;

    /// <summary>
    /// Découpe le texte (déjà nettoyé via <see cref="CleanText"/>) en segments de la taille d'une phrase :
    /// coupe sur la ponctuation de fin (. ! ? …), et borne les très longues phrases sans ponctuation à
    /// <paramref name="maxChars"/> (cassées sur la dernière virgule de la fenêtre, pour préserver une
    /// frontière de prosodie).
    /// </summary>
    public static IEnumerable<string> SplitSentences(string text, int maxChars = DefaultMaxChars)
    {
        var sb = new StringBuilder();
        foreach (var rune in text.EnumerateRunes())
        {
            sb.Append(rune.ToString());
            if (rune.Value is '.' or '!' or '?' or '…')
            {
                foreach (var piece in EmitChunk(sb.ToString(), maxChars))
                    yield return piece;
                sb.Clear();
            }
        }
        foreach (var piece in EmitChunk(sb.ToString(), maxChars))
            yield return piece;
    }

    private static IEnumerable<string> EmitChunk(string raw, int max)
    {
        var s = raw.Trim();
        if (s.Length == 0)
            yield break;
        if (s.Length <= max)
        {
            yield return s;
            yield break;
        }
        // Run-on sans ponctuation de fin : on coupe à la dernière virgule de chaque fenêtre de largeur max.
        var pos = 0;
        while (pos < s.Length)
        {
            var take = Math.Min(max, s.Length - pos);
            var comma = s.LastIndexOf(',', pos + take - 1, take);
            if (comma > pos + 20)
                take = comma - pos + 1;
            yield return s.Substring(pos, take).Trim();
            pos += take;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Nettoyage du texte : retire emojis et balisage markdown avant la synthèse.
    // ─────────────────────────────────────────────────────────────────────────────────────────
    public static string CleanText(string text)
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

        // Tableaux markdown : sans traitement, le TTS prononce les « | » et « --- » → charabia. On retire les
        // lignes de séparation (|---|:--:|), on enlève les « | » de début/fin de ligne, et on remplace les « | »
        // internes par des virgules → les cellules sont lues comme une liste (« Backend, qwen3, clonage »).
        text = Regex.Replace(text, @"(?m)^[ \t]*\|?[ \t]*:?-{2,}:?[ \t]*(\|[ \t]*:?-{2,}:?[ \t]*)*\|?[ \t]*$", "");
        text = Regex.Replace(text, @"(?m)^[ \t]*\|(.+)\|[ \t]*$", "$1");
        text = Regex.Replace(text, @"[ \t]*\|[ \t]*", ", ");

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
    public static string RemoveEmojis(string text)
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
}
