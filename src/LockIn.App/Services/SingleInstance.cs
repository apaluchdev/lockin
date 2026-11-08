namespace LockIn.App.Services;

/// <summary>
/// Keeps LockIn to one instance per Windows user. A second launch asks the first to show its window, then exits.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _showSignal;
    private RegisteredWaitHandle? _registration;

    public SingleInstance()
    {
        var id = $"LockIn-{Environment.UserName}";
        _mutex = new Mutex(initiallyOwned: true, $@"Local\{id}-instance", out var createdNew);
        IsFirstInstance = createdNew;
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{id}-show");
    }

    public bool IsFirstInstance { get; }

    /// <summary>Asks the already-running instance to show itself.</summary>
    public void SignalFirstInstance() => _showSignal.Set();

    /// <summary>Runs <paramref name="onShowRequested"/> (on a thread-pool thread) whenever another launch signals us.</summary>
    public void ListenForShowRequests(Action onShowRequested) =>
        _registration = ThreadPool.RegisterWaitForSingleObject(
            _showSignal, (_, _) => onShowRequested(), null, Timeout.Infinite, executeOnlyOnce: false);

    public void Dispose()
    {
        _registration?.Unregister(null);
        if (IsFirstInstance)
        {
            _mutex.ReleaseMutex();
        }

        _mutex.Dispose();
        _showSignal.Dispose();
    }
}
