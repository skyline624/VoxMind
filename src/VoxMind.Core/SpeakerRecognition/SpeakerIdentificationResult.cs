namespace VoxMind.Core.SpeakerRecognition;

public class SpeakerIdentificationResult
{
    public bool IsIdentified { get; }
    public Guid? ProfileId { get; }
    public string? SpeakerName { get; }
    public float Confidence { get; }
    public float Threshold { get; }

    /// <summary>True si confidence >= threshold mais pas encore dans la base (nouveau locuteur)</summary>
    public bool IsNewSpeaker => !IsIdentified && Confidence >= Threshold;

    public SpeakerIdentificationResult(bool isIdentified, Guid? profileId, string? speakerName, float confidence, float threshold)
    {
        IsIdentified = isIdentified;
        ProfileId = profileId;
        SpeakerName = speakerName;
        Confidence = confidence;
        Threshold = threshold;
    }

    public static SpeakerIdentificationResult Unknown(float threshold) =>
        new(false, null, null, 0f, threshold);
}
