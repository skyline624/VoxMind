using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using VoxMind.ClientGrpc;
using VoxMind.Core.Configuration;
using VoxMind.Core.RemoteClients;
using VoxMind.Core.Session;
using Xunit;

namespace VoxMind.Tests.Unit.RemoteClients;

public class AudioStreamReceiverTests
{
    private readonly RemoteClientRegistry _registry;
    private readonly Mock<ISessionManager> _mockSession;
    private readonly AudioStreamReceiverService _service;
    private const string ValidToken = "test-token-123";

    public AudioStreamReceiverTests()
    {
        _registry = new RemoteClientRegistry(NullLogger<RemoteClientRegistry>.Instance);
        _mockSession = new Mock<ISessionManager>();

        var config = new AppConfiguration
        {
            RemoteClients = new RemoteClientsConfig
            {
                SharedToken = ValidToken,
                Enabled = true,
                Port = 50052
            }
        };

        _service = new AudioStreamReceiverService(
            _registry,
            _mockSession.Object,
            config,
            NullLogger<AudioStreamReceiverService>.Instance);
    }

    [Fact]
    public async Task RegisterClient_ValidToken_ReturnsSuccess()
    {
        var request = new RegisterRequest
        {
            ClientId = "c1",
            ClientName = "Test Machine",
            AuthToken = ValidToken,
            Platform = "Linux"
        };

        var response = await _service.RegisterClient(request, MakeContext());

        Assert.True(response.Success);
        Assert.Empty(response.Error);
        Assert.True(_registry.Exists("c1"));
    }

    [Fact]
    public async Task RegisterClient_InvalidToken_ReturnsFailed()
    {
        var request = new RegisterRequest
        {
            ClientId = "c2",
            ClientName = "Intruder",
            AuthToken = "wrong-token",
            Platform = "Windows"
        };

        var response = await _service.RegisterClient(request, MakeContext());

        Assert.False(response.Success);
        Assert.NotEmpty(response.Error);
        Assert.False(_registry.Exists("c2"));
    }

    [Fact]
    public async Task SendHeartbeat_RegisteredClient_ReturnsAlive()
    {
        _registry.Register(new RemoteClientInfo { ClientId = "hb1", Name = "HB" });

        var response = await _service.SendHeartbeat(
            new HeartbeatRequest { ClientId = "hb1" },
            MakeContext());

        Assert.True(response.Alive);
    }

    [Fact]
    public async Task StreamAudio_WhenSessionActive_InjectsChunks()
    {
        _registry.Register(new RemoteClientInfo { ClientId = "stream1", Name = "Streamer" });

        _mockSession.Setup(s => s.InjectAudioChunkAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
                    .Returns(Task.CompletedTask);

        var chunks = new List<AudioChunkMessage>
        {
            new() { ClientId = "stream1", AudioData = Google.Protobuf.ByteString.CopyFrom(new byte[100]), SampleRate = 16000 },
            new() { ClientId = "stream1", AudioData = Google.Protobuf.ByteString.CopyFrom(new byte[200]), SampleRate = 16000 },
        };

        var mockStream = new MockAsyncStreamReader<AudioChunkMessage>(chunks);
        var response = await _service.StreamAudio(mockStream, MakeContext());

        Assert.True(response.Accepted);
        Assert.Equal(2, response.ChunksReceived);
        _mockSession.Verify(s => s.InjectAudioChunkAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task StreamAudio_UnknownClient_SkipsChunks()
    {
        var chunks = new List<AudioChunkMessage>
        {
            new() { ClientId = "ghost", AudioData = Google.Protobuf.ByteString.CopyFrom(new byte[50]), SampleRate = 16000 }
        };

        var mockStream = new MockAsyncStreamReader<AudioChunkMessage>(chunks);
        var response = await _service.StreamAudio(mockStream, MakeContext());

        Assert.Equal(0, response.ChunksReceived);
        _mockSession.Verify(s => s.InjectAudioChunkAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static ServerCallContext MakeContext() =>
        new MockServerCallContext(CancellationToken.None);
}

// Helpers de test

internal class MockAsyncStreamReader<T> : IAsyncStreamReader<T>
{
    private readonly IEnumerator<T> _enumerator;

    public MockAsyncStreamReader(IEnumerable<T> items) =>
        _enumerator = items.GetEnumerator();

    public T Current => _enumerator.Current;

    public Task<bool> MoveNext(CancellationToken cancellationToken) =>
        Task.FromResult(_enumerator.MoveNext());
}

internal class MockServerCallContext : ServerCallContext
{
    private readonly CancellationToken _ct;

    public MockServerCallContext(CancellationToken ct) => _ct = ct;

    protected override string MethodCore => "/test";
    protected override string HostCore => "localhost";
    protected override string PeerCore => "127.0.0.1";
    protected override DateTime DeadlineCore => DateTime.MaxValue;
    protected override Metadata RequestHeadersCore => new();
    protected override CancellationToken CancellationTokenCore => _ct;
    protected override Metadata ResponseTrailersCore => new();
    protected override Status StatusCore { get; set; }
    protected override WriteOptions? WriteOptionsCore { get; set; }
    protected override AuthContext AuthContextCore => new("", new Dictionary<string, List<AuthProperty>>());
    protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) =>
        throw new NotImplementedException();
    protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;
}
