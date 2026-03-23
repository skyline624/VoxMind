namespace VoxMind.CLI;

/// <summary>
/// Utility class for managing VoxMind data directories.
/// Ensures all required directories exist at startup.
/// </summary>
public static class VoxMindDirectories
{
    /// <summary>
    /// Ensures all required VoxMind directories exist.
    /// Creates profiles/, sessions/, shared/, logs/, config/ subdirectories.
    /// </summary>
    /// <returns>The base data directory path.</returns>
    public static string EnsureDirectories()
    {
        var baseDir = GetDataDirectory();
        Directory.CreateDirectory(Path.Combine(baseDir, "profiles"));
        Directory.CreateDirectory(Path.Combine(baseDir, "sessions"));
        Directory.CreateDirectory(Path.Combine(baseDir, "shared"));
        Directory.CreateDirectory(Path.Combine(baseDir, "logs"));
        Directory.CreateDirectory(Path.Combine(baseDir, "config"));
        return baseDir;
    }

    /// <summary>
    /// Gets the VoxMind data directory, checking in order:
    /// 1. VOXMIND_DATA_DIR environment variable
    /// 2. ./voice_data relative to executable
    /// 3. ./voice_data in current working directory (dev mode)
    /// 4. OS-specific default (Linux: ~/.local/share/VoxMind, Windows: %AppData%/VoxMind)
    /// </summary>
    public static string GetDataDirectory()
    {
        // 1. VOXMIND_DATA_DIR env variable
        var env = Environment.GetEnvironmentVariable("VOXMIND_DATA_DIR");
        if (!string.IsNullOrEmpty(env))
        {
            Directory.CreateDirectory(env);
            return env;
        }

        // 2. Legacy path for existing users: ~/voice_data
        var home = Environment.GetEnvironmentVariable("HOME");
        if (!string.IsNullOrEmpty(home))
        {
            var legacyPath = Path.Combine(home, "voice_data");
            if (Directory.Exists(legacyPath))
                return legacyPath;
        }

        // 3. ./voice_data relative to executable
        var relativeVoiceData = Path.Combine(AppContext.BaseDirectory, "voice_data");
        if (Directory.Exists(relativeVoiceData))
            return relativeVoiceData;

        // 4. Current directory for dev
        var cwdVoiceData = Path.Combine(Directory.GetCurrentDirectory(), "voice_data");
        if (Directory.Exists(cwdVoiceData))
            return cwdVoiceData;

        // 5. Create default in OS-specific location
        return CreateDefaultDataDirectory();
    }

    private static string CreateDefaultDataDirectory()
    {
        string baseDir;
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            var home = Environment.GetEnvironmentVariable("HOME") ?? "/tmp";
            baseDir = Path.Combine(home, ".local", "share", "VoxMind");
        }
        else
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            baseDir = Path.Combine(appData, "VoxMind");
        }
        Directory.CreateDirectory(baseDir);
        return baseDir;
    }
}