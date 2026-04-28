using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace VoxMind.Core.Transcription;

/// <summary>
/// Détecteur de langue lexical par recouvrement de stopwords.
/// Compte combien de mots du texte appartiennent à la liste de stopwords de chaque langue
/// candidate ; renvoie la langue avec le plus grand recouvrement (normalisé).
///
/// Pourquoi pas une lib externe ? Les détecteurs n-gram (NTextCat / cld3) demandent un
/// profil sur disque (~5 MB) ou un binding natif. Ici on cible spécifiquement les
/// 25 langues européennes que Parakeet v3 supporte sur du texte déjà transcrit
/// (donc bien formé et de longueur typique &gt; 5 mots) — l'approche stopwords est
/// suffisante, déterministe, AOT-compatible et zéro dépendance.
/// </summary>
public sealed class StopwordLanguageDetector : ILanguageDetector
{
    /// <summary>Texte de moins de N tokens utiles → langue indéterminée.</summary>
    private const int MinUsefulTokens = 3;

    /// <summary>Score normalisé minimal pour considérer une détection valide.</summary>
    private const double MinScore = 0.10;

    private static readonly Regex TokenRegex = new(@"\p{L}+", RegexOptions.Compiled);

    private readonly ILogger<StopwordLanguageDetector>? _logger;
    private readonly IReadOnlyDictionary<string, HashSet<string>> _stopwords;

    public StopwordLanguageDetector(ILogger<StopwordLanguageDetector>? logger = null)
    {
        _logger = logger;
        _stopwords = BuildStopwordTable();
    }

    public string DetectLanguage(string text, IEnumerable<string>? candidateCodes = null)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "und";

        var tokens = TokenRegex.Matches(text)
            .Select(m => m.Value.ToLowerInvariant())
            .ToArray();

        if (tokens.Length < MinUsefulTokens)
            return "und";

        var candidates = candidateCodes is null
            ? _stopwords.Keys
            : candidateCodes.Where(c => _stopwords.ContainsKey(c));

        string bestCode = "und";
        double bestScore = 0.0;

        foreach (var code in candidates)
        {
            var stopset = _stopwords[code];
            int matches = 0;
            foreach (var tok in tokens)
            {
                if (stopset.Contains(tok))
                    matches++;
            }

            double score = (double)matches / tokens.Length;
            if (score > bestScore)
            {
                bestScore = score;
                bestCode = code;
            }
        }

        if (bestScore < MinScore)
        {
            _logger?.LogDebug(
                "Détection langue : confiance trop faible (best={Code} score={Score:F2} sur {N} tokens), retour 'und'.",
                bestCode, bestScore, tokens.Length);
            return "und";
        }

        return bestCode;
    }

    /// <summary>
    /// Stopwords (top-30 mots fréquents) pour les 25 langues européennes supportées par
    /// Parakeet TDT v3. Sources publiques : listes de fréquence Wikipedia / corpus Tatoeba.
    /// Codes ISO 639-1.
    /// </summary>
    private static IReadOnlyDictionary<string, HashSet<string>> BuildStopwordTable()
    {
        return new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["bg"] = Set("и", "в", "на", "е", "за", "се", "че", "не", "с", "по", "от", "като", "са", "до", "този", "така", "този", "при", "който", "ще", "беше", "си", "но", "още", "ако", "те", "го", "много", "защото", "всичко"),
            ["hr"] = Set("i", "u", "je", "na", "se", "da", "su", "za", "od", "ne", "to", "s", "kao", "po", "ali", "ako", "li", "iz", "već", "samo", "još", "te", "biti", "kad", "ovo", "tako", "što", "može", "ima", "ili"),
            ["cs"] = Set("a", "v", "se", "na", "je", "to", "že", "s", "z", "do", "i", "o", "ve", "k", "ale", "tak", "jak", "co", "by", "není", "jako", "už", "také", "od", "po", "ze", "pro", "jsou", "při", "tím"),
            ["da"] = Set("og", "i", "at", "det", "en", "den", "til", "er", "som", "på", "de", "med", "han", "af", "for", "ikke", "der", "var", "mig", "sig", "men", "et", "har", "om", "vi", "min", "havde", "ham", "hun", "nu"),
            ["nl"] = Set("de", "en", "het", "van", "een", "in", "is", "dat", "op", "te", "zijn", "voor", "met", "niet", "die", "ik", "je", "om", "naar", "ook", "maar", "als", "er", "aan", "bij", "uit", "zich", "of", "we", "nog"),
            ["en"] = Set("the", "be", "to", "of", "and", "a", "in", "that", "have", "i", "it", "for", "not", "on", "with", "he", "as", "you", "do", "at", "this", "but", "his", "by", "from", "they", "we", "say", "her", "she", "is", "are", "was", "were", "an"),
            ["et"] = Set("ja", "on", "ei", "et", "ta", "see", "ma", "kui", "olen", "siis", "ka", "aga", "või", "selle", "oma", "minu", "ole", "nii", "kõik", "ning", "veel", "üks", "ühe", "kus", "ole", "mis", "olid", "nad", "mind", "olnud"),
            ["fi"] = Set("ja", "on", "ei", "se", "että", "hän", "ne", "olen", "kun", "niin", "minä", "mutta", "vain", "tai", "mitä", "kuin", "mikä", "vielä", "myös", "tämä", "minun", "nyt", "voi", "siinä", "missä", "siellä", "oli", "ollut", "olla", "kaikki"),
            ["fr"] = Set("le", "la", "les", "de", "des", "du", "un", "une", "et", "à", "en", "que", "qui", "ne", "pas", "ce", "il", "elle", "je", "vous", "nous", "ils", "elles", "on", "dans", "sur", "avec", "pour", "par", "mais", "au", "aux", "est", "sont", "était"),
            ["de"] = Set("der", "die", "das", "und", "in", "zu", "den", "ist", "von", "nicht", "sich", "auch", "es", "auf", "für", "mit", "ein", "eine", "einen", "dem", "im", "an", "war", "war", "wie", "aber", "nur", "noch", "wir", "ihr", "sie", "sind", "haben", "werden", "sein"),
            ["el"] = Set("και", "να", "το", "η", "ο", "σε", "δεν", "που", "τα", "με", "στο", "για", "τη", "από", "θα", "είναι", "ένα", "μια", "αυτό", "αυτή", "αυτός", "αλλά", "ή", "αν", "πιο", "πολύ", "όλα", "όταν", "επίσης", "γιατί"),
            ["hu"] = Set("a", "az", "és", "hogy", "nem", "is", "egy", "én", "te", "ő", "mi", "ti", "ők", "van", "volt", "lesz", "csak", "már", "még", "ha", "de", "vagy", "így", "ezt", "azt", "olyan", "sok", "minden", "ki", "mit"),
            ["it"] = Set("il", "la", "di", "e", "che", "è", "in", "un", "una", "per", "non", "con", "ma", "si", "lo", "le", "i", "del", "della", "dei", "delle", "al", "alla", "ai", "alle", "da", "su", "come", "anche", "più", "sono", "ho", "ha", "se"),
            ["lv"] = Set("un", "ir", "es", "tu", "viņš", "viņa", "mēs", "jūs", "viņi", "ka", "ne", "uz", "no", "ar", "kā", "tā", "kas", "tas", "šis", "šī", "bet", "vai", "arī", "tikai", "vēl", "jau", "kur", "kad", "ja", "vairs"),
            ["lt"] = Set("ir", "yra", "aš", "tu", "jis", "ji", "mes", "jūs", "jie", "kad", "ne", "į", "iš", "su", "kaip", "tai", "kas", "šis", "ši", "bet", "ar", "taip", "tik", "dar", "jau", "kur", "kada", "jei", "labai", "tačiau"),
            ["mt"] = Set("u", "il", "f", "ta", "li", "hu", "hi", "huma", "jien", "int", "aħna", "intom", "ma", "ġie", "kien", "tkun", "ġimgħa", "kif", "x", "xi", "kollox", "kollha", "biex", "imma", "issa", "qabel", "wara", "fuq", "taħt", "bejn"),
            ["pl"] = Set("i", "w", "na", "z", "się", "do", "że", "to", "nie", "jest", "co", "po", "od", "jak", "ale", "tak", "ja", "ty", "on", "ona", "my", "wy", "oni", "tu", "tam", "też", "już", "tylko", "lub", "jeszcze"),
            ["pt"] = Set("o", "a", "os", "as", "de", "do", "da", "dos", "das", "e", "que", "em", "para", "com", "não", "um", "uma", "se", "por", "mas", "como", "também", "mais", "só", "já", "ele", "ela", "eu", "tu", "ser", "está", "são", "foi", "ter", "tem"),
            ["ro"] = Set("și", "în", "a", "este", "să", "nu", "de", "la", "pe", "cu", "un", "o", "se", "că", "ca", "din", "pentru", "dar", "fi", "are", "fost", "fie", "el", "ea", "ei", "ele", "eu", "tu", "noi", "voi"),
            ["sk"] = Set("a", "v", "sa", "na", "je", "to", "že", "s", "z", "do", "i", "o", "vo", "k", "ale", "tak", "ako", "čo", "by", "nie", "už", "tiež", "od", "po", "zo", "pre", "sú", "pri", "tým", "len"),
            ["sl"] = Set("in", "v", "se", "je", "na", "da", "so", "za", "pa", "ne", "to", "s", "kot", "po", "ali", "če", "iz", "že", "samo", "še", "te", "biti", "ko", "to", "tako", "kaj", "lahko", "ima", "ali", "pri"),
            ["es"] = Set("el", "la", "los", "las", "de", "del", "y", "que", "en", "un", "una", "es", "se", "no", "lo", "le", "su", "por", "con", "para", "como", "más", "pero", "sus", "al", "está", "están", "son", "ser", "fue", "ha", "yo", "tú", "él", "ella", "nosotros", "me", "te", "nos", "muy", "todo", "todos", "este", "esta", "eso", "esto", "aquí", "ahí", "ahora", "hoy", "cómo", "qué", "porque", "donde", "cuando", "siempre", "nunca", "también", "ya"),
            ["sv"] = Set("och", "i", "att", "det", "en", "som", "är", "på", "för", "med", "har", "till", "den", "av", "men", "om", "han", "hon", "vi", "ni", "de", "var", "inte", "så", "ett", "från", "kan", "bara", "när", "eller"),
            ["ru"] = Set("и", "в", "не", "на", "я", "что", "он", "с", "по", "это", "она", "но", "за", "к", "из", "от", "у", "то", "его", "так", "же", "как", "бы", "то", "только", "вы", "ты", "мы", "они", "был"),
            ["uk"] = Set("і", "в", "у", "на", "не", "що", "з", "до", "як", "це", "він", "вона", "вони", "я", "ти", "ми", "ви", "так", "але", "по", "за", "від", "із", "та", "або", "ще", "вже", "тільки", "коли", "де"),
        };

        static HashSet<string> Set(params string[] words)
            => new(words, StringComparer.OrdinalIgnoreCase);
    }
}
