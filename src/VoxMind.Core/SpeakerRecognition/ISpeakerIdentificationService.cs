using VoxMind.Core.Vad;

namespace VoxMind.Core.SpeakerRecognition;

/// <summary>Résultat de diarisation pour un segment : locuteur identifié ou auto-créé.</summary>
public record SpeakerLabel(Guid? ProfileId, string Name);

public interface ISpeakerIdentificationService : IDisposable
{
    /// <summary>
    /// Diarise une liste de segments VAD : extrait les empreintes, regroupe par locuteur
    /// (clustering greedy cosinus), matche contre les profils connus et crée automatiquement
    /// les profils inconnus ("Locuteur N").
    /// Retourne un dictionnaire index de segment → locuteur.
    /// </summary>
    Task<IReadOnlyDictionary<int, SpeakerLabel>> DiarizeSegmentsAsync(
        IReadOnlyList<VadSegment> segments,
        CancellationToken ct = default);
    /// <summary>Enroller un nouveau locuteur avec une empreinte vocale</summary>
    Task<SpeakerProfile> EnrollSpeakerAsync(string name, float[] embedding, float initialConfidence, int audioDurationSeconds = 0);

    /// <summary>Ajouter une nouvelle empreinte à un profil existant</summary>
    Task AddEmbeddingToProfileAsync(Guid profileId, float[] embedding, float confidence);

    /// <summary>Identifier un locuteur à partir d'une empreinte</summary>
    Task<SpeakerIdentificationResult> IdentifyAsync(float[] embedding);

    /// <summary>Extraire l'embedding et identifier directement depuis des données audio WAV PCM 16kHz</summary>
    Task<SpeakerIdentificationResult> IdentifyFromAudioAsync(byte[] audioData, CancellationToken ct = default);

    /// <summary>Extraire uniquement l'embedding vocal depuis des données audio WAV PCM 16kHz (pour enrollment)</summary>
    Task<float[]?> ExtractEmbeddingAsync(byte[] audioData, CancellationToken ct = default);

    /// <summary>Obtenir un profil par ID</summary>
    Task<SpeakerProfile?> GetProfileAsync(Guid profileId);

    /// <summary>Lister tous les profils actifs</summary>
    Task<IReadOnlyList<SpeakerProfile>> GetAllProfilesAsync();

    /// <summary>Fusionner deux profils (même personne) — transfert des embeddings vers target, suppression de source</summary>
    Task MergeProfilesAsync(Guid targetProfileId, Guid sourceProfileId);

    /// <summary>Renommer un profil</summary>
    Task RenameProfileAsync(Guid profileId, string newName);

    /// <summary>Supprimer un profil et ses embeddings</summary>
    Task DeleteProfileAsync(Guid profileId);

    /// <summary>Lier un locuteur inconnu à un profil connu (alias)</summary>
    Task LinkSpeakersAsync(Guid knownProfileId, Guid unknownProfileId);

    /// <summary>Mettre à jour la date de dernière détection</summary>
    Task UpdateLastSeenAsync(Guid profileId);

    /// <summary>Vérifier la connexion au service PyAnnote</summary>
    Task<bool> CheckHealthAsync();
}
