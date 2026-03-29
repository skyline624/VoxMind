namespace VoxMind.Core.Transcription;

public interface ITranscriptionService : IDisposable
{
    /// <summary>Transcrire un fichier audio complet</summary>
    Task<TranscriptionResult> TranscribeFileAsync(string filePath, CancellationToken ct = default);

    /// <summary>Transcrire un chunk audio (temps réel) — WAV PCM 16kHz mono</summary>
    Task<TranscriptionResult> TranscribeChunkAsync(byte[] audioData, CancellationToken ct = default);

    /// <summary>Détecter la langue d'un audio</summary>
    Task<string> DetectLanguageAsync(byte[] audioData);

    /// <summary>Statut et info du modèle chargé</summary>
    ModelInfo Info { get; }

    /// <summary>Charger un nouveau modèle</summary>
    Task LoadModelAsync(ModelSize size);
}
