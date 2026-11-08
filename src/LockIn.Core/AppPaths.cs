namespace LockIn.Core;

/// <summary>Where LockIn keeps its files.</summary>
public static class AppPaths
{
    /// <summary>%APPDATA%\LockIn — settings (roams with the Windows profile).</summary>
    public static string RoamingDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LockIn");

    /// <summary>%LOCALAPPDATA%\LockIn — caches and logs.</summary>
    public static string LocalDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LockIn");

    public static string SettingsFile { get; } = Path.Combine(RoamingDirectory, "settings.json");

    public static string LogDirectory { get; } = Path.Combine(LocalDirectory, "logs");

    public static string CacheDirectory { get; } = Path.Combine(LocalDirectory, "cache");
}
