using VoxMind.Core.Vad;

namespace VoxMind.Core.SpeakerRecognition;

/// <summary>
/// Implémentation NoOp d'<see cref="ISpeakerIdentificationService"/> — enregistrée quand
/// <c>speaker_recognition.enabled = false</c>. Ne charge JAMAIS la lib native sherpa-onnx
/// (<c>libsherpa-onnx-c-api.so</c>), ce qui évite le conflit de version
/// <c>libonnxruntime.so</c> ↔ <c>onnxruntime-gpu</c> dans le conteneur GPU (le finalizer
/// sherpa provoquait sinon un <c>DllNotFoundException</c> / crash exit 139 au démarrage).
///
/// Diarisation/identification renvoient des résultats vides ; les mutations de profils
/// lèvent <see cref="NotSupportedException"/> (jamais appelées dans le conteneur TTS GPU).
/// </summary>
public sealed class DisabledSpeakerService : ISpeakerIdentificationService
{
    private const string DisabledMsg =
        "La reconnaissance du locuteur est désactivée (speaker_recognition.enabled = false).";

    public Task<IReadOnlyDictionary<int, SpeakerLabel>> DiarizeAudioAsync(
        float[] audioSamples,
        IReadOnlyList<VadSegment> vadSegments,
        CancellationToken ct = default,
        int? numSpeakers = null)
        => Task.FromResult<IReadOnlyDictionary<int, SpeakerLabel>>(new Dictionary<int, SpeakerLabel>());

    public Task<SpeakerProfile> EnrollSpeakerAsync(string name, float[] embedding, float initialConfidence, int audioDurationSeconds = 0)
        => throw new NotSupportedException(DisabledMsg);

    public Task AddEmbeddingToProfileAsync(Guid profileId, float[] embedding, float confidence)
        => throw new NotSupportedException(DisabledMsg);

    public Task<SpeakerIdentificationResult> IdentifyAsync(float[] embedding)
        => Task.FromResult(SpeakerIdentificationResult.Unknown(0f));

    public Task<SpeakerIdentificationResult> IdentifyFromAudioAsync(byte[] audioData, CancellationToken ct = default)
        => Task.FromResult(SpeakerIdentificationResult.Unknown(0f));

    public Task<float[]?> ExtractEmbeddingAsync(byte[] audioData, CancellationToken ct = default)
        => Task.FromResult<float[]?>(null);

    public Task<SpeakerProfile?> GetProfileAsync(Guid profileId)
        => Task.FromResult<SpeakerProfile?>(null);

    public Task<IReadOnlyList<SpeakerProfile>> GetAllProfilesAsync()
        => Task.FromResult<IReadOnlyList<SpeakerProfile>>(Array.Empty<SpeakerProfile>());

    public Task MergeProfilesAsync(Guid targetProfileId, Guid sourceProfileId)
        => throw new NotSupportedException(DisabledMsg);

    public Task RenameProfileAsync(Guid profileId, string newName)
        => throw new NotSupportedException(DisabledMsg);

    public Task DeleteProfileAsync(Guid profileId)
        => throw new NotSupportedException(DisabledMsg);

    public Task LinkSpeakersAsync(Guid knownProfileId, Guid unknownProfileId)
        => throw new NotSupportedException(DisabledMsg);

    public Task UpdateLastSeenAsync(Guid profileId)
        => Task.CompletedTask;

    public Task<bool> CheckHealthAsync()
        => Task.FromResult(false);

    public void Dispose() { }
}
