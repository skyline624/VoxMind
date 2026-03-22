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
    private readonly ITranscriptionService _transcriptionService;
    private readonly ISpeakerIdentificationService _speakerService;
    private readonly IPyAnnoteClient _pyAnnoteClient;
    private readonly ISummaryGenerator _summaryGenerator;
    private readonly VoxMindDbContext _db;
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

    public SessionStatus Status => _status;
    public ListeningSession? CurrentSession => _currentSession;
    public bool IsListening => _status == SessionStatus.Listening || _status == SessionStatus.Paused;

    public event EventHandler<SegmentProcessedEventArgs>? SegmentProcessed;
    public event EventHandler<SessionEndedEventArgs>? SessionEnded;

    public SessionManager(
        IAudioCapture audioCapture,
        ITranscriptionService transcriptionService,
        ISpeakerIdentificationService speakerService,
        IPyAnnoteClient pyAnnoteClient,
        ISummaryGenerator summaryGenerator,
        VoxMindDbContext db,
        ILogger<SessionManager> logger,
        string sessionsOutputFolder = "/home/pc/voice_data/sessions")
    {
        _audioCapture = audioCapture;
        _transcriptionService = transcriptionService;
        _speakerService = speakerService;
        _pyAnnoteClient = pyAnnoteClient;
        _summaryGenerator = summaryGenerator;
        _db = db;
        _logger = logger;
        _sessionsOutputFolder = sessionsOutputFolder;
        Directory.CreateDirectory(sessionsOutputFolder);
    }

    public async Task<ListeningSession> StartSessionAsync(string? name = null, CancellationToken ct = default)
    {
        if (_status != SessionStatus.Idle)
            throw new InvalidOperationException($"Impossible de démarrer : session déjà en cours ({_status}).");

        var session = new ListeningSession
        {
            Id = Guid.NewGuid(),
            Name = name ?? $"session_{DateTime.Now:yyyyMMdd_HHmmss}",
            StartedAt = DateTime.UtcNow,
            Status = SessionStatus.Listening
        };

        // Persister en DB
        _db.ListeningSessions.Add(new ListeningSessionEntity
        {
            Id = session.Id,
            Name = session.Name,
            StartedAt = session.StartedAt,
            Status = "active"
        });
        await _db.SaveChangesAsync(ct);

        _currentSession = session;
        _currentSegments.Clear();
        _status = SessionStatus.Listening;

        // Canal de chunks audio (capacité bornée : 100 chunks = ~10s à 100ms/chunk)
        _audioChannel = Channel.CreateBounded<AudioChunk>(new BoundedChannelOptions(100)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });

        // Démarrer le traitement async des chunks
        _processingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _processingTask = ProcessAudioChannelAsync(_processingCts.Token);

        // S'abonner à la capture audio
        _audioCapture.AudioChunkReceived += OnAudioChunkReceived;
        var config = new AudioConfiguration();
        await _audioCapture.StartCaptureAsync(config, ct);

        // Timer de résumés intermédiaires toutes les 5 minutes
        _summaryTimer = new Timer(_ => GenerateIntermediateSummary(), null,
            TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));

        _logger.LogInformation("Session '{Name}' (ID={Id}) démarrée.", session.Name, session.Id);
        return session;
    }

    public async Task<ListeningSession> StopSessionAsync()
    {
        if (_status == SessionStatus.Idle || _currentSession is null)
            throw new InvalidOperationException("Aucune session active à arrêter.");

        _logger.LogInformation("Arrêt de la session '{Name}'...", _currentSession.Name);

        // Arrêter la capture audio
        _audioCapture.AudioChunkReceived -= OnAudioChunkReceived;
        await _audioCapture.StopCaptureAsync();

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

        // Générer le résumé final
        var segments = _currentSegments.ToList();
        var summary = await _summaryGenerator.GenerateAsync(_currentSession, segments);
        _currentSession.Summary = summary;

        // Sauvegarder en DB
        var entity = await _db.ListeningSessions.FindAsync(_currentSession.Id);
        if (entity is not null)
        {
            entity.EndedAt = _currentSession.EndedAt;
            entity.Status = "completed";
            await _db.SaveChangesAsync();
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
        // Étape 1 : Extraction de l'embedding PyAnnote
        SpeakerIdentificationResult? identification = null;
        var embResult = await _pyAnnoteClient.ExtractEmbeddingAsync(chunk.RawData, ct);
        if (embResult.Success && embResult.Embedding.Length > 0)
        {
            identification = await _speakerService.IdentifyAsync(embResult.Embedding);
            if (identification.IsIdentified && identification.ProfileId.HasValue)
                await _speakerService.UpdateLastSeenAsync(identification.ProfileId.Value);
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

        // Enregistrer en DB
        _db.SessionSegments.Add(new SessionSegmentEntity
        {
            Id = segment.Id,
            SessionId = segment.SessionId,
            SpeakerId = segment.SpeakerId,
            StartTime = segment.StartSeconds,
            EndTime = segment.EndSeconds,
            Transcript = segment.Text,
            Confidence = segment.Confidence
        });
        await _db.SaveChangesAsync(ct);

        _currentSegments.Add(segment);
        _currentSession.SegmentCount++;

        // Ajouter le participant si nouveau
        if (identification?.ProfileId.HasValue == true && !_currentSession.ParticipantIds.Contains(identification.ProfileId.Value))
            _currentSession.ParticipantIds.Add(identification.ProfileId.Value);

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

    private void GenerateIntermediateSummary()
    {
        if (_currentSession is null || !_currentSegments.Any()) return;
        _logger.LogInformation("Génération du résumé intermédiaire...");
        // Résumé intermédiaire asynchrone en arrière-plan, non bloquant
        _ = Task.Run(async () =>
        {
            var summary = await _summaryGenerator.GenerateAsync(_currentSession, _currentSegments.ToList());
            _currentSession.Summary = summary;
        });
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
        _audioCapture.AudioChunkReceived -= OnAudioChunkReceived;
    }
}
