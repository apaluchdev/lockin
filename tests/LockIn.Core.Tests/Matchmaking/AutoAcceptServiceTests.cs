using LockIn.Core.Hosting;
using LockIn.Core.Lcu;
using LockIn.Core.Matchmaking;
using LockIn.Core.Tests.Fakes;

namespace LockIn.Core.Tests.Matchmaking;

public class AutoAcceptServiceTests
{
    private const string AcceptPath = "/lol-matchmaking/v1/ready-check/accept";

    private static Task<TestHost> StartAsync(Action<Core.Settings.AppSettings>? configure = null) =>
        TestHost.StartAsync(configure, s => s.AddHostedSingleton<AutoAcceptService>());

    [Fact]
    public async Task Accepts_a_found_match()
    {
        await using var host = await StartAsync();

        await host.Client.PushAsync(LcuEndpoints.ReadyCheck, new { state = "InProgress", playerResponse = "None", timer = 1 });

        await host.WaitUntilAsync(() => host.Client.RequestsTo("POST", AcceptPath).Any(), "accept the match");
        Assert.Contains(host.Logs.Messages, m => m.Contains("Accepted the match", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Does_nothing_when_auto_accept_is_off()
    {
        await using var host = await StartAsync(s => s.AutoAcceptEnabled = false);

        await host.Client.PushAsync(LcuEndpoints.ReadyCheck, new { state = "InProgress", playerResponse = "None", timer = 1 });

        await host.WaitUntilAsync(() => host.Logs.Messages.Any(m => m.Contains("auto-accept is off", StringComparison.Ordinal)), "log that it is off");
        Assert.Empty(host.Client.RequestsTo("POST", AcceptPath));
    }

    [Fact]
    public async Task Does_not_accept_if_the_player_answered_during_the_delay()
    {
        await using var host = await StartAsync(s => s.AcceptDelaySeconds = 0.3);
        host.Client.SetGet(LcuEndpoints.ReadyCheck, new { state = "InProgress", playerResponse = "Declined", timer = 2 });

        await host.Client.PushAsync(LcuEndpoints.ReadyCheck, new { state = "InProgress", playerResponse = "None", timer = 1 });

        await host.WaitUntilAsync(() => host.Logs.Messages.Any(m => m.Contains("already answered", StringComparison.Ordinal)), "notice the manual answer");
        Assert.Empty(host.Client.RequestsTo("POST", AcceptPath));
    }

    [Fact]
    public async Task Accepts_after_the_configured_delay()
    {
        await using var host = await StartAsync(s => s.AcceptDelaySeconds = 0.3);
        host.Client.SetGet(LcuEndpoints.ReadyCheck, new { state = "InProgress", playerResponse = "None", timer = 2 });
        var pushed = DateTime.UtcNow;

        await host.Client.PushAsync(LcuEndpoints.ReadyCheck, new { state = "InProgress", playerResponse = "None", timer = 1 });

        await host.WaitUntilAsync(() => host.Client.RequestsTo("POST", AcceptPath).Any(), "accept the match");
        Assert.True(DateTime.UtcNow - pushed >= TimeSpan.FromMilliseconds(280));
    }
}
