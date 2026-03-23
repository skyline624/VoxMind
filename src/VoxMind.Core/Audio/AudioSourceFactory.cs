using Microsoft.Extensions.Logging;

namespace VoxMind.Core.Audio;

/// <summary>
/// Crée la source audio appropriée selon le type demandé ("live" ou "file").
/// </summary>
public class AudioSourceFactory
{
    private readonly IAudioCapture _liveCapture;
    private readonly ILoggerFactory _loggerFactory;

    public AudioSourceFactory(IAudioCapture liveCapture, ILoggerFactory loggerFactory)
    {
        _liveCapture = liveCapture;
        _loggerFactory = loggerFactory;
    }

    /// <summary>
    /// Retourne la source audio correspondant au type demandé.
    /// </summary>
    /// <param name="sourceType">"live" (microphone) ou "file" (fichier audio).</param>
    /// <param name="sourcePath">Chemin du fichier — obligatoire si sourceType = "file".</param>
    public IAudioCapture Create(string sourceType, string? sourcePath = null)
    {
        return sourceType.ToLowerInvariant() switch
        {
            "file" => new FileAudioSource(
                sourcePath ?? throw new ArgumentException(
                    "source_path est obligatoire quand source_type = \"file\".", nameof(sourcePath)),
                _loggerFactory.CreateLogger<FileAudioSource>()),
            _ => _liveCapture
        };
    }
}
