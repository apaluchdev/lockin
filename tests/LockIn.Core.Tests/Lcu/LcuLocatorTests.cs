using System.Diagnostics;
using LockIn.Core.Lcu;
using Microsoft.Extensions.Logging.Abstractions;

namespace LockIn.Core.Tests.Lcu;

public sealed class LcuLocatorTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("lockin-tests-").FullName;
    private readonly LcuLocator _locator = new(NullLogger<LcuLocator>.Instance);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Reads_a_lockfile_for_a_running_process()
    {
        var path = Path.Combine(_dir, "lockfile");
        File.WriteAllText(path, $"LeagueClient:{Environment.ProcessId}:5555:pw:https");

        var lockfile = _locator.TryRead(path);

        Assert.NotNull(lockfile);
        Assert.Equal(5555, lockfile.Port);
    }

    [Fact]
    public void Ignores_a_stale_lockfile()
    {
        // Start and wait for a short-lived process so its PID is known to be dead.
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit") { CreateNoWindow = true })!;
        process.WaitForExit();
        var path = Path.Combine(_dir, "lockfile");
        File.WriteAllText(path, $"LeagueClient:{process.Id}:5555:pw:https");

        Assert.Null(_locator.TryRead(path));
    }

    [Fact]
    public void Reads_a_lockfile_held_open_by_another_writer()
    {
        var path = Path.Combine(_dir, "lockfile");
        using var holder = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        holder.Write(System.Text.Encoding.UTF8.GetBytes($"LeagueClient:{Environment.ProcessId}:5555:pw:https"));
        holder.Flush();

        Assert.NotNull(_locator.TryRead(path));
    }

    [Fact]
    public void Install_path_override_is_used()
    {
        File.WriteAllText(Path.Combine(_dir, "lockfile"), $"LeagueClient:{Environment.ProcessId}:6666:pw:https");
        _locator.InstallPathOverride = _dir;

        Assert.Equal(6666, _locator.TryLocate()?.Port);
    }
}
