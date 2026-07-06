using System.IO;
using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using VoxMind.Core.Configuration;
using VoxMind.Core.Transcription;
using VoxMind.Core.Tts;
using Xunit;

namespace VoxMind.Tests.Unit.Tts;

/// <summary>
/// Tests de <see cref="Qwen3VllmTtsService"/> avec un <see cref="HttpClient"/> mocké : on valide la
/// construction du payload <c>/v1/audio/speech</c>, le décodage PCM int16→float32, le mapping de langue
/// (fr→French) et la dégradation propre quand le sidecar vLLM est injoignable.
/// </summary>
public class Qwen3VllmTtsServiceTests
{
    private static ILogger<Qwen3VllmTtsService> Logger => Mock.Of<ILogger<Qwen3VllmTtsService>>();
    private static readonly Uri BaseUri = new("http://localhost:8091");

    private sealed class StubHandler : HttpMessageHandler
    {
        public string? CapturedBody;       // corps du DERNIER POST /v1/audio/speech
        public string? CapturedPath;
        public readonly List<string> Requests = new();   // "METHOD path" de chaque requête
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            Requests.Add($"{request.Method} {path}");
            CapturedPath = path;
            if (request.Content is not null && path.EndsWith("/speech", StringComparison.Ordinal))
                CapturedBody = await request.Content.ReadAsStringAsync(ct);
            return _responder(request);
        }
    }

    private sealed class StubFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public StubFactory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false) { BaseAddress = BaseUri };
    }

    private static Qwen3VllmConfig Cfg(bool enabled = true) => new() { Enabled = enabled, BaseUrl = BaseUri.ToString() };

    private static Qwen3VllmTtsService Service(HttpMessageHandler handler, bool enabled = true)
        => new(Cfg(enabled), new StubFactory(handler), Logger);

    [Fact]
    public void Constructor_Disabled_ReportsNotLoaded_AsQwen3OnCuda()
    {
        using var svc = Service(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)), enabled: false);

        svc.Info.EngineName.Should().Be("qwen3");
        svc.Info.IsLoaded.Should().BeFalse();
        svc.Info.Backend.Should().Be(ComputeBackend.CUDA);
    }

    [Fact]
    public void Constructor_Enabled_ExposesConfiguredLanguages()
    {
        using var svc = Service(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));

        svc.Info.IsLoaded.Should().BeTrue();
        svc.Info.AvailableLanguages.Should().Contain(new[] { "fr", "en" });
    }

    [Fact]
    public async Task SynthesizeAsync_BuildsOpenAiPayload_AndDecodesPcm16()
    {
        // 4 octets = 2 échantillons int16 LE : 0x4000 = 16384 → 0.5 ; 0x8000 = -32768 → -1.0
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[] { 0x00, 0x40, 0x00, 0x80 }),
        });
        using var svc = Service(handler);

        var res = await svc.SynthesizeAsync("Bonjour le monde", "fr", instructions: "d'un ton joyeux");

        // Décodage PCM
        res.SampleRate.Should().Be(24000);
        res.Language.Should().Be("fr");
        res.Pcm.Should().HaveCount(2);
        res.Pcm[0].Should().BeApproximately(0.5f, 1e-3f);
        res.Pcm[1].Should().BeApproximately(-1.0f, 1e-3f);

        // Payload OpenAI-compatible
        handler.CapturedPath.Should().Be("/v1/audio/speech");
        using var doc = JsonDocument.Parse(handler.CapturedBody!);
        var root = doc.RootElement;
        root.GetProperty("input").GetString().Should().Be("Bonjour le monde");
        root.GetProperty("language").GetString().Should().Be("French");      // fr → French
        root.GetProperty("voice").GetString().Should().Be("Ryan");
        root.GetProperty("task_type").GetString().Should().Be("CustomVoice");
        root.GetProperty("response_format").GetString().Should().Be("pcm");
        root.GetProperty("instructions").GetString().Should().Be("d'un ton joyeux");
        root.GetProperty("stream").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task SynthesizeAsync_ServerUnreachable_ThrowsNotSupported()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("connection refused"));
        using var svc = Service(handler);

        await Assert.ThrowsAsync<NotSupportedException>(() => svc.SynthesizeAsync("Bonjour", "fr"));
    }

    [Fact]
    public async Task SynthesizeAsync_Non2xx_ThrowsNotSupported()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("model loading"),
        });
        using var svc = Service(handler);

        await Assert.ThrowsAsync<NotSupportedException>(() => svc.SynthesizeAsync("Bonjour", "fr"));
    }

    [Fact]
    public async Task SynthesizeAsync_EmptyText_ThrowsArgument()
    {
        using var svc = Service(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));

        await Assert.ThrowsAsync<ArgumentException>(() => svc.SynthesizeAsync("   ", "fr"));
    }

    [Fact]
    public async Task SynthesizeAsync_CloningBase_UploadsVoice_ThenTargetsItByName()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"voxmind_ref_{Guid.NewGuid():N}.wav");
        await File.WriteAllBytesAsync(tmp, new byte[] { 1, 2, 3, 4 });
        try
        {
            var cfg = new Qwen3VllmConfig
            {
                Enabled = true,
                BaseUrl = BaseUri.ToString(),
                Model = "Qwen/Qwen3-TTS-12Hz-1.7B-Base",
                TaskType = "Base",
                ReferenceAudioPath = tmp,
                ReferenceText = "Texte de référence.",     // → mode ICL
                ReferenceVoiceName = "ma_voix",
            };
            // GET voices → liste vide (déclenche l'upload) ; POST voices → OK ; POST speech → PCM.
            var handler = new StubHandler(req =>
            {
                var path = req.RequestUri!.AbsolutePath;
                if (path.EndsWith("/voices") && req.Method == HttpMethod.Get)
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent("{\"voices\":[],\"uploaded_voices\":[]}") };
                if (path.EndsWith("/voices"))
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"success\":true}") };
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 0x00, 0x40 }) };
            });
            using var svc = new Qwen3VllmTtsService(cfg, new StubFactory(handler), Logger);

            svc.Info.IsLoaded.Should().BeTrue();
            await svc.SynthesizeAsync("Bonjour", "fr");

            // La voix a bien été enregistrée (GET puis POST /v1/audio/voices) avant la synthèse.
            handler.Requests.Should().Contain("GET /v1/audio/voices");
            handler.Requests.Should().Contain("POST /v1/audio/voices");

            // La synthèse cible la voix par NOM, sans ref_audio ni task_type inline.
            using var doc = JsonDocument.Parse(handler.CapturedBody!);
            var root = doc.RootElement;
            root.GetProperty("voice").GetString().Should().Be("ma_voix");
            root.TryGetProperty("ref_audio", out _).Should().BeFalse();
            root.TryGetProperty("task_type", out _).Should().BeFalse();
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public async Task SynthesizeAsync_Cloning_SkipsUpload_WhenVoiceAlreadyRegistered()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"voxmind_ref_{Guid.NewGuid():N}.wav");
        await File.WriteAllBytesAsync(tmp, new byte[] { 1, 2, 3, 4 });
        try
        {
            var cfg = new Qwen3VllmConfig
            {
                Enabled = true,
                BaseUrl = BaseUri.ToString(),
                TaskType = "Base",
                ReferenceAudioPath = tmp,
                ReferenceVoiceName = "deja_la",
            };
            var handler = new StubHandler(req =>
            {
                var path = req.RequestUri!.AbsolutePath;
                if (path.EndsWith("/voices") && req.Method == HttpMethod.Get)
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent("{\"voices\":[\"deja_la\"],\"uploaded_voices\":[{\"name\":\"deja_la\"}]}") };
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 0x00, 0x40 }) };
            });
            using var svc = new Qwen3VllmTtsService(cfg, new StubFactory(handler), Logger);

            await svc.SynthesizeAsync("Bonjour", "fr");

            handler.Requests.Should().Contain("GET /v1/audio/voices");
            handler.Requests.Should().NotContain("POST /v1/audio/voices");   // déjà enregistrée → pas de ré-upload
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public async Task SynthesizeAsync_VoxtralBackend_EmptyTaskType_OmitsTaskType_SendsPresetVoice()
    {
        // Voxtral : model dédié, pas de task_type, voix = preset (fr_female).
        var cfg = new Qwen3VllmConfig
        {
            Enabled = true,
            BaseUrl = BaseUri.ToString(),
            Model = "mistralai/Voxtral-4B-TTS-2603",
            TaskType = "",
            DefaultVoice = "fr_female",
        };
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[] { 0x00, 0x40 }),
        });
        using var svc = new Qwen3VllmTtsService(cfg, new StubFactory(handler), Logger);

        await svc.SynthesizeAsync("Bonjour", "fr");

        using var doc = JsonDocument.Parse(handler.CapturedBody!);
        var root = doc.RootElement;
        root.GetProperty("model").GetString().Should().Be("mistralai/Voxtral-4B-TTS-2603");
        root.GetProperty("voice").GetString().Should().Be("fr_female");
        root.GetProperty("language").GetString().Should().Be("French");
        root.TryGetProperty("task_type", out _).Should().BeFalse();   // omis car vide
        root.TryGetProperty("ref_audio", out _).Should().BeFalse();
    }

    [Fact]
    public void Constructor_BaseTask_WithoutReference_ReportsNotLoaded()
    {
        var cfg = new Qwen3VllmConfig
        {
            Enabled = true,
            BaseUrl = BaseUri.ToString(),
            TaskType = "Base",
            ReferenceAudioPath = null,
        };
        using var svc = new Qwen3VllmTtsService(
            cfg, new StubFactory(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))), Logger);

        svc.Info.IsLoaded.Should().BeFalse();   // Base sans référence → indisponible (→ 503)
    }

    [Fact]
    public void Config_Languages_MapIsoToVllmNames()
    {
        var cfg = new Qwen3VllmConfig();

        cfg.Languages["fr"].Should().Be("French");
        cfg.Languages["en"].Should().Be("English");
        cfg.Languages.Should().HaveCount(10);
    }
}
