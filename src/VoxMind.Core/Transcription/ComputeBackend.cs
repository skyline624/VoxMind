using System.Runtime.InteropServices;

namespace VoxMind.Core.Transcription;

public enum ComputeBackend
{
    CPU,
    CUDA,   // NVIDIA GPU
    ROCm,   // AMD GPU Linux
    MPS,    // Apple Silicon
    Auto    // Détection automatique
}

public static class ComputeBackendDetector
{
    public static ComputeBackend DetectBestAvailable()
    {
        // Vérification CUDA via variable d'environnement ou présence de l'outil
        if (IsCudaAvailable())
            return ComputeBackend.CUDA;

        // ROCm (AMD GPU Linux)
        if (IsRocmAvailable())
            return ComputeBackend.ROCm;

        return ComputeBackend.CPU;
    }

    public static string GetDeviceString(ComputeBackend backend) => backend switch
    {
        ComputeBackend.CUDA => "cuda",
        ComputeBackend.MPS  => "mps",
        ComputeBackend.ROCm => "hip",
        ComputeBackend.CPU  => "cpu",
        ComputeBackend.Auto => GetDeviceString(DetectBestAvailable()),
        _                   => "cpu"
    };

    private static bool IsCudaAvailable()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux) &&
            !RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return false;

        // Vérifier la présence de nvidia-smi ou variable CUDA_VISIBLE_DEVICES
        return File.Exists("/usr/bin/nvidia-smi") ||
               File.Exists("/usr/local/cuda/bin/nvcc") ||
               !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CUDA_HOME"));
    }

    private static bool IsRocmAvailable()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return false;

        return Directory.Exists("/opt/rocm") ||
               !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ROCM_PATH"));
    }
}
