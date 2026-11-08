using LockIn.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace LockIn.Core.Tests.Settings;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("lockin-settings-").FullName;

    private string SettingsPath => Path.Combine(_dir, "settings.json");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Round_trips_every_kind_of_setting()
    {
        using (var store = new SettingsStore(SettingsPath, NullLogger<SettingsStore>.Instance))
        {
            store.Update(s =>
            {
                s.AcceptDelaySeconds = 1.5;
                s.Picks.Lists[Role.Middle] = [103, 61];
                s.Bans.Lists[Role.Any] = [157];
                s.Loadouts.Champions[103] = new ChampionLoadout { RunePageId = 42, RunePageName = "Ahri", Spell1Id = 4, Spell2Id = 14 };
                s.Loadouts.FlashKey = FlashKey.F;
            });
        }

        using var reloaded = new SettingsStore(SettingsPath, NullLogger<SettingsStore>.Instance);
        var s = reloaded.Snapshot();

        Assert.Equal(1.5, s.AcceptDelaySeconds);
        Assert.Equal([103, 61], s.Picks.Lists[Role.Middle]);
        Assert.Equal([157], s.Bans.Lists[Role.Any]);
        Assert.Equal(42, s.Loadouts.Champions[103].RunePageId);
        Assert.Equal(FlashKey.F, s.Loadouts.FlashKey);
        Assert.Contains("\"middle\"", File.ReadAllText(SettingsPath), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Snapshots_are_isolated_from_later_updates()
    {
        using var store = new SettingsStore(SettingsPath, NullLogger<SettingsStore>.Instance);
        var before = store.Snapshot();

        store.Update(s => s.Picks.Lists[Role.Top] = [86]);

        Assert.False(before.Picks.Lists.ContainsKey(Role.Top));
        Assert.Equal([86], store.Snapshot().Picks.Lists[Role.Top]);
    }

    [Fact]
    public void A_corrupt_file_falls_back_to_defaults_and_is_kept()
    {
        File.WriteAllText(SettingsPath, "{ not json");

        using var store = new SettingsStore(SettingsPath, NullLogger<SettingsStore>.Instance);

        Assert.True(store.Snapshot().AutoAcceptEnabled);
        Assert.True(File.Exists(SettingsPath + ".bad"));
    }
}
