using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace LockIn.Core.Lcu;

/// <summary>
/// Finds the running League client and reads its lockfile.
/// </summary>
public sealed class LcuLocator(ILogger<LcuLocator> logger)
{
    public const string DefaultInstallPath = @"C:\Riot Games\League of Legends";
    private const string LockfileName = "lockfile";
    private static readonly string[] ClientProcessNames = ["LeagueClientUx", "LeagueClient"];

    /// <summary>Optional install folder set by the user, checked first.</summary>
    public string? InstallPathOverride { get; set; }

    public LcuLockfile? TryLocate()
    {
        foreach (var directory in CandidateDirectories().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var lockfile = TryRead(Path.Combine(directory, LockfileName));
            if (lockfile is not null)
            {
                return lockfile;
            }
        }

        return null;
    }

    /// <summary>Reads and validates a lockfile. Returns null if it is missing, malformed or stale.</summary>
    public LcuLockfile? TryRead(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        string content;
        try
        {
            // The client keeps the file open, so allow every kind of sharing.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            content = reader.ReadToEnd();
        }
        catch (IOException ex)
        {
            logger.LogDebug(ex, "Could not read lockfile at {Path}", path);
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            logger.LogDebug(ex, "Access denied reading lockfile at {Path}", path);
            return null;
        }

        if (!LcuLockfile.TryParse(content, out var lockfile))
        {
            logger.LogDebug("Lockfile at {Path} is malformed", path);
            return null;
        }

        if (!IsProcessAlive(lockfile.ProcessId))
        {
            logger.LogDebug("Lockfile at {Path} is stale (process {Pid} is not running)", path, lockfile.ProcessId);
            return null;
        }

        return lockfile;
    }

    private IEnumerable<string> CandidateDirectories()
    {
        if (!string.IsNullOrWhiteSpace(InstallPathOverride))
        {
            yield return InstallPathOverride;
        }

        foreach (var name in ClientProcessNames)
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                string? directory = null;
                try
                {
                    directory = Path.GetDirectoryName(process.MainModule?.FileName);
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    logger.LogDebug(ex, "Could not read the path of {Process} ({Pid})", name, process.Id);
                }
                finally
                {
                    process.Dispose();
                }

                if (directory is not null)
                {
                    yield return directory;
                }
            }
        }

        yield return DefaultInstallPath;
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
