using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace LockIn.Core.Lcu;

/// <summary>
/// Connection details for the League Client Update (LCU) local API, as written by the
/// client to its <c>lockfile</c> while it is running.
/// </summary>
/// <remarks>
/// The lockfile lives in the League install folder and has the form
/// <c>name:pid:port:password:protocol</c>, e.g. <c>LeagueClient:22540:54321:abc123:https</c>.
/// </remarks>
public sealed record LcuLockfile(string ProcessName, int ProcessId, int Port, string Password, string Protocol)
{
    /// <summary>The user name the LCU expects for HTTP basic auth.</summary>
    public const string UserName = "riot";

    public Uri BaseAddress => new($"{Protocol}://127.0.0.1:{Port}/");

    public static bool TryParse(string? content, [NotNullWhen(true)] out LcuLockfile? lockfile)
    {
        lockfile = null;
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        // The password never contains ':', so a plain split is safe.
        var parts = content.Trim().Split(':');
        if (parts.Length != 5
            || parts[0].Length == 0
            || parts[3].Length == 0
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            || port is < 1 or > 65535
            || parts[4] is not ("https" or "http"))
        {
            return false;
        }

        lockfile = new LcuLockfile(parts[0], pid, port, parts[3], parts[4]);
        return true;
    }

    /// <summary>Keeps the password out of logs and debugger views.</summary>
    public override string ToString() => $"{ProcessName} (pid {ProcessId}) on {BaseAddress}";
}
