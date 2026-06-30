namespace VoxMind.Core.Transcription;

public enum ComputeBackend
{
    CPU,

    /// <summary>Exécution sur GPU NVIDIA (CUDA). Utilisé par le moteur Qwen3-TTS natif (ggml-cuda).</summary>
    CUDA
}
