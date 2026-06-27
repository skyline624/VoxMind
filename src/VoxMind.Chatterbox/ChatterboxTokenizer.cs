using System.Text.Json;
using System.Text.RegularExpressions;

namespace VoxMind.Chatterbox;

/// <summary>
/// Réimplémentation du tokenizer BPE de Chatterbox (onnx-community/chatterbox-multilingual-ONNX).
/// Reproduit : normalizer (espace -> [SPACE]), pré-tokenizer Whitespace, BPE (vocab+merges),
/// et le post-processor TemplateProcessing : [EXAG=6563, BOS=255, &lt;texte&gt;, EOS=0, START_SPEECH=6561, 6561].
/// </summary>
public sealed class ChatterboxTokenizer
{
    private readonly Dictionary<string, int> _vocab;
    private readonly Dictionary<(string, string), int> _mergeRank;
    private readonly Dictionary<string, int> _added;       // content -> id (tokens spéciaux apparaissant dans le texte)
    private readonly Regex _addedRegex;
    private readonly Regex _preTok = new(@"\w+|[^\w\s]+", RegexOptions.Compiled);

    // ids du template (post_processor.special_tokens)
    private const int EXAGGERATION = 6563;
    private const int BOS = 255;
    private const int EOS = 0;
    private const int START_SPEECH = 6561;
    private const int UNK = 1;

    public ChatterboxTokenizer(string tokenizerJsonPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(tokenizerJsonPath));
        var model = doc.RootElement.GetProperty("model");

        _vocab = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var kv in model.GetProperty("vocab").EnumerateObject())
            _vocab[kv.Name] = kv.Value.GetInt32();

        _mergeRank = new Dictionary<(string, string), int>();
        int rank = 0;
        foreach (var m in model.GetProperty("merges").EnumerateArray())
        {
            // merges au format "a b" (string) — certains tokenizers utilisent ["a","b"]
            string a, b;
            if (m.ValueKind == JsonValueKind.Array)
            {
                a = m[0].GetString()!; b = m[1].GetString()!;
            }
            else
            {
                var parts = m.GetString()!.Split(' ', 2);
                a = parts[0]; b = parts[1];
            }
            _mergeRank.TryAdd((a, b), rank++);
        }

        _added = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var t in doc.RootElement.GetProperty("added_tokens").EnumerateArray())
        {
            var content = t.GetProperty("content").GetString()!;
            _added[content] = t.GetProperty("id").GetInt32();
        }
        // regex qui matche n'importe quel token spécial, plus longs d'abord
        var alt = string.Join("|", _added.Keys.OrderByDescending(k => k.Length).Select(Regex.Escape));
        _addedRegex = new Regex("(" + alt + ")", RegexOptions.Compiled);
    }

    /// <summary>Encode "[lang]texte" en IDs prêts pour embed_tokens (avec template).</summary>
    public long[] Encode(string text, string lang)
    {
        string norm = ($"[{lang}]" + text).Replace(" ", "[SPACE]");

        var ids = new List<int>();
        // découpe en isolant les tokens spéciaux ([fr], [SPACE], ...)
        foreach (var part in _addedRegex.Split(norm))
        {
            if (part.Length == 0) continue;
            if (_added.TryGetValue(part, out var sid))
            {
                ids.Add(sid);
            }
            else
            {
                foreach (Match w in _preTok.Matches(part))
                    BpeEncode(w.Value, ids);
            }
        }

        var result = new long[ids.Count + 5];
        result[0] = EXAGGERATION;
        result[1] = BOS;
        for (int i = 0; i < ids.Count; i++) result[i + 2] = ids[i];
        result[^3] = EOS;
        result[^2] = START_SPEECH;
        result[^1] = START_SPEECH;
        return result;
    }

    private void BpeEncode(string word, List<int> outIds)
    {
        if (word.Length == 0) return;
        var symbols = word.Select(c => c.ToString()).ToList();

        while (symbols.Count > 1)
        {
            int bestRank = int.MaxValue, bestIdx = -1;
            for (int i = 0; i < symbols.Count - 1; i++)
            {
                if (_mergeRank.TryGetValue((symbols[i], symbols[i + 1]), out var r) && r < bestRank)
                {
                    bestRank = r; bestIdx = i;
                }
            }
            if (bestIdx < 0) break;
            symbols[bestIdx] = symbols[bestIdx] + symbols[bestIdx + 1];
            symbols.RemoveAt(bestIdx + 1);
        }

        foreach (var s in symbols)
            outIds.Add(_vocab.TryGetValue(s, out var id) ? id : UNK);
    }
}
