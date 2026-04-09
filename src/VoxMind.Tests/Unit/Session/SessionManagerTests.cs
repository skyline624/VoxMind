using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using VoxMind.Core.Audio;
using VoxMind.Core.Session;
using VoxMind.Core.SpeakerRecognition;
using VoxMind.Core.Transcription;
using Xunit;

namespace VoxMind.Tests.Unit.Session;

public class SessionManagerTests : IDisposable
{
    private readonly Mock<IAudioCapture> _mockAudio;
    private readonly Mock<ITranscriptionService> _mockTranscription;
    private readonly Mock<ISpeakerIdentificationService> _mockSpeaker;
    private readonly Mock<ISummaryGenerator> _mockSummary;
    private readonly TestDbContextFactory _dbFactory;
    private readonly SessionManager _manager;

    public SessionManagerTests()
    {
        _mockAudio = new Mock<IAudioCapture>();
        _mockTranscription = new Mock<ITranscriptionService>();
        _mockSpeaker = new Mock<ISpeakerIdentificationService>();
        _mockSummary = new Mock<ISummaryGenerator>();

        _dbFactory = new TestDbContextFactory();

        // Configurer les mocks de base
        _mockAudio.Setup(a => a.StartCaptureAsync(It.IsAny<AudioConfiguration>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockAudio.Setup(a => a.StopCaptureAsync()).Returns(Task.CompletedTask);

        _mockSummary.Setup(s => s.GenerateAsync(It.IsAny<ListeningSession>(), It.IsAny<IReadOnlyList<SessionSegment>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SessionSummary());

        var tmpDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        _manager = new SessionManager(
            _mockAudio.Object,
            _mockTranscription.Object,
            _mockSpeaker.Object,
            _mockSummary.Object,
            _dbFactory,
            NullLogger<SessionManager>.Instance,
            tmpDir
        );
    }

    [Fact]
    public async Task StartSession_WithNoActiveSession_StartsSuccessfully()
    {
        // Act
        var session = await _manager.StartSessionAsync("test_session");

        // Assert
        Assert.NotNull(session);
        Assert.Equal(SessionStatus.Listening, _manager.Status);
        Assert.Equal("test_session", session.Name);
        Assert.Null(session.EndedAt);
        Assert.True(_manager.IsListening);
    }

    [Fact]
    public async Task StopSession_WithoutStart_ThrowsInvalidOperationException()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => _manager.StopSessionAsync());
    }

    [Fact]
    public async Task StopSession_WithActiveSession_StopsAndReturnsSession()
    {
        // Arrange
        var started = await _manager.StartSessionAsync();

        // Act
        var stopped = await _manager.StopSessionAsync();

        // Assert
        Assert.Equal(SessionStatus.Idle, _manager.Status);
        Assert.False(_manager.IsListening);
        Assert.NotNull(stopped.EndedAt);
        Assert.Equal(started.Id, stopped.Id);
    }

    [Fact]
    public async Task StartSession_WhenAlreadyListening_ThrowsInvalidOperationException()
    {
        await _manager.StartSessionAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => _manager.StartSessionAsync());
    }

    [Fact]
    public async Task PauseAndResume_MaintainsSession()
    {
        await _manager.StartSessionAsync();

        await _manager.PauseSessionAsync();
        Assert.Equal(SessionStatus.Paused, _manager.Status);
        Assert.True(_manager.IsListening); // Toujours "IsListening" en pause

        await _manager.ResumeSessionAsync();
        Assert.Equal(SessionStatus.Listening, _manager.Status);

        await _manager.StopSessionAsync(); // Nettoyage
    }

    [Fact]
    public async Task PauseSession_WhenIdle_ThrowsInvalidOperationException()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => _manager.PauseSessionAsync());
    }

    [Fact]
    public async Task ResumeSession_WhenNotPaused_ThrowsInvalidOperationException()
    {
        await _manager.StartSessionAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => _manager.ResumeSessionAsync());
        await _manager.StopSessionAsync(); // Nettoyage
    }

    [Fact]
    public async Task StartSession_SessionRemainsValidWhileListening()
    {
        // Arrange — le timer résumé intermédiaire est à 5 min, ne se déclenche pas pendant ce test
        // Ce test vérifie que la session reste valide pendant toute la durée d'écoute

        // Act
        var session = await _manager.StartSessionAsync("session-robustesse");
        await Task.Delay(100); // simule une courte période d'écoute

        // Assert — la session est toujours en cours et n'a pas crashé
        Assert.NotNull(session);
        Assert.Equal(SessionStatus.Listening, _manager.Status);
        Assert.Equal("session-robustesse", session.Name);
        Assert.Null(session.EndedAt); // pas encore terminée

        await _manager.StopSessionAsync(); // Nettoyage
    }

    public void Dispose()
    {
        _manager.Dispose();
    }
}
