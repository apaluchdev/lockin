using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace LockIn.App.Services;

/// <summary>Adds or removes LockIn from the current user's "run at sign-in" list (no admin rights needed).</summary>
public sealed class StartupRegistration(ILogger<StartupRegistration> logger)
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "LockIn";
    public const string TrayArgument = "--tray";

    public void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            var command = $"\"{Environment.ProcessPath}\" {TrayArgument}";
            var existing = key.GetValue(ValueName) as string;

            if (enabled && existing != command)
            {
                key.SetValue(ValueName, command);
                logger.LogInformation("LockIn will start with Windows ({Command})", command);
            }
            else if (!enabled && existing is not null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                logger.LogInformation("LockIn will no longer start with Windows");
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            logger.LogWarning("Could not update the Start with Windows setting: {Message}", ex.Message);
        }
    }
}
