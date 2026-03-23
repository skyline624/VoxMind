using System.Text.Json;

namespace VoxMind.Core.Configuration;

public static class ConfigurationLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static AppConfiguration Load(string configPath)
    {
        if (!File.Exists(configPath))
            throw new FileNotFoundException($"Fichier de configuration introuvable : {configPath}");

        var json = File.ReadAllText(configPath);
        var config = JsonSerializer.Deserialize<AppConfiguration>(json, JsonOptions)
            ?? throw new InvalidOperationException("Impossible de désérialiser la configuration.");

        Validate(config);
        return config;
    }

    public static AppConfiguration LoadOrDefault()
    {
        var env = Environment.GetEnvironmentVariable("VOXMIND_DATA_DIR");

        var candidates = new[]
        {
            env != null ? Path.Combine(env, "config", "config.json") : null,
            Path.Combine(AppContext.BaseDirectory, "voice_data", "config", "config.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "voice_data", "config", "config.json")
        }.Where(p => p != null).Cast<string>().ToArray();

        foreach (var path in candidates)
        {
            if (File.Exists(path))
                return Load(path);
        }

        return new AppConfiguration();
    }

    private static void Validate(AppConfiguration config)
    {
        if (config.Audio.DefaultSampleRate <= 0)
            throw new InvalidOperationException("audio.default_sample_rate doit être positif.");

        if (config.Ml.SpeakerRecognition.ConfidenceThreshold is < 0 or > 1)
            throw new InvalidOperationException("ml.speaker_recognition.confidence_threshold doit être entre 0 et 1.");
    }
}
