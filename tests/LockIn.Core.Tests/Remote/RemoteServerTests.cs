using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using LockIn.Core.Champions;
using LockIn.Core.ChampSelect;
using LockIn.Core.Hosting;
using LockIn.Core.Lcu;
using LockIn.Core.Live;
using LockIn.Core.Loadouts;
using LockIn.Core.Settings;
using LockIn.Core.Tests.Fakes;
using LockIn.Remote;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LockIn.Core.Tests.Remote;

/// <summary>The phone server, end to end: real Kestrel on a random port, real services, fake League client.</summary>
public sealed class RemoteServerTests : IDisposable
{
    private const int Ahri = 103, Orianna = 61, Me = 2;
    private readonly string _cache = Directory.CreateTempSubdirectory("lockin-remote-").FullName;
    private readonly HttpClient _http = new();

    public RemoteServerTests() =>
        File.WriteAllText(Path.Combine(_cache, "champions.json"),
            """{"version":"1","champions":[{"id":103,"key":"Ahri","name":"Ahri"},{"id":61,"key":"Orianna","name":"Orianna"}]}""");

    public void Dispose()
    {
        _http.Dispose();
        Directory.Delete(_cache, recursive: true);
    }

    private async Task<(TestHost Host, RemoteServer Server)> StartAsync()
    {
        RemoteServer.LoopbackOnly = true; // No firewall prompts from tests.
        var host = await TestHost.StartAsync(
            s =>
            {
                s.Remote.Enabled = true;
                s.Remote.Port = 0;
                s.Picks.Lists[Role.Middle] = [Ahri, Orianna];
            },
            services =>
            {
                services.AddSingleton(sp => new ChampionCatalog(sp.GetRequiredService<LcuConnection>(),
                    sp.GetRequiredService<ILogger<ChampionCatalog>>(), new NoNetworkHandler(), _cache));
                services.AddHostedSingleton<ChampSelectService>();
                services.AddHostedSingleton<LoadoutService>();
                services.AddHostedSingleton<LiveStateService>();
                services.AddHostedSingleton<RemoteServer>();
            });
        await host.Host.Services.GetRequiredService<ChampionCatalog>().LoadAsync();
        var server = host.Host.Services.GetRequiredService<RemoteServer>();
        await host.WaitUntilAsync(() => server.IsRunning, "start the phone server");
        return (host, server);
    }

    private static Uri Http(RemoteServer server, string path) => new($"http://127.0.0.1:{server.Port}{path}");

    private static async Task<ClientWebSocket> ConnectAsync(RemoteServer server, string? key, string? origin = null)
    {
        var socket = new ClientWebSocket();
        if (origin is not null)
        {
            socket.Options.SetRequestHeader("Origin", origin);
        }

        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/ws?k={Uri.EscapeDataString(key ?? "")}"), CancellationToken.None);
        return socket;
    }

    private static async Task<JsonElement> ReceiveAsync(ClientWebSocket socket, string type, int timeoutMs = 3000)
    {
        using var timeout = new CancellationTokenSource(timeoutMs);
        var buffer = new byte[256 * 1024];
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, timeout.Token);
            var json = JsonDocument.Parse(Encoding.UTF8.GetString(buffer, 0, result.Count)).RootElement.Clone();
            if (json.GetProperty("type").GetString() == type)
            {
                return json;
            }
        }
    }

    private static Task SendAsync(ClientWebSocket socket, object message) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message)), WebSocketMessageType.Text, true, CancellationToken.None);

    [Fact]
    public async Task Serves_the_page_to_anyone_but_the_api_only_with_the_key()
    {
        var (host, server) = await StartAsync();
        await using var _ = host;

        var page = await _http.GetAsync(Http(server, "/"));
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("LockIn", await page.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Contains("default-src 'self'", page.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.GetAsync(Http(server, "/api/champions"))).StatusCode);

        using var request = new HttpRequestMessage(HttpMethod.Get, Http(server, "/api/champions"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", server.Key);
        var champions = await (await _http.SendAsync(request)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, champions.GetProperty("champions").GetArrayLength());
        Assert.Equal(Ahri, champions.GetProperty("picks").GetProperty("Middle")[0].GetInt32());
    }

    [Fact]
    public async Task Pairing_code_trades_for_the_key_and_wrong_codes_are_rate_limited()
    {
        var (host, server) = await StartAsync();
        await using var _ = host;

        var ok = await _http.PostAsJsonAsync(Http(server, "/api/pair"), new { code = server.PairingCode[..3] + " " + server.PairingCode[3..] });
        Assert.Equal(server.Key, (await ok.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("key").GetString());

        var wrong = server.PairingCode == "000000" ? "111111" : "000000";
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 6; i++)
        {
            statuses.Add((await _http.PostAsJsonAsync(Http(server, "/api/pair"), new { code = wrong })).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, statuses[0]);
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[^1]);

        // Locked out: even the right code is refused for a while.
        Assert.Equal(HttpStatusCode.TooManyRequests, (await _http.PostAsJsonAsync(Http(server, "/api/pair"), new { code = server.PairingCode })).StatusCode);
    }

    [Fact]
    public async Task Socket_rejects_a_wrong_key_and_other_origins()
    {
        var (host, server) = await StartAsync();
        await using var _ = host;

        await Assert.ThrowsAnyAsync<WebSocketException>(() => ConnectAsync(server, "wrong"));
        await Assert.ThrowsAnyAsync<WebSocketException>(() => ConnectAsync(server, server.Key, origin: "http://evil.example"));

        using var sameOrigin = await ConnectAsync(server, server.Key, origin: $"http://127.0.0.1:{server.Port}");
        Assert.Equal(WebSocketState.Open, sameOrigin.State);
    }

    [Fact]
    public async Task Pushes_state_and_heartbeats()
    {
        var (host, server) = await StartAsync();
        await using var _ = host;
        using var socket = await ConnectAsync(server, server.Key);

        var state = (await ReceiveAsync(socket, "state")).GetProperty("state");
        Assert.Equal("connected", state.GetProperty("connection").GetString());
        Assert.Equal("Tester#NA1", state.GetProperty("summoner").GetString());

        var heartbeat = await ReceiveAsync(socket, "heartbeat", timeoutMs: 2500);
        Assert.True(heartbeat.GetProperty("serverTime").GetInt64() > 0);
        await host.WaitUntilAsync(() => server.ConnectedPhones == 1, "count the phone");
    }

    [Fact]
    public async Task Shows_champ_select_live_and_applies_a_pick_override()
    {
        var (host, server) = await StartAsync();
        await using var _ = host;
        host.Client.SetGet(LcuEndpoints.GameflowSession, new { gameData = new { queue = new { id = 400, gameMode = "CLASSIC", description = "Normal Draft" } } });
        await host.Client.PushAsync(LcuEndpoints.GameflowPhase, "ChampSelect");
        await host.Client.PushAsync(LcuEndpoints.ChampSelectSession, new
        {
            gameId = 7,
            localPlayerCellId = Me,
            myTeam = new[] { new { cellId = Me, assignedPosition = "middle", championId = 0 } },
            theirTeam = new[] { new { cellId = 7, championId = 0 } },
            actions = new[] { new[] { new { id = 10, actorCellId = Me, type = "pick", isAllyAction = true, isInProgress = false, completed = false, championId = 0 } } },
            timer = new { phase = "PLANNING", adjustedTimeLeftInPhase = 25_000, totalTimeInPhase = 30_000, internalNowInEpochMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() },
        });
        using var socket = await ConnectAsync(server, server.Key);

        JsonElement cs;
        do
        {
            cs = (await ReceiveAsync(socket, "state")).GetProperty("state").GetProperty("champSelect");
        }
        while (cs.ValueKind == JsonValueKind.Null || cs.GetProperty("plannedPick").ValueKind == JsonValueKind.Null);

        Assert.Equal("Planning", cs.GetProperty("stageLabel").GetString());
        Assert.Equal("Mid", cs.GetProperty("role").GetString());
        Assert.Equal(Ahri, cs.GetProperty("plannedPick").GetProperty("championId").GetInt32());

        await SendAsync(socket, new { type = "override", kind = "pick", championId = Orianna, id = 1 });
        var result = await ReceiveAsync(socket, "result");
        Assert.True(result.GetProperty("ok").GetBoolean(), result.ToString());

        do
        {
            cs = (await ReceiveAsync(socket, "state")).GetProperty("state").GetProperty("champSelect");
        }
        while (cs.GetProperty("plannedPick").GetProperty("championId").GetInt32() != Orianna);

        Assert.Equal("Override", cs.GetProperty("plannedPick").GetProperty("source").GetString());
        Assert.Equal(Orianna, cs.GetProperty("pickOverride").GetInt32());
    }

    [Fact]
    public async Task Applies_rune_and_spell_overrides_and_shows_them_live()
    {
        var (host, server) = await StartAsync();
        await using var _ = host;
        host.Client.SetGet("/lol-perks/v1/pages", new object[]
        {
            new { id = 1, name = "Default", isActive = true, isEditable = true },
            new { id = 42, name = "Burst", isActive = false, isEditable = true },
        });
        host.Client.SetGet(LcuEndpoints.GameflowSession, new { gameData = new { queue = new { id = 400, gameMode = "CLASSIC" } } });
        host.Client.SetGet(LcuEndpoints.GameflowPhase, "ChampSelect");
        await host.Client.PushAsync(LcuEndpoints.GameflowPhase, "ChampSelect");
        await host.Client.PushAsync(LcuEndpoints.ChampSelectSession, new
        {
            gameId = 7,
            localPlayerCellId = Me,
            myTeam = new[] { new { cellId = Me, assignedPosition = "middle", championId = 0 } },
            actions = new[] { new[] { new { id = 10, actorCellId = Me, type = "pick", isAllyAction = true, isInProgress = false, completed = false, championId = 0 } } },
            timer = new { phase = "PLANNING", adjustedTimeLeftInPhase = 25_000, totalTimeInPhase = 30_000, internalNowInEpochMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() },
        });
        var champSelect = host.Host.Services.GetRequiredService<ChampSelectService>();
        await host.WaitUntilAsync(() => champSelect.View is not null, "see champ select");
        using var socket = await ConnectAsync(server, server.Key);

        using var request = new HttpRequestMessage(HttpMethod.Get, Http(server, "/api/loadout-options"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", server.Key);
        var options = await (await _http.SendAsync(request)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Burst", options.GetProperty("pages")[1].GetProperty("name").GetString());
        Assert.Equal(SummonerSpells.All.Count, options.GetProperty("spells").GetArrayLength());

        await SendAsync(socket, new { type = "runeOverride", pageId = 42, id = 1 });
        Assert.True((await ReceiveAsync(socket, "result")).GetProperty("ok").GetBoolean());
        await SendAsync(socket, new { type = "spellOverride", spell1Id = 14, spell2Id = 4, id = 2 });
        Assert.True((await ReceiveAsync(socket, "result")).GetProperty("ok").GetBoolean());

        Assert.Equal("42", Assert.Single(host.Client.RequestsTo("PUT", "/lol-perks/v1/currentpage")).Body);
        var spells = Assert.Single(host.Client.RequestsTo("PATCH", "/lol-champ-select/v1/session/my-selection")).Json;
        Assert.Equal((14, 4), (spells.GetProperty("spell1Id").GetInt32(), spells.GetProperty("spell2Id").GetInt32()));

        JsonElement loadout;
        do
        {
            loadout = (await ReceiveAsync(socket, "state")).GetProperty("state").GetProperty("loadout");
        }
        while (loadout.ValueKind == JsonValueKind.Null || !loadout.GetProperty("spellOverride").GetBoolean());

        Assert.Equal("Burst", loadout.GetProperty("runePage").GetString());
        Assert.True(loadout.GetProperty("runeOverride").GetBoolean());
        Assert.Equal(("Ignite", "Flash"), (loadout.GetProperty("spell1").GetString(), loadout.GetProperty("spell2").GetString()));
    }

    [Fact]
    public async Task Page_and_live_feed_carry_the_same_build_id()
    {
        var (host, server) = await StartAsync();
        await using var _ = host;

        var page = await _http.GetStringAsync(Http(server, "/"));
        using var socket = await ConnectAsync(server, server.Key);
        var state = await ReceiveAsync(socket, "state");

        Assert.Equal(16, RemoteServer.BuildId.Length);
        Assert.Contains($"name=\"lockin-build\" content=\"{RemoteServer.BuildId}\"", page, StringComparison.Ordinal);
        Assert.DoesNotContain("__LOCKIN_BUILD__", page, StringComparison.Ordinal);
        Assert.Equal(RemoteServer.BuildId, state.GetProperty("build").GetString());
    }

    [Fact]
    public async Task Override_outside_champ_select_is_refused_with_a_reason()
    {
        var (host, server) = await StartAsync();
        await using var _ = host;
        using var socket = await ConnectAsync(server, server.Key);

        await SendAsync(socket, new { type = "override", kind = "pick", championId = Ahri, id = 1 });

        var result = await ReceiveAsync(socket, "result");
        Assert.False(result.GetProperty("ok").GetBoolean());
        Assert.Equal("You're not in champ select", result.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Regenerating_the_key_signs_phones_out()
    {
        var (host, server) = await StartAsync();
        await using var _ = host;
        var oldKey = server.Key;

        server.RegenerateKey();
        await host.WaitUntilAsync(() => server.Key != oldKey, "apply the new key");

        await Assert.ThrowsAnyAsync<WebSocketException>(() => ConnectAsync(server, oldKey));
        using var socket = await ConnectAsync(server, server.Key);
        Assert.Equal(WebSocketState.Open, socket.State);
    }

    [Fact]
    public async Task Stops_when_disabled()
    {
        var (host, server) = await StartAsync();
        await using var _ = host;

        host.Settings.Update(s => s.Remote.Enabled = false);

        await host.WaitUntilAsync(() => !server.IsRunning, "stop the phone server");
    }
}
