using System.Text.Json;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VoxMind.Core.Audio;
using VoxMind.Core.Database;
using VoxMind.Core.SpeakerRecognition;
using VoxMind.Core.Transcription;

namespace VoxMind.Core.Session;

public class SessionManager : ISessionManager
{
    private readonly IAudioCapture _audioCapture;
    private readonly AudioSourceFactory? _audioSourceFactory;
    private readonly ITranscriptionService _transcriptionService;
    private readonly ISpeakerIdentificationService _speakerService;
    private readonly ISummaryGenerator _summaryGenerator;
    private readonly IDbContextFactory<VoxMindDbContext> _dbFactory;
    private readonly ILogger<SessionManager> _logger;
    private readonly string _sessionsOutputFolder;

    private SessionStatus _status = SessionStatus.Idle;
    private ListeningSession? _currentSession;
    private readonly List<SessionSegment> _currentSegments = new();
    private Channel<AudioChunk>? _audioChannel;
    private CancellationTokenSource? _processingCts;
    private Task? _processingTask;
    private Timer? _summaryTimer;
    private bool _disposed;
    private bool _isRemoteSession;
    private readonly object _lock = new();

    // Source audio de la session en cours (peut changer selon sourceType)
    private IAudioCapture _activeCapture;

    public SessionStatus Status => _status;
    public ListeningSession? CurrentSession => _currentSession;
    public bool IsListening => _status == SessionStatus.Listening || _status == SessionStatus.Paused;

    public event EventHandler<SegmentProcessedEventArgs>? SegmentProcessed;
    public event EventHandler<SessionEndedEventArgs>? SessionEnded;

    public SessionManager(
        IAudioCapture audioCapture,
        ITranscriptionService transcriptionService,
        ISpeakerIdentificationService speakerService,
        ISummaryGenerator summaryGenerator,
        IDbContextFactory<VoxMindDbContext> dbFactory,
        ILogger<SessionManager> logger,
        string sessionsOutputFolder = "/home/pc/voice_data/sessions",
        AudioSourceFactory? audioSourceFactory = null)
    {
        _audioCapture = audioCapture;
        _activeCapture = audioCapture;
        _audioSourceFactory = audioSourceFactory;
        _transcriptionService = transcriptionService;
        _speakerService = speakerService;
        _summaryGenerator = summaryGenerator;
        _dbFactory = dbFactory;
        _logger = logger;
        _sessionsOutputFolder = sessionsOutputFolder;
        Directory.CreateDirectory(sessionsOutputFolder);
    }

    public Task<ListeningSession> StartSessionAsync(
        string? name = null,
        string sourceType = "live",
        string? sourcePath = null,
        CancellationToken ct = default)
        => InitializeSessionAsync(
            name ?? $"session_{DateTime.Now:yyyyMMdd_HHmmss}",
            isRemote: false,
            sourceType: sourceType,
            sourcePath: sourcePath,
            ct: ct);

    public Task<ListeningSession> StartRemoteListeningAsync(string? name = null, CancellationToken ct = default)
        => InitializeSessionAsync(
            name ?? $"remote_{DateTime.Now:yyyyMMdd_HHmmss}",
            isRemote: true,
            sourceType: "live",
            sourcePath: null,
            ct: ct);

    /// <summary>
    /// Démarrage commun (local + remote) — élimine la duplication entre StartSessionAsync et
    /// StartRemoteListeningAsync. Persiste la session, initialise le channel et démarre la pipeline.
    /// </summary>
    private async Task<ListeningSession> InitializeSessionAsync(
        string name,
        bool isRemote,
        string sourceType,
        string? sourcePath,
        CancellationToken ct)
    {
        if (_status != SessionStatus.Idle)
            throw new InvalidOperationException($"Impossible de démarrer : session déjà en cours ({_status}).");

        var session = new ListeningSession
        {
            Id = Guid.NewGuid(),
            Name = name,
            StartedAt = DateTime.UtcNow,
            Status = SessionStatus.Listening
        };

        // Persister en DB via une instance dédiée (DbContext non thread-safe)
        await using (var db = await _dbFactory.CreateDbContextAsync(ct))
        {
            db.ListeningSessions.Add(new ListeningSessionEntity
            {
                Id = session.Id,
                Name = session.Name,
                StartedAt = session.StartedAt,
                Status = "active"
            });
            await db.SaveChangesAsync(ct);
        }

        lock (_lock)
        {
            _currentSession = session;
            _currentSegments.Clear();
            _status = SessionStatus.Listening;
            _isRemoteSession = isRemote;
        }

        // Canal de chunks audio (capacité bornée : 100 chunks = ~10s à 100ms/chunk)
        _audioChannel = Channel.CreateBounded<AudioChunk>(new BoundedChannelOptions(100)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });

        // Démarrer le traitement async des chunks
        _processingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _processingTask = ProcessAudioChannelAsync(_processingCts.Token);

        if (!isRemote)
        {
            // Sélectionner la source audio (live = micro, file = décodage FFmpeg)
            _activeCapture = _audioSourceFactory?.Create(sourceType, sourcePath) ?? _audioCapture;

            // S'abonner à la capture audio
            _activeCapture.AudioChunkReceived += OnAudioChunkReceived;
            if (!_activeCapture.IsLive && _activeCapture is FileAudioSource fileSource)
                fileSource.PlaybackCompleted += OnPlaybackCompleted;

            var config = new AudioConfiguration();
            await _activeCapture.StartCaptureAsync(config, ct);
        }

        // Timer de résumés intermédiaires toutes les 5 minutes (fire-and-forget sécurisé)
        _summaryTimer = new Timer(_ =>
        {
            _ = Task.Run(async () =>
            {
                try { await GenerateIntermediateSummaryAsync(); }
                catch (Exception ex) { _logger.LogError(ex, "Erreur lors de la génération du résumé intermédiaire."); }
            });
        }, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));

        if (isRemote)
            _logger.LogInformation("Session distante '{Name}' (ID={Id}) démarrée.", session.Name, session.Id);
        else
            _logger.LogInformation("Session '{Name}' (ID={Id}) démarrée (source: {Source}).",
                session.Name, session.Id, sourceType);
        return session;
    }

    public async Task InjectAudioChunkAsync(byte[] wavData, CancellationToken ct = default)
    {
        if (_status != SessionStatus.Listening || _audioChannel is null) return;
        var chunk = new AudioChunk(wavData, AudioSourceType.Remote, TimeSpan.Zero, 16000);
        await _audioChannel.Writer.WriteAsync(chunk, ct);
    }

    public async Task<ListeningSession> StopSessionAsync()
    {
        if (_status == SessionStatus.Idle || _currentSession is null)
            throw new InvalidOperationException("Aucune session active à arrêter.");

        _logger.LogInformation("Arrêt de la session '{Name}'...", _currentSession.Name);

        // Arrêter la capture audio (seulement pour les sessions locales)
        if (!_isRemoteSession)
        {
            _activeCapture.AudioChunkReceived -= OnAudioChunkReceived;
            if (_activeCapture is FileAudioSource fs)
                fs.PlaybackCompleted -= OnPlaybackCompleted;
            await _activeCapture.StopCaptureAsync();
            if (!_activeCapture.IsLive)
                _activeCapture.Dispose();
        }

        // Arrêter le timer de résumés intermédiaires
        _summaryTimer?.Dispose();
        _summaryTimer = null;

        // Vider et fermer le channel
        _audioChannel?.Writer.TryComplete();
        _processingCts?.Cancel();
        if (_processingTask is not null)
            await _processingTask.WaitAsync(TimeSpan.FromSeconds(10));

        _currentSession.EndedAt = DateTime.UtcNow;
        _currentSession.Status = SessionStatus.Idle;

        // Générer le résumé final (snapshot des segments sous lock — race avec ProcessChunkAsync)
        List<SessionSegment> segments;
        lock (_lock) { segments = _currentSegments.ToList(); }
        var summary = await _summaryGenerator.GenerateAsync(_currentSession, segments);
        _currentSession.Summary = summary;

        // Sauvegarder en DB via instance dédiée (DbContext non thread-safe)
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            var entity = await db.ListeningSessions.FindAsync(_currentSession.Id);
            if (entity is not null)
            {
                entity.EndedAt = _currentSession.EndedAt;
                entity.Status = "completed";
                await db.SaveChangesAsync();
            }
        }

        // Sauvegarder le JSON de session
        await SaveSessionJsonAsync(_currentSession, segments);

        var finishedSession = _currentSession;
        _status = SessionStatus.Idle;
        _currentSession = null;

        SessionEnded?.Invoke(this, new SessionEndedEventArgs
        {
            Session = finishedSession,
            Duration = finishedSession.Duration,
            TotalSegments = segments.Count,
            Summary = summary
        });

        _logger.LogInformation("Session '{Name}' terminée ({Duration}).", finishedSession.Name, FormatDuration(finishedSession.Duration));
        return finishedSession;
    }

    public Task PauseSessionAsync()
    {
        if (_status != SessionStatus.Listening)
            throw new InvalidOperationException("Impossible de mettre en pause : session non active.");
        _status = SessionStatus.Paused;
        _currentSession!.Status = SessionStatus.Paused;
        _logger.LogInformation("Session mise en pause.");
        return Task.CompletedTask;
    }

    public Task ResumeSessionAsync()
    {
        if (_status != SessionStatus.Paused)
            throw new InvalidOperationException("Impossible de reprendre : session non en pause.");
        _status = SessionStatus.Listening;
        _currentSession!.Status = SessionStatus.Listening;
        _logger.LogInformation("Session reprise.");
        return Task.CompletedTask;
    }

    private void OnAudioChunkReceived(object? sender, AudioChunkEventArgs e)
    {
        if (_status != SessionStatus.Listening) return;
        _audioChannel?.Writer.TryWrite(e.Chunk);
    }

    /// <summary>Appelé quand un fichier audio atteint sa fin — arrête la session automatiquement.</summary>
    private void OnPlaybackCompleted(object? sender, EventArgs e)
    {
        _ = Task.Run(async () =>
        {
            try { await StopSessionAsync(); }
            catch (InvalidOperationException) { /* session déjà arrêtée */ }
            catch (Exception ex) { _logger.LogError(ex, "Erreur lors de l'auto-arrêt après fin de fichier."); }
        });
    }

    private async Task ProcessAudioChannelAsync(CancellationToken ct)
    {
        if (_audioChannel is null) return;

        await foreach (var chunk in _audioChannel.Reader.ReadAllAsync(ct))
        {
            try
            {
                await ProcessChunkAsync(chunk, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erreur lors du traitement d'un chunk audio");
            }
        }
    }

    private async Task ProcessChunkAsync(AudioChunk chunk, CancellationToken ct)
    {
        // Étape 1 : Identification du locuteur via sherpa-onnx (extraction embedding + identification)
        SpeakerIdentificationResult? identification = null;
        try
        {
            identification = await _speakerService.IdentifyFromAudioAsync(chunk.RawData, ct);
            if (identification.IsIdentified && identification.ProfileId.HasValue)
                await _speakerService.UpdateLastSeenAsync(identification.ProfileId.Value);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Identification du locuteur non disponible pour ce chunk.");
        }

        // Étape 2 : Transcription
        TranscriptionResult transcription;
        try
        {
            transcription = await _transcriptionService.TranscribeChunkAsync(chunk.RawData, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Transcription échouée pour ce chunk");
            return;
        }

        if (string.IsNullOrWhiteSpace(transcription.Text)) return;

        // Étape 3 : Créer le segment
        var segment = new SessionSegment
        {
            Id = Guid.NewGuid(),
            SessionId = _currentSession!.Id,
            SpeakerId = identification?.ProfileId,
            SpeakerName = identification?.SpeakerName,
            StartSeconds = chunk.Timestamp.TotalSeconds,
            EndSeconds = (chunk.Timestamp + chunk.Duration).TotalSeconds,
            Text = transcription.Text,
            Confidence = transcription.Confidence
        };

        // Enregistrer en DB via instance dédiée (DbContext non thread-safe — créé par chunk)
        await using (var db = await _dbFactory.CreateDbContextAsync(ct))
        {
            db.SessionSegments.Add(new SessionSegmentEntity
            {
                Id = segment.Id,
                SessionId = segment.SessionId,
                SpeakerId = segment.SpeakerId,
                StartTime = segment.StartSeconds,
                EndTime = segment.EndSeconds,
                Transcript = segment.Text,
                Confidence = segment.Confidence
            });
            await db.SaveChangesAsync(ct);
        }

        // Mutations partagées sous lock — ProcessAudioChannelAsync tourne sur un thread dédié,
        // mais StopSessionAsync, GenerateIntermediateSummaryAsync et le Timer peuvent lire en parallèle.
        lock (_lock)
        {
            _currentSegments.Add(segment);
            _currentSession.SegmentCount++;

            // Ajouter le participant si nouveau
            if (identification?.ProfileId.HasValue == true && !_currentSession.ParticipantIds.Contains(identification.ProfileId.Value))
                _currentSession.ParticipantIds.Add(identification.ProfileId.Value);
        }

        // Déclencher l'événement
        SegmentProcessed?.Invoke(this, new SegmentProcessedEventArgs
        {
            SessionId = _currentSession.Id,
            Segment = new DiarizedTranscription
            {
                Start = TimeSpan.FromSeconds(segment.StartSeconds),
                End = TimeSpan.FromSeconds(segment.EndSeconds),
                SpeakerId = segment.SpeakerId,
                SpeakerName = segment.SpeakerName,
                Text = segment.Text,
                Confidence = segment.Confidence
            },
            Elapsed = _currentSession.Duration,
            TotalSegments = _currentSession.SegmentCount
        });
    }

    private async Task GenerateIntermediateSummaryAsync()
    {
        ListeningSession? sessionSnapshot;
        List<SessionSegment> segmentsSnapshot;
        lock (_lock)
        {
            if (_currentSession is null || _currentSegments.Count == 0) return;
            sessionSnapshot = _currentSession;
            segmentsSnapshot = _currentSegments.ToList();
        }

        _logger.LogInformation("Génération du résumé intermédiaire...");
        var summary = await _summaryGenerator.GenerateAsync(sessionSnapshot, segmentsSnapshot);
        lock (_lock)
        {
            // _currentSession peut avoir été remplacé pendant l'await — ne pas écraser une autre session
            if (_currentSession is not null && _currentSession.Id == sessionSnapshot.Id)
                _currentSession.Summary = summary;
        }
        _logger.LogInformation("Résumé intermédiaire généré pour session {Id}", sessionSnapshot.Id);
    }

    private async Task SaveSessionJsonAsync(ListeningSession session, List<SessionSegment> segments)
    {
        var sessionDir = Path.Combine(_sessionsOutputFolder, session.Id.ToString());
        Directory.CreateDirectory(sessionDir);

        var sessionData = new
        {
            id = session.Id,
            name = session.Name,
            started_at = session.StartedAt,
            ended_at = session.EndedAt,
            duration_seconds = (int)session.Duration.TotalSeconds,
            status = "completed",
            participants = session.Summary?.Participants.Select(p => new
            {
                speaker_id = p.SpeakerId,
                name = p.Name,
                speaking_time_seconds = (int)p.SpeakingTime.TotalSeconds,
                utterance_count = p.UtteranceCount,
                percentage = p.PercentageOfSession
            }),
            segments = segments.Select(s => new
            {
                id = s.Id,
                speaker_id = s.SpeakerId,
                speaker_name = s.SpeakerName,
                start_seconds = s.StartSeconds,
                end_seconds = s.EndSeconds,
                text = s.Text,
                confidence = s.Confidence
            }),
            key_moments = session.Summary?.KeyMoments,
            decisions = session.Summary?.Decisions,
            action_items = session.Summary?.ActionItems,
            summary = session.Summary?.GeneratedSummary,
            raw_transcript = session.Summary?.FullTranscript
        };

        var json = JsonSerializer.Serialize(sessionData, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(Path.Combine(sessionDir, "session.json"), json);
        _logger.LogInformation("Session JSON sauvegardée dans {Dir}", sessionDir);
    }

    private static string FormatDuration(TimeSpan d) =>
        d.TotalHours >= 1 ? $"{(int)d.TotalHours}h{d.Minutes:D2}min" : $"{(int)d.TotalMinutes}min";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _summaryTimer?.Dispose();
        _processingCts?.Cancel();
        _processingCts?.Dispose();
        _activeCapture.AudioChunkReceived -= OnAudioChunkReceived;
        if (_activeCapture is FileAudioSource fs)
        {
            fs.PlaybackCompleted -= OnPlaybackCompleted;
            fs.Dispose();
        }
    }
}
