namespace VoxMind.Parakeet;

/// <summary>
/// Vocabulary-based token decoder for Parakeet TDT (vocab.txt).
/// Handles SentencePiece-style tokens (▁ prefix = word boundary).
/// </summary>
public sealed class TokenDecoder
{
    private readonly string[] _vocab;

    public int VocabSize => _vocab.Length;

    /// <summary>Blank token index (CTC/TDT blank). Defaults to last token if not found.</summary>
    public int BlankIndex { get; }

    /// <summary>Beginning-of-sequence token index.</summary>
    public int BosIndex { get; }

    /// <summary>End-of-sequence token index.</summary>
    public int EosIndex { get; }

    public TokenDecoder(string vocabPath)
    {
        // Format vocab.txt : "<token> <id>" par ligne, lignes ordonnées par ID croissant.
        // On extrait uniquement la partie token (tout ce qui précède le dernier espace).
        _vocab = File.ReadAllLines(vocabPath)
            .Select(l => l.Trim())
            .Where(l => !string.IsNullOrEmpty(l))
            .Select(static l => { var i = l.LastIndexOf(' '); return i > 0 ? l[..i] : l; })
            .ToArray();

        if (_vocab.Length == 0)
            throw new InvalidOperationException($"vocab.txt is empty: {vocabPath}");

        // Detect special tokens by conventional names
        BlankIndex = FindTokenIndex("<blk>", "<blank>", "⁇blk") ?? _vocab.Length - 1;
        BosIndex = FindTokenIndex("<s>", "<bos>", "[CLS]", "<sos>") ?? 0;
        EosIndex = FindTokenIndex("</s>", "<eos>", "[SEP]", "<pad>") ?? _vocab.Length - 2;
    }

    private int? FindTokenIndex(params string[] candidates)
    {
        for (int i = 0; i < _vocab.Length; i++)
            if (candidates.Any(c => string.Equals(c, _vocab[i], StringComparison.OrdinalIgnoreCase)))
                return i;
        return null;
    }

    /// <summary>
    /// Convert a list of token IDs to text.
    /// Filters blank/BOS/EOS tokens. Handles ▁ (U+2581) as space prefix (SentencePiece).
    /// </summary>
    public string DecodeTokens(IEnumerable<int> tokenIds)
    {
        var parts = tokenIds
            .Where(t => t >= 0 && t < _vocab.Length && t != BlankIndex && t != BosIndex && t != EosIndex)
            .Select(t => _vocab[t]);

        var text = string.Concat(parts);

        // SentencePiece: ▁ marks a word boundary (prefix space)
        text = text.Replace('▁', ' ').Trim();

        return text;
    }

    /// <summary>Returns the token string for a given index (for debugging).</summary>
    public string GetToken(int index) =>
        index >= 0 && index < _vocab.Length ? _vocab[index] : $"<{index}>";
}
