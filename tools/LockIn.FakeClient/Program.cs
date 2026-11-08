using System.Globalization;

using LockIn.Core.ChampSelect;
using LockIn.Core.Lcu;
using LockIn.Core.Tests.Fakes;

// Plays out a ranked champ select against LockIn:
//   queue pop → planning → simultaneous bans → picks → your last-pick turn with a timer that is
//   never refreshed (the case where the client's timer goes stale) → game start.
// Point LockIn at the folder printed below (Settings → League install folder).

var installDir = args.FirstOrDefault() ?? Path.Combine(Path.GetTempPath(), "LockIn-FakeClient");
var scale = double.Parse(Environment.GetEnvironmentVariable("FAKE_SPEED") ?? "1", CultureInfo.InvariantCulture);
await using var client = await FakeLeagueClient.StartAsync(installDir);

Console.WriteLine($"Fake League client running. Lockfile: {Path.Combine(installDir, "lockfile")}");
Console.WriteLine($"In LockIn, set Settings → League install folder to: {installDir}");
Console.WriteLine("Waiting for LockIn to connect…");

const int Me = 2;
int[] enemyBans = [157, 238, 777, 84, 91];     // Yasuo, Zed, Yone, Akali, Talon
int[] allyPicks = [266, 64, 0, 22, 412];        // Aatrox, Lee Sin, (me), Ashe, Thresh
string[] positions = ["top", "jungle", "middle", "bottom", "utility"];

client.SetGet(LcuEndpoints.GameflowPhase, "Lobby");
client.SetGet(LcuEndpoints.GameflowSession, new { gameData = new { isCustomGame = false, queue = new { id = 420, gameMode = "CLASSIC", description = "Ranked Solo/Duo" } } });
client.SetGet("/lol-perks/v1/pages", new object[]
{
    new { id = 1001, name = "Mage (Electrocute)", isActive = true, isEditable = true },
    new { id = 1002, name = "Control (Arcane Comet)", isActive = false, isEditable = true },
    new { id = 1003, name = "Tank support (Aftershock)", isActive = false, isEditable = true },
});
client.SetGet("/lol-champions/v1/inventories/1/champions-minimal", Array.Empty<object>());

await WaitUntil(() => client.Subscriptions.Count >= 3, TimeSpan.FromMinutes(10));
Console.WriteLine("LockIn connected.");
await Pause(1);

// ── Queue and ready check ──────────────────────────────────────────────
await Phase("Matchmaking");
await Pause(3);
await Phase("ReadyCheck");
var readyCheck = new { state = "InProgress", playerResponse = "None", timer = 0 };
client.SetGet(LcuEndpoints.ReadyCheck, readyCheck);
await client.PushAsync(LcuEndpoints.ReadyCheck, readyCheck);
Console.WriteLine("Match found — waiting up to 12 s for LockIn to accept…");
var accepted = await WaitUntil(() => client.RequestsTo("POST", "/lol-matchmaking/v1/ready-check/accept").Any(), TimeSpan.FromSeconds(12));
Console.WriteLine(accepted ? "LockIn accepted." : "Not accepted (is auto-accept on?). Continuing anyway.");
await client.PushAsync(LcuEndpoints.ReadyCheck, new { state = "EveryoneReady", playerResponse = "Accepted", timer = 3 });
await Pause(2);
await client.PushAsync(LcuEndpoints.ReadyCheck, null, "Delete");

// ── Champ select ───────────────────────────────────────────────────────
// One simultaneous ban turn for all ten players (ids 1–10; ours is 1 + Me).
List<List<ChampSelectAction>> actions =
[
    [.. Enumerable.Range(0, 10).Select(cell => new ChampSelectAction { Id = cell + 1, ActorCellId = cell, Type = "ban", IsAllyAction = cell < 5 })],
];
var pickOrder = new[] { 0, 5, 6, 1, 3, 7, 8, 4, 9, Me }; // We're last pick.
var pickId = 20;
foreach (var cell in pickOrder)
{
    actions.Add([new ChampSelectAction { Id = pickId++, ActorCellId = cell, Type = "pick", IsAllyAction = (cell < 5) }]);
}

var session = new ChampSelectSession
{
    GameId = 4242,
    LocalPlayerCellId = Me,
    MyTeam = [.. Enumerable.Range(0, 5).Select(cell => new ChampSelectPlayer
    {
        CellId = cell,
        AssignedPosition = positions[cell],
        ChampionPickIntent = cell == Me ? 0 : allyPicks[cell],
    })],
    TheirTeam = [.. Enumerable.Range(5, 5).Select(cell => new ChampSelectPlayer { CellId = cell })],
    Actions = actions,
};

client.OnRequest = async request =>
{
    if (request.Method == "PATCH" && request.Path.StartsWith("/lol-champ-select/v1/session/actions/", StringComparison.Ordinal))
    {
        var id = int.Parse(request.Path[(request.Path.LastIndexOf('/') + 1)..], CultureInfo.InvariantCulture);
        var body = request.Json;
        var champion = body.TryGetProperty("championId", out var c) ? c.GetInt32() : 0;
        var completed = body.TryGetProperty("completed", out var d) && d.GetBoolean();
        Update(id, a => a with { ChampionId = champion, Completed = a.Completed || completed, IsInProgress = a.IsInProgress && !completed });
        if (session.Timer.Phase == ChampSelectTimer.Planning)
        {
            session = session with { MyTeam = [.. session.MyTeam.Select(p => p.CellId == Me ? p with { ChampionPickIntent = champion } : p)] };
        }

        Console.WriteLine($"  LockIn → {(completed ? "LOCK" : "hover")} action {id}: champion {champion}");
        await Push(refreshTimer: false);
    }
    else
    {
        Console.WriteLine($"  LockIn → {request.Method} {request.Path} {request.Body}");
    }
};

await Phase("ChampSelect");
client.SetGet("/lol-champ-select/v1/session", session);

// Planning
Console.WriteLine("Planning phase (8 s)…");
await Turn(ChampSelectTimer.Planning, 8);

// Bans: everyone at once. Teammates hover after a moment; enemies' bans are revealed at the end.
Console.WriteLine("Ban phase (15 s)…");
SetInProgress(actions[0].Select(a => a.Id).ToArray());
await Turn(ChampSelectTimer.BanPick, 15, async () =>
{
    await Pause(2);
    Update(1, a => a with { ChampionId = 157 }); // A teammate hovers Yasuo — LockIn should avoid it.
    await Push(refreshTimer: false);
});
foreach (var ban in actions[0].Where(a => !a.Completed))
{
    Update(ban.Id, a => a with
    {
        Completed = true,
        IsInProgress = false,
        ChampionId = a.ActorCellId >= 5 ? enemyBans[a.ActorCellId - 5] : a.ChampionId,
    });
}

// Picks before ours: 4 s each. Enemies pick from a fixed list; teammates lock their intent.
int[] enemyPicks = [86, 103, 99, 202, 875];
var enemyIndex = 0;
foreach (var turn in actions.Skip(1))
{
    var action = turn[0];
    if (action.ActorCellId == Me)
    {
        break;
    }

    SetInProgress(action.Id);
    await Turn(ChampSelectTimer.BanPick, 4);
    var champion = action.ActorCellId < 5 ? allyPicks[action.ActorCellId] : enemyPicks[enemyIndex++];
    Update(action.Id, a => a with { ChampionId = champion, Completed = true, IsInProgress = false });
    session = session with
    {
        MyTeam = [.. session.MyTeam.Select(p => p.CellId == action.ActorCellId ? p with { ChampionId = champion } : p)],
        TheirTeam = [.. session.TheirTeam.Select(p => p.CellId == action.ActorCellId ? p with { ChampionId = champion } : p)],
    };
}

// Our pick: one snapshot, then silence. LockIn must lock in from its own clock.
Console.WriteLine("Your pick (30 s). The timer snapshot is sent once and never refreshed…");
var mine = actions.Skip(1).First(t => t[0].ActorCellId == Me)[0];
SetInProgress(mine.Id);
var started = DateTime.UtcNow;
await Push(refreshTimer: true, phase: ChampSelectTimer.BanPick, totalSeconds: 30);
client.SetGet("/lol-champ-select/v1/session", session);
await WaitUntil(() => Find(mine.Id).Completed, TimeSpan.FromSeconds(31 / scale));
var lockedAfter = DateTime.UtcNow - started;
Console.WriteLine(Find(mine.Id).Completed
    ? $"Locked in after {lockedAfter.TotalSeconds:0.0} s ({30 / scale - lockedAfter.TotalSeconds:0.0} s before the end)."
    : "Timer ran out without a lock-in — that would have been a dodge.");

Console.WriteLine("Finalization (6 s)…");
await Turn(ChampSelectTimer.Finalization, 6);
await client.PushAsync(LcuEndpoints.ChampSelectSession, null, "Delete");
await Phase("InProgress");
Console.WriteLine("Game started. Demo finished — press Enter to stop the fake client.");
Console.ReadLine();

// ── Helpers ────────────────────────────────────────────────────────────

async Task Phase(string phase)
{
    client.SetGet(LcuEndpoints.GameflowPhase, phase);
    await client.PushAsync(LcuEndpoints.GameflowPhase, phase);
}

async Task Turn(string phase, double seconds, Func<Task>? during = null)
{
    await Push(refreshTimer: true, phase, seconds);
    var turn = Task.Delay(TimeSpan.FromSeconds(seconds / scale));
    if (during is not null)
    {
        await during();
    }

    await turn;
}

async Task Push(bool refreshTimer, string? phase = null, double totalSeconds = 0)
{
    if (refreshTimer)
    {
        session = session with
        {
            Timer = new ChampSelectTimer
            {
                Phase = phase ?? session.Timer.Phase,
                TotalTimeInPhase = (long)(totalSeconds * 1000 / scale),
                AdjustedTimeLeftInPhase = (long)(totalSeconds * 1000 / scale),
                InternalNowInEpochMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            },
        };
    }

    client.SetGet("/lol-champ-select/v1/session", session);
    await client.PushAsync(LcuEndpoints.ChampSelectSession, session);
}

void SetInProgress(params int[] ids) => session = session with
{
    Actions = [.. session.Actions.Select(turn => turn.Select(a => a with { IsInProgress = ids.Contains(a.Id) && !a.Completed }).ToList())],
};

void Update(int id, Func<ChampSelectAction, ChampSelectAction> change) => session = session with
{
    Actions = [.. session.Actions.Select(turn => turn.Select(a => a.Id == id ? change(a) : a).ToList())],
};

ChampSelectAction Find(int id) => session.AllActions.First(a => a.Id == id);

Task Pause(double seconds) => Task.Delay(TimeSpan.FromSeconds(seconds / scale));

static async Task<bool> WaitUntil(Func<bool> condition, TimeSpan timeout)
{
    var deadline = DateTime.UtcNow + timeout;
    while (!condition())
    {
        if (DateTime.UtcNow > deadline)
        {
            return false;
        }

        await Task.Delay(100);
    }

    return true;
}



