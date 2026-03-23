namespace VoxMind.Core.Audio;

public interface IAudioCapture : IDisposable
{
    /// <summary>Énumère les sources audio disponibles sur le système</summary>
    Task<IReadOnlyList<AudioDeviceInfo>> GetAvailableSourcesAsync();

    /// <summary>Démarre la capture audio</summary>
    Task StartCaptureAsync(AudioConfiguration config, CancellationToken ct = default);

    /// <summary>Arrête la capture audio</summary>
    Task StopCaptureAsync();

    /// <summary>Événement déclenché pour chaque chunk audio capturé</summary>
    event EventHandler<AudioChunkEventArgs>? AudioChunkReceived;

    /// <summary>Indique si la capture est active</summary>
    bool IsCapturing { get; }

    /// <summary>True = flux continu (micro), False = source finie (fichier)</summary>
    bool IsLive { get; }

    /// <summary>Configuration actuelle</summary>
    AudioConfiguration? CurrentConfig { get; }
}
