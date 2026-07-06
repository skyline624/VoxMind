using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using VoxMind.Core.Configuration;
using VoxMind.Core.Transcription;

namespace VoxMind.Core.Tts;

/// <summary>
/// Implémentation d'<see cref="ITtsService"/> basée sur <b>Qwen3-TTS</b> servi par un sidecar
/// <b>vLLM-omni</b> (GPU), via son endpoint OpenAI-compatible <c>POST /v1/audio/speech</c>.
///
/// <para>C'est un simple <b>client HTTP</b> : VoxMind.Api relaie le flux PCM produit par vLLM. Mesuré à
/// <b>RTF ~0,5 sur RTX 3090</b> (1.7B), TTFA basse — le seul chemin temps réel pour du Qwen3-TTS expressif
/// (les CUDA Graphs de vLLM débloquent le live, là où l'eager natif/PyTorch plafonne à RTF 2-5).</para>
///
/// <para>Le serveur rend du <b>PCM int16 LE 24 kHz mono</b> ; on le décode en float32 [-1, 1] pour
/// <see cref="TtsResult.Pcm"/>. Le champ <c>instructions</c> porte le contrôle d'émotion/style en langage
/// naturel (ex. « d'un ton enjoué »).</para>
///
/// <para>Résilience : aucune dépendance au démarrage du sidecar. Si le serveur est injoignable (il met
/// ~3-5 min à charger le modèle + capturer les CUDA graphs), la synthèse lève <see cref="NotSupportedException"/>
/// → l'endpoint répond 503, et Kokoro reste disponible.</para>
/// </summary>
public sealed class Qwen3VllmTtsService : ITtsService
{
    private const int SampleRate = 24000;     // Qwen3-TTS produit du 24 kHz mono.

    /// <summary>Nom du client HTTP nommé (configuré dans le DI avec BaseAddress + Timeout).</summary>
    public const string HttpClientName = "qwen3vllm";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Qwen3VllmConfig _config;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<Qwen3VllmTtsService> _logger;
    private readonly TtsModelInfo _info;
    private readonly bool _isCloning;            // TaskType == "Base" : clonage par voix de référence
    private readonly byte[]? _refAudioBytes;     // WAV de référence préchargé (uploadé au sidecar à la demande)
    private readonly SemaphoreSlim _registerLock = new(1, 1);
    private volatile bool _voiceRegistered;      // la voix clonée est enregistrée auprès du sidecar courant
    private readonly string _backendName;        // "qwen3" | "voxtral" : quel backend cette instance représente
    private readonly VllmBackendManager? _manager;   // état du backend actif + demande de bascule (null en test)

    public TtsModelInfo Info => _info;

    public Qwen3VllmTtsService(
        Qwen3VllmConfig config,
        IHttpClientFactory httpFactory,
        ILogger<Qwen3VllmTtsService> logger,
        string backendName = "qwen3",
        VllmBackendManager? manager = null)
    {
        _config = config;
        _httpFactory = httpFactory;
        _logger = logger;
        _backendName = string.IsNullOrWhiteSpace(backendName) ? "qwen3" : backendName.Trim().ToLowerInvariant();
        _manager = manager;

        // Clonage (TaskType = "Base") : précharge le WAV de référence (uploadé au sidecar à la 1ʳᵉ synthèse).
        _isCloning = string.Equals(config.TaskType, "Base", StringComparison.OrdinalIgnoreCase);
        if (_isCloning)
        {
            var path = config.ReferenceAudioPath;
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                _refAudioBytes = File.ReadAllBytes(path);
                _logger.LogInformation(
                    "Qwen3-TTS (vLLM) clonage Base : référence « {Path} » ({Ko} Ko), mode {Mode}, voix « {Name} ».",
                    path, _refAudioBytes.Length / 1024,
                    string.IsNullOrWhiteSpace(config.ReferenceText) ? "embedding" : "ICL + ref_text",
                    config.ReferenceVoiceName);
            }
            else
            {
                _logger.LogWarning(
                    "Qwen3-TTS (vLLM) TaskType=Base mais audio de référence introuvable ({Path}) → clonage indisponible.",
                    string.IsNullOrWhiteSpace(path) ? "(reference_audio_path non défini)" : path);
            }
        }

        var languages = config.Languages.Count > 0 ? config.Languages.Keys.ToArray() : Array.Empty<string>();
        _info = new TtsModelInfo
        {
            EngineName = _backendName,
            Backend = ComputeBackend.CUDA,    // exécuté sur GPU côté sidecar vLLM
            // En mode clonage, on exige la référence (sinon le sidecar renverrait 400) → 503 propre via l'endpoint.
            IsLoaded = config.Enabled && languages.Length > 0 && (!_isCloning || _refAudioBytes is not null),
            AvailableLanguages = languages,
        };

        if (_info.IsLoaded)
            _logger.LogInformation(
                "Qwen3-TTS (vLLM) : {N} langue(s) ({Langs}), sidecar {Url}, modèle {Model}, {VoiceOrClone}.",
                languages.Length, string.Join(", ", languages), config.BaseUrl, config.Model,
                _isCloning ? $"clonage « {config.ReferenceVoiceName} » (réf {(_refAudioBytes is null ? "MANQUANTE" : "OK")})" : $"voix {config.DefaultVoice}");
        else
            _logger.LogWarning("Qwen3-TTS (vLLM) : désactivé ou aucune langue mappée (enabled={Enabled}).", config.Enabled);
    }

    public async Task<TtsResult> SynthesizeAsync(
        string text,
        string? language = null,
        byte[]? referenceWav = null,        // ignoré en v1 (voix CustomVoice par speaker, pas de clonage)
        string? referenceText = null,       // ignoré
        string? instructions = null,
        string? voice = null,
        CancellationToken ct = default)
    {
        // Bascule de backend : si CE moteur (qwen3/voxtral) n'est pas celui servi par le sidecar, on demande
        // le rechargement (le watcher du conteneur s'en charge) et on renvoie 503 le temps du reload (~3 min).
        if (_manager is not null && !string.Equals(_backendName, _manager.ActiveBackend, StringComparison.OrdinalIgnoreCase))
        {
            _manager.RequestSwitch(_backendName);
            throw Unavailable(null, $"bascule du TTS vers « {_backendName} » demandée — rechargement du modèle en cours (~3 min), réessayez");
        }

        var (iso, vllmLang, resolvedVoice, cleaned) = Prepare(text, language, voice);

        // Clonage : s'assure que la voix de référence est enregistrée auprès du sidecar courant (idempotent).
        if (_isCloning)
            await EnsureClonedVoiceAsync(ct).ConfigureAwait(false);

        var sw = Stopwatch.StartNew();
        using var resp = await SendAsync(BuildPayload(cleaned, vllmLang, resolvedVoice, instructions, stream: false), ct).ConfigureAwait(false);
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        var pcm = DecodePcm16(bytes, bytes.Length);
        sw.Stop();

        LogRtf("synthèse", iso, cleaned.Length, pcm.Length, sw);
        return new TtsResult { Pcm = pcm, SampleRate = SampleRate, Language = iso, SynthesisLatency = sw.Elapsed };
    }

    // Pas de surcharge de SynthesizeStreamAsync : le relais de flux HTTP brut (stream:true) ET la variante
    // « per-phrase » multi-yield livraient par intermittence 0 octet au client via Results.Stream (course entre
    // l'itérateur async et la réponse ASP.NET). On garde donc l'implémentation PAR DÉFAUT d'ITtsService, qui
    // appelle SynthesizeAsync (un seul POST NON-streaming + ReadAsByteArrayAsync = fiable) et émet la réponse
    // complète en un segment. Compromis assumé : TTFA = durée de génération (≈ RTF × durée audio, ~1-3 s),
    // sans incrémental par phrase — mais fiable.

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Valide la requête, résout langue (ISO + nom vLLM) et voix, nettoie le texte.</summary>
    /// <param name="requestVoice">Voix demandée par la requête ; prime sur la voix par défaut (hors clonage).</param>
    private (string Iso, string VllmLang, string Voice, string Cleaned) Prepare(string text, string? language, string? requestVoice = null)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Le texte à synthétiser est vide.", nameof(text));
        if (!_config.Enabled || _config.Languages.Count == 0)
            throw new NotSupportedException("Qwen3-TTS (vLLM) est désactivé ou non configuré.");

        var iso = (!string.IsNullOrWhiteSpace(language) && _config.Languages.ContainsKey(language))
            ? language!
            : DefaultIso();
        var vllmLang = _config.Languages[iso];
        // Voix : la requête prime (per-requête, ex. « fr_male » Voxtral) ; sinon la voix par défaut du profil.
        // En mode clonage, BuildPayload ignore cette valeur (voix = référence enregistrée).
        var voice = string.IsNullOrWhiteSpace(requestVoice) ? _config.DefaultVoice : requestVoice!.Trim();

        var cleaned = TtsTextSegmenter.CleanText(text);
        if (string.IsNullOrWhiteSpace(cleaned))
            cleaned = text.Trim();

        return (iso, vllmLang, voice, cleaned);
    }

    private string DefaultIso()
        => _config.Languages.ContainsKey(_config.DefaultLanguage) ? _config.DefaultLanguage : _config.Languages.Keys.First();

    private HttpContent BuildPayload(string input, string vllmLang, string voice, string? instructions, bool stream)
    {
        var instr = string.IsNullOrWhiteSpace(instructions) ? _config.DefaultInstruction : instructions;
        var payload = new Dictionary<string, object?>
        {
            ["model"] = _config.Model,
            ["input"] = input,
            ["language"] = vllmLang,
            ["response_format"] = "pcm",
            ["stream"] = stream,
            ["max_new_tokens"] = _config.MaxNewTokens,
            ["instructions"] = string.IsNullOrWhiteSpace(instr) ? null : instr,
        };

        if (_isCloning)
            // Clonage : la voix de référence a été enregistrée (upload) ; on la cible par NOM. Pas de task_type
            // ni de ref_audio inline (l'ICL inline plante le moteur : « ref_audio … missing ref_code »).
            payload["voice"] = _config.ReferenceVoiceName;
        else
        {
            // task_type : Qwen3 (CustomVoice/VoiceDesign) l'exige ; Voxtral ne l'utilise pas → on l'omet si vide.
            if (!string.IsNullOrWhiteSpace(_config.TaskType))
                payload["task_type"] = _config.TaskType;
            payload["voice"] = voice;                                  // preset (Qwen3 speaker ou Voxtral « fr_female »…)
        }

        return JsonContent.Create(payload, options: JsonOpts);
    }

    /// <summary>POST <c>/v1/audio/speech</c> avec gestion d'erreur réseau → <see cref="NotSupportedException"/> (503).</summary>
    private async Task<HttpResponseMessage> SendAsync(HttpContent content, CancellationToken ct)
    {
        var http = _httpFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/audio/speech") { Content = content };
        HttpResponseMessage resp;
        try
        {
            resp = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                             .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _voiceRegistered = false;   // sidecar injoignable/redémarré → la voix uploadée est perdue, ré-enregistrer
            throw Unavailable(ex);
        }

        if (!resp.IsSuccessStatusCode)
        {
            var body = await SafeReadAsync(resp, ct).ConfigureAwait(false);
            resp.Dispose();
            _voiceRegistered = false;   // ex. « voix inconnue » après un redémarrage du sidecar → ré-enregistrer
            throw Unavailable(null, $"HTTP {(int)resp.StatusCode} {resp.StatusCode}: {body}");
        }
        return resp;
    }

    private NotSupportedException Unavailable(Exception? inner, string? detail = null)
        => new($"Qwen3-TTS (vLLM) injoignable sur {_config.BaseUrl} ({detail ?? inner?.Message}). " +
               "Le sidecar vLLM démarre peut-être encore (chargement modèle + capture CUDA graphs).", inner);

    /// <summary>
    /// Enregistre (idempotent) la voix clonée auprès du sidecar via <c>POST /v1/audio/voices</c> — qui calcule
    /// le <c>ref_code</c> (requis par l'ICL) et stocke l'<c>embedding</c> + le <c>ref_text</c>. Après quoi la
    /// synthèse ne fait que cibler la voix par nom. Le serveur garde les voix EN MÉMOIRE : un redémarrage du
    /// sidecar les perd → on revérifie tant que <see cref="_voiceRegistered"/> est faux (remis à faux sur erreur).
    /// </summary>
    private async Task EnsureClonedVoiceAsync(CancellationToken ct)
    {
        if (_voiceRegistered || _refAudioBytes is null)
            return;

        await _registerLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_voiceRegistered)
                return;

            var http = _httpFactory.CreateClient(HttpClientName);

            // Déjà enregistrée auprès de ce sidecar ?
            try
            {
                using var list = await http.GetAsync("/v1/audio/voices", ct).ConfigureAwait(false);
                if (list.IsSuccessStatusCode)
                {
                    var json = await list.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    if (json.Contains($"\"{_config.ReferenceVoiceName}\"", StringComparison.Ordinal))
                    {
                        _voiceRegistered = true;
                        return;
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                throw Unavailable(ex);   // sidecar pas encore prêt → 503 propre
            }

            // Upload de la voix (multipart) : audio_sample + name + consent (+ ref_text pour l'ICL).
            using var form = new MultipartFormDataContent();
            var audio = new ByteArrayContent(_refAudioBytes);
            audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            form.Add(audio, "audio_sample", "reference.wav");
            form.Add(new StringContent(_config.ReferenceVoiceName), "name");
            form.Add(new StringContent(_config.Consent), "consent");
            if (!string.IsNullOrWhiteSpace(_config.ReferenceText))
                form.Add(new StringContent(_config.ReferenceText), "ref_text");

            HttpResponseMessage up;
            try
            {
                up = await http.PostAsync("/v1/audio/voices", form, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                throw Unavailable(ex);
            }

            using (up)
            {
                if (!up.IsSuccessStatusCode)
                    throw Unavailable(null, $"upload voix HTTP {(int)up.StatusCode}: {await SafeReadAsync(up, ct).ConfigureAwait(false)}");
            }

            _voiceRegistered = true;
            _logger.LogInformation(
                "Qwen3-TTS (vLLM) voix clonée « {Name} » enregistrée auprès du sidecar (mode {Mode}).",
                _config.ReferenceVoiceName, string.IsNullOrWhiteSpace(_config.ReferenceText) ? "embedding" : "ICL");
        }
        finally
        {
            _registerLock.Release();
        }
    }

    private static async Task<string> SafeReadAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try { var s = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false); return s.Length > 200 ? s[..200] : s; }
        catch { return "(corps illisible)"; }
    }

    /// <summary>Décode un buffer PCM int16 LE complet en float32 [-1, 1].</summary>
    private static float[] DecodePcm16(byte[] bytes, int count)
    {
        int n = count / 2;
        var pcm = new float[n];
        for (int i = 0; i < n; i++)
        {
            short s = (short)(bytes[i * 2] | (bytes[i * 2 + 1] << 8));
            pcm[i] = s / 32768f;
        }
        return pcm;
    }


    private void LogRtf(string what, string lang, int chars, long samples, Stopwatch sw)
    {
        double durationSec = samples / (double)SampleRate;
        double rtf = durationSec > 0 ? sw.Elapsed.TotalSeconds / durationSec : 0;
        _logger.LogInformation(
            "Qwen3-TTS (vLLM) {What} {Lang} : {Chars} char → {Samples} samples ({Duration:F2}s) en {Latency} ms (RTF {Rtf:F3}).",
            what, lang, chars, samples, durationSec, sw.ElapsedMilliseconds, rtf);
    }

    public void Dispose()
    {
        // HttpClient géré par IHttpClientFactory ; on libère le verrou d'enregistrement de voix.
        _registerLock.Dispose();
    }
}
