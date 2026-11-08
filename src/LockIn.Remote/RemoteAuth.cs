using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace LockIn.Remote;

/// <summary>
/// Who may use the phone page: anyone presenting the secret key (carried in the QR-code link), or who
/// trades the 6-digit pairing code for it. Wrong codes are rate-limited per address.
/// </summary>
public sealed class RemoteAuth(TimeProvider time)
{
    private const int MaxFailures = 5;
    private static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan Lockout = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<IPAddress, Attempts> _attempts = new();
    private byte[] _keyBytes = [];

    public string Key { get; private set; } = "";

    /// <summary>Six digits shown in the desktop app, for typing the address by hand.</summary>
    public string PairingCode { get; private set; } = NewPairingCode();

    public static string NewKey() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string NewPairingCode() =>
        RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);

    public void SetKey(string key)
    {
        Key = key;
        _keyBytes = Encoding.UTF8.GetBytes(key);
    }

    public void NewCode() => PairingCode = NewPairingCode();

    public bool IsValidKey(string? candidate) =>
        !string.IsNullOrEmpty(candidate)
        && _keyBytes.Length > 0
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(candidate), _keyBytes);

    public enum PairResult
    {
        Ok,
        WrongCode,
        TooManyAttempts,
    }

    /// <summary>Checks a pairing code from <paramref name="address"/>.</summary>
    public PairResult TryPair(IPAddress address, string? code)
    {
        var now = time.GetUtcNow();
        var attempts = _attempts.GetOrAdd(address, _ => new Attempts());
        lock (attempts)
        {
            if (attempts.LockedUntil > now)
            {
                return PairResult.TooManyAttempts;
            }

            var normalized = new string((code ?? "").Where(char.IsDigit).ToArray());
            if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(normalized), Encoding.ASCII.GetBytes(PairingCode)))
            {
                attempts.Failures.Clear();
                return PairResult.Ok;
            }

            attempts.Failures.RemoveAll(t => now - t > FailureWindow);
            attempts.Failures.Add(now);
            if (attempts.Failures.Count >= MaxFailures)
            {
                attempts.LockedUntil = now + Lockout;
                attempts.Failures.Clear();
                return PairResult.TooManyAttempts;
            }

            return PairResult.WrongCode;
        }
    }

    private sealed class Attempts
    {
        public List<DateTimeOffset> Failures { get; } = [];

        public DateTimeOffset LockedUntil { get; set; }
    }
}
