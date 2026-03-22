namespace VoxMind.Core.Transcription;

public enum ModelSize
{
    Tiny,    // ~75 MB  — temps réel CPU
    Base,    // ~140 MB — temps réel recommandé
    Small,   // ~465 MB — balance qualité/vitesse
    Medium,  // ~1.5 GB — haute précision
    Large    // ~3 GB   — précision maximale
}
