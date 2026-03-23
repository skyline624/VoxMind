namespace VoxMind.Core.Session;

public class SegmentProcessedEventArgs : EventArgs
{
    public Guid SessionId { get; init; }
    public DiarizedTranscription Segment { get; init; } = null!;
    public TimeSpan Elapsed { get; init; }
    public int TotalSegments { get; init; }
}

public class SessionEndedEventArgs : EventArgs
{
    public ListeningSession Session { get; init; } = null!;
    public TimeSpan Duration { get; init; }
    public int TotalSegments { get; init; }
    public SessionSummary? Summary { get; init; }
}

public interface ISessionManager : IDisposable
{
    /// <summary>Démarrer une session d'écoute (source locale ou fichier)</summary>
    Task<ListeningSession> StartSessionAsync(
        string? name = null,
        string sourceType = "live",
        string? sourcePath = null,
        CancellationToken ct = default);

    /// <summary>Démarrer une session d'écoute distante (sans capture audio locale)</summary>
    Task<ListeningSession> StartRemoteListeningAsync(string? name = null, CancellationToken ct = default);

    /// <summary>Injecter un chunk audio reçu d'un client distant</summary>
    Task InjectAudioChunkAsync(byte[] wavData, CancellationToken ct = default);

    /// <summary>Arrêter la session (SEUL moyen d'arrêt)</summary>
    Task<ListeningSession> StopSessionAsync();

    /// <summary>Mettre en pause</summary>
    Task PauseSessionAsync();

    /// <summary>Reprendre après pause</summary>
    Task ResumeSessionAsync();

    /// <summary>Statut actuel</summary>
    SessionStatus Status { get; }

    /// <summary>Session active (null si Idle)</summary>
    ListeningSession? CurrentSession { get; }

    /// <summary>True si une session est en cours (Listening ou Paused)</summary>
    bool IsListening { get; }

    /// <summary>Événement : nouveau segment traité</summary>
    event EventHandler<SegmentProcessedEventArgs>? SegmentProcessed;

    /// <summary>Événement : session terminée</summary>
    event EventHandler<SessionEndedEventArgs>? SessionEnded;
}
