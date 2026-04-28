namespace VoxMind.F5Tts;

/// <summary>
/// Tokenizer phonémique BPE pour F5-TTS (sentencepiece-style).
///
/// Le port <see href="https://github.com/DakeQQ/F5-TTS-ONNX"/> fournit un
/// <c>tokens.txt</c> par checkpoint au format <c>token<TAB>id</c>. Pour les
/// textes en langues UE, l'export utilise pyopenphonemizer en amont — donc
/// idéalement on reçoit un texte phonémisé. La v1 de VoxMind se contente
/// d'un fallback "char-tokens" pour les langues à alphabet latin (FR/EN), ce
/// qui suffit à valider le pipeline de bout en bout. Le phonemizer plus
/// élaboré peut être ajouté plus tard sans casser l'API.
/// </summary>
public sealed class F5TtsTokenizer
{
    public const int PadToken = 0;
    public const int UnkToken = 1;

    private readonly Dictionary<string, int> _tokenToId;
    private readonly string[] _idToToken;

    public int VocabSize => _idToToken.Length;

    public F5TtsTokenizer(string tokensPath)
    {
        if (!File.Exists(tokensPath))
            throw new FileNotFoundException($"tokens.txt introuvable : {tokensPath}", tokensPath);

        var lines = File.ReadAllLines(tokensPath);
        _tokenToId = new Dictionary<string, int>(lines.Length);
        var list = new List<string>(lines.Length);

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            // Format DakeQQ : "token<TAB>id" ou "token<SPACE>id" ou simplement "token"
            var parts = line.Split(new[] { '\t', ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
            var token = parts[0];
            int id;
            if (parts.Length == 2 && int.TryParse(parts[1], out id))
            {
                while (list.Count <= id) list.Add(string.Empty);
                list[id] = token;
            }
            else
            {
                id = list.Count;
                list.Add(token);
            }
            _tokenToId[token] = id;
        }

        _idToToken = list.ToArray();
    }

    /// <summary>
    /// Encode un texte en suite d'identifiants token. Tokenisation char-level
    /// (chaque caractère unicode → un token), avec espace mappé sur le token
    /// dédié si présent. Inconnus → <see cref="UnkToken"/>.
    /// </summary>
    public int[] Encode(string text)
    {
        if (string.IsNullOrEmpty(text)) return Array.Empty<int>();

        var enumerator = System.Globalization.StringInfo.GetTextElementEnumerator(text);
        var ids = new List<int>(text.Length);
        while (enumerator.MoveNext())
        {
            var ch = (string)enumerator.Current;
            if (_tokenToId.TryGetValue(ch, out int id))
                ids.Add(id);
            else if (ch == " " && _tokenToId.TryGetValue("▁", out int spaceId))
                ids.Add(spaceId);
            else
                ids.Add(UnkToken);
        }
        return ids.ToArray();
    }
}
