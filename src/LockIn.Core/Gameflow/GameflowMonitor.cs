using LockIn.Core.Lcu;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LockIn.Core.Gameflow;

public sealed class GameflowPhaseChangedEventArgs(string previous, string current) : EventArgs
{
    public string Previous { get; } = previous;

    public string Current { get; } = current;
}

/// <summary>
/// Tracks the client's gameflow phase (Lobby, Matchmaking, ReadyCheck, ChampSelect, InProgress…).
/// </summary>
public sealed class GameflowMonitor(LcuConnection connection, ILogger<GameflowMonitor> logger) : IHostedService
{
    public const string None = "None";
    public const string ReadyCheck = "ReadyCheck";
    public const string ChampSelect = "ChampSelect";

    private readonly Lock _gate = new();

    public string Phase { get; private set; } = None;

    /// <summary>Raised on a background thread.</summary>
    public event EventHandler<GameflowPhaseChangedEventArgs>? PhaseChanged;

    public static string Describe(string phase) => phase switch
    {
        None => "Idle",
        "Lobby" => "In lobby",
        "Matchmaking" => "In queue",
        ReadyCheck => "Match found",
        ChampSelect => "Champ select",
        "GameStart" or "InProgress" => "In game",
        "Reconnect" => "Reconnect needed",
        "WaitingForStats" or "PreEndOfGame" or "EndOfGame" => "Post-game",
        _ => phase,
    };

    public Task StartAsync(CancellationToken cancellationToken)
    {
        connection.StateChanged += OnConnectionStateChanged;
        connection.EventReceived += OnEvent;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        connection.StateChanged -= OnConnectionStateChanged;
        connection.EventReceived -= OnEvent;
        return Task.CompletedTask;
    }

    private void OnConnectionStateChanged(object? sender, EventArgs e)
    {
        if (connection.Client is { } client)
        {
            _ = LoadInitialPhaseAsync(client);
        }
        else
        {
            SetPhase(None);
        }
    }

    private async Task LoadInitialPhaseAsync(LcuClient client)
    {
        try
        {
            var phase = await client.GetAsync<string>(LcuEndpoints.GameflowPhase).ConfigureAwait(false);
            SetPhase(phase ?? None);
        }
        catch (LcuRequestException ex)
        {
            logger.LogDebug("Could not read the gameflow phase: {Message}", ex.Message);
        }
    }

    private void OnEvent(object? sender, LcuEvent e)
    {
        if (e.Uri == LcuEndpoints.GameflowPhase)
        {
            SetPhase(e.DataAs<string>() ?? None);
        }
    }

    private void SetPhase(string phase)
    {
        string previous;
        lock (_gate)
        {
            if (Phase == phase)
            {
                return;
            }

            previous = Phase;
            Phase = phase;
        }

        logger.LogInformation("Client: {Previous} → {Current}", Describe(previous), Describe(phase));
        PhaseChanged?.Invoke(this, new GameflowPhaseChangedEventArgs(previous, phase));
    }
}
