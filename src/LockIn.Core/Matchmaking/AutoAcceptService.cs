using LockIn.Core.Gameflow;
using LockIn.Core.Lcu;
using LockIn.Core.Settings;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LockIn.Core.Matchmaking;

/// <summary>The client's ready-check resource (<c>/lol-matchmaking/v1/ready-check</c>).</summary>
public sealed record ReadyCheck(string? State, string? PlayerResponse, double Timer)
{
    /// <summary>A match was found and we haven't answered yet.</summary>
    public bool AwaitingOurResponse => State == "InProgress" && PlayerResponse == "None";
}

/// <summary>Accepts found matches after the configured delay.</summary>
public sealed class AutoAcceptService(
    LcuConnection connection,
    GameflowMonitor gameflow,
    SettingsStore settings,
    TimeProvider time,
    ILogger<AutoAcceptService> logger) : IHostedService
{
    private const string AcceptPath = "lol-matchmaking/v1/ready-check/accept";
    private const string ReadyCheckPath = "lol-matchmaking/v1/ready-check";

    private int _handling;
    private string? _lastState;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        connection.EventReceived += OnEvent;
        gameflow.PhaseChanged += OnPhaseChanged;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        connection.EventReceived -= OnEvent;
        gameflow.PhaseChanged -= OnPhaseChanged;
        return Task.CompletedTask;
    }

    private void OnPhaseChanged(object? sender, GameflowPhaseChangedEventArgs e)
    {
        // Covers LockIn starting (or reconnecting) while a ready check is already on screen.
        if (e.Current == GameflowMonitor.ReadyCheck && connection.Client is { } client)
        {
            _ = CheckCurrentAsync(client);
        }
    }

    private async Task CheckCurrentAsync(LcuClient client)
    {
        try
        {
            var readyCheck = await client.GetAsync<ReadyCheck>(ReadyCheckPath).ConfigureAwait(false);
            if (readyCheck is not null)
            {
                OnReadyCheck(readyCheck);
            }
        }
        catch (LcuRequestException ex)
        {
            logger.LogDebug("Could not read the ready check: {Message}", ex.Message);
        }
    }

    private void OnEvent(object? sender, LcuEvent e)
    {
        if (e.Uri != LcuEndpoints.ReadyCheck)
        {
            return;
        }

        if (e.IsDelete)
        {
            _lastState = null;
            return;
        }

        if (e.DataAs<ReadyCheck>() is { } readyCheck)
        {
            OnReadyCheck(readyCheck);
        }
    }

    private void OnReadyCheck(ReadyCheck readyCheck)
    {
        LogOutcome(readyCheck);
        if (readyCheck.AwaitingOurResponse && Interlocked.Exchange(ref _handling, 1) == 0)
        {
            _ = AcceptAsync();
        }
    }

    private void LogOutcome(ReadyCheck readyCheck)
    {
        var state = $"{readyCheck.State}/{readyCheck.PlayerResponse}";
        if (state == Interlocked.Exchange(ref _lastState, state))
        {
            return;
        }

        switch (readyCheck.State, readyCheck.PlayerResponse)
        {
            case ("EveryoneReady", _):
                logger.LogInformation("Everyone accepted — heading to champ select");
                break;
            case ("StrangerNotReady" or "PartyNotReady", "Accepted"):
                logger.LogInformation("Someone else declined the match; you're back in the queue");
                break;
            case (_, "Declined"):
                logger.LogInformation("The match was declined");
                break;
        }
    }

    private async Task AcceptAsync()
    {
        try
        {
            var s = settings.Snapshot();
            if (!s.AutoAcceptEnabled)
            {
                logger.LogInformation("Match found — auto-accept is off, leaving it to you");
                return;
            }

            if (connection.Client is not { } client)
            {
                return;
            }

            if (s.AcceptDelaySeconds > 0)
            {
                logger.LogInformation("Match found — accepting in {Delay:0.#} s", s.AcceptDelaySeconds);
                await Task.Delay(TimeSpan.FromSeconds(s.AcceptDelaySeconds), time).ConfigureAwait(false);

                // Things may have changed while we waited.
                if (!settings.Snapshot().AutoAcceptEnabled)
                {
                    logger.LogInformation("Auto-accept was turned off while waiting; not accepting");
                    return;
                }

                var current = await client.GetAsync<ReadyCheck>(ReadyCheckPath).ConfigureAwait(false);
                if (current is not { AwaitingOurResponse: true })
                {
                    logger.LogInformation("The ready check was already answered or ended ({State}, {Response}); not accepting",
                        current?.State ?? "gone", current?.PlayerResponse ?? "-");
                    return;
                }
            }
            else
            {
                logger.LogInformation("Match found — accepting");
            }

            await client.PostAsync(AcceptPath).ConfigureAwait(false);
            logger.LogInformation("Accepted the match");
        }
        catch (LcuRequestException ex)
        {
            logger.LogWarning("Could not accept the match: {Message}", ex.Message);
        }
        finally
        {
            Volatile.Write(ref _handling, 0);
        }
    }
}
