using System.Net;
using LockIn.Core.Champions;
using LockIn.Core.Hosting;
using LockIn.Core.Lcu;
using LockIn.Core.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LockIn.Core.Tests.Champions;

public sealed class ChampionCatalogTests : IDisposable
{
    private const string ChampionJson = """
        {"type":"champion","version":"15.1.1","data":{
          "MonkeyKing":{"id":"MonkeyKing","key":"62","name":"Wukong"},
          "Ahri":{"id":"Ahri","key":"103","name":"Ahri"},
          "Thresh":{"id":"Thresh","key":"412","name":"Thresh"}}}
        """;

    private readonly string _cache = Directory.CreateTempSubdirectory("lockin-champs-").FullName;

    public void Dispose() => Directory.Delete(_cache, recursive: true);

    private static LcuConnection OfflineConnection() =>
        new(new LcuLocator(NullLogger<LcuLocator>.Instance), NullLogger<LcuConnection>.Instance);

    [Fact]
    public async Task Downloads_parses_and_caches_the_champion_list()
    {
        var handler = new StubHandler(url => url switch
        {
            _ when url.EndsWith("versions.json", StringComparison.Ordinal) => """["15.1.1","15.0.1"]""",
            _ when url.EndsWith("champion.json", StringComparison.Ordinal) => ChampionJson,
            _ => null,
        });
        using var catalog = new ChampionCatalog(OfflineConnection(), NullLogger<ChampionCatalog>.Instance, handler, _cache);

        await catalog.LoadAsync();

        Assert.Equal(["Ahri", "Thresh", "Wukong"], catalog.All.Select(c => c.Name));
        Assert.Equal("MonkeyKing", catalog.Find(62)!.Key);
        Assert.Equal("15.1.1", catalog.Version);

        // A second start with no network still has the list.
        using var offline = new ChampionCatalog(OfflineConnection(), NullLogger<ChampionCatalog>.Instance, new StubHandler(_ => null), _cache);
        await offline.LoadAsync();
        Assert.Equal(3, offline.All.Count);
    }

    [Fact]
    public async Task Downloads_icons_once()
    {
        var handler = new StubHandler(url => url switch
        {
            _ when url.EndsWith("versions.json", StringComparison.Ordinal) => """["15.1.1"]""",
            _ when url.EndsWith("champion.json", StringComparison.Ordinal) => ChampionJson,
            _ when url.EndsWith("/img/champion/Ahri.png", StringComparison.Ordinal) => "PNG",
            _ => null,
        });
        using var catalog = new ChampionCatalog(OfflineConnection(), NullLogger<ChampionCatalog>.Instance, handler, _cache);
        await catalog.LoadAsync();

        var path = await catalog.GetIconPathAsync(catalog.Find(103)!);
        await catalog.GetIconPathAsync(catalog.Find(103)!);

        Assert.True(File.Exists(path));
        Assert.Equal(1, handler.Requests.Count(u => u.EndsWith("Ahri.png", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Reads_playable_champions_from_the_client()
    {
        await using var host = await TestHost.StartAsync(services: s => s.AddSingleton(sp =>
            new ChampionCatalog(sp.GetRequiredService<LcuConnection>(), sp.GetRequiredService<ILogger<ChampionCatalog>>(), new StubHandler(_ => null), _cache)));
        host.Client.SetGet("/lol-champions/v1/inventories/1/champions-minimal", new object[]
        {
            new { id = 103, freeToPlay = false, ownership = new { owned = true } },
            new { id = 412, freeToPlay = true, ownership = new { owned = false } },
            new { id = 62, freeToPlay = false, ownership = new { owned = false, rental = new { rented = false } } },
        });
        host.Client.SetGet("/lol-game-data/assets/v1/champion-summary.json", new object[]
        {
            new { id = -1, name = "None", alias = "None" },
            new { id = 103, name = "Ahri", alias = "Ahri" },
        });
        var catalog = host.Host.Services.GetRequiredService<ChampionCatalog>();

        await catalog.RefreshPlayableAsync();

        Assert.Equal([103, 412], catalog.Playable!.Order());
        Assert.Equal("Ahri", catalog.NameOf(103)); // Fell back to the client's list (no network).
    }

    private sealed class StubHandler(Func<string, string?> respond) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            lock (Requests)
            {
                Requests.Add(url);
            }

            var body = respond(url);
            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}
