using LockIn.Core.Champions;
using LockIn.Core.Lcu;
using LockIn.Core.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LockIn.Core.Tests.Champions;

public sealed class EmptyInventoryTests : IDisposable
{
    private readonly string _cache = Directory.CreateTempSubdirectory("lockin-inv-").FullName;

    public void Dispose() => Directory.Delete(_cache, recursive: true);

    /// <summary>Found by the fake-client demo: an empty inventory made every champion look unowned, so nothing was picked.</summary>
    [Fact]
    public async Task An_empty_inventory_means_unknown_not_owns_nothing()
    {
        await using var host = await TestHost.StartAsync(services: s => s.AddSingleton(sp =>
            new ChampionCatalog(sp.GetRequiredService<LcuConnection>(), sp.GetRequiredService<ILogger<ChampionCatalog>>(), new NoNetworkHandler(), _cache)));
        host.Client.SetGet("/lol-champions/v1/inventories/1/champions-minimal", Array.Empty<object>());
        var catalog = host.Host.Services.GetRequiredService<ChampionCatalog>();

        await catalog.RefreshPlayableAsync();

        Assert.Null(catalog.Playable);
    }
}
