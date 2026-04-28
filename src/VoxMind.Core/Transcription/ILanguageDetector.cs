namespace VoxMind.Core.Transcription;

/// <summary>
/// Détecteur de langue post-hoc sur le texte transcrit.
/// Utilisé en aval du STT (Parakeet v3 transcrit dans la langue cible mais n'expose
/// pas le code langue détecté ; on infère depuis le texte produit).
/// </summary>
public interface ILanguageDetector
{
    /// <summary>
    /// Détecte la langue d'un texte.
    /// </summary>
    /// <param name="text">Texte transcrit (peut être vide).</param>
    /// <param name="candidateCodes">
    /// Si non null, restreint la prédiction à ces codes ISO 639-1.
    /// Utile pour borner aux langues que le STT amont supporte (Parakeet v3 = 25 langues UE).
    /// </param>
    /// <returns>
    /// Code ISO 639-1 (<c>"fr"</c>, <c>"en"</c>, …) ou <c>"und"</c>
    /// si la confiance est insuffisante (texte trop court, aucun match).
    /// </returns>
    string DetectLanguage(string text, IEnumerable<string>? candidateCodes = null);
}
