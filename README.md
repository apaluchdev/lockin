# LockIn

A Windows app for League of Legends that handles the busywork around champ select:

- **Auto-accept**: accepts a found match after an optional delay.
- **Priority ban lists**: one per role plus a Default list. LockIn bans the first champion that isn't
  already banned, being banned by a teammate, wanted by a teammate, or your own pick.
- **Priority pick lists per role**: one each for Top, Jungle, Mid, Bot and Support, plus an Any role list
  for blind pick and as a fallback. LockIn skips banned, taken, unowned or teammate-wanted champions.
- **Lock-in timing**: LockIn hovers your pick and locks it in **X seconds before your timer ends**
  (configurable), even if you hovered something else yourself. It times this with its own clock, so it
  still locks in when the client's timer stops updating (e.g. when you're last pick).
- **Pick intent**: during planning, LockIn shows your top pick to your team.
- **Runes & summoner spells per champion**: switches to one of your saved rune pages and sets your
  spells when the champion locks in, keeping Flash on your preferred key.
- **Activity log**: shows every decision LockIn makes, with the reason ("Pick: Orianna (#2 on your Mid
  list) — skipped Ahri (already picked)"). Also written to daily log files.
- **Tray app**: closing or minimizing the window keeps LockIn running in the tray. It can also start
  with Windows.
- **Phone page**: follow champ select live on your phone over your home Wi-Fi. It shows a heartbeat,
  the client status (in queue, match found, ban phase, pick phase…), a countdown, both teams' picks and
  bans, and LockIn's planned ban and pick with reasons. You can **override** the ban or pick for the
  current game and **lock in now**. Turn it on in the **Phone** tab and scan the QR code.

LockIn talks only to the League client's local API (LCU) on `127.0.0.1`, which is the same API the client's
own interface uses. It never reads the screen, sends keystrokes, or touches the game itself. It
downloads the champion list and icons from Riot's public Data Dragon CDN.

## Install & use

1. Download the `.msi` from the [Releases](https://github.com/apaluchdev/lockin/releases) page and run it.
   It installs for your Windows user only, needs no administrator rights or .NET install, and adds LockIn
   to the Start menu. The installer isn't code-signed, so Windows SmartScreen may warn you: choose
   **More info → Run anyway**.
2. Open LockIn and set up your **Picks**, **Bans**, and **Runes & spells** tabs. Everything saves automatically.
3. Leave it running in the tray while you play. The status bar shows the connection and the current phase.

| What | Where |
|---|---|
| Settings | `%APPDATA%\LockIn\settings.json` |
| Logs (14 days) | `%LOCALAPPDATA%\LockIn\logs` |
| Champion list & icon cache | `%LOCALAPPDATA%\LockIn\cache` |

### Phone page

- Off until you enable it on the **Phone** tab. It then listens on port 47800 (changeable) on your
  local network only. Nothing goes over the internet.
- **Access is protected.** The QR code link carries a secret key after `#`, which browsers never send
  in requests. If you type the address by hand, the page asks for the 6-digit pairing code shown on the
  Phone tab. Wrong codes are rate-limited. **Sign out all phones** issues a new key and code.
- Windows asks once whether LockIn may use the network. Allow it on **private** networks. If you
  dismissed the prompt, allow LockIn under *Windows Security → Firewall & network protection → Allow an
  app through firewall*.
- Overrides apply to the current champ select only. Your saved lists don't change.

**Modes:** picks, bans, runes and spells are automated on Summoner's Rift (draft, ranked, blind,
Clash). In ARAM, Arena and other modes, LockIn only auto-accepts. Custom games are off by default (Settings).

## Build

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (pinned in `global.json`).

```bash
dotnet build
dotnet test
dotnet run --project src/LockIn.App
```

Single self-contained `artifacts\LockIn.exe` (runs the tests first):

```powershell
./tools/publish.ps1
```

Installer in `artifacts\installer` (runs the tests and publishes first):

```powershell
./tools/build-installer.ps1
```

### Try it without playing

`tools/LockIn.FakeClient` pretends to be the League client and plays a scripted ranked champ select:
queue pop, planning, bans, and a last-pick turn with a stale timer. See its
[README](tools/LockIn.FakeClient/README.md).

When developing the phone page, set `LOCKIN_REMOTE_LOOPBACK=1` so LockIn listens on `127.0.0.1` only
(no firewall prompt), then open `http://127.0.0.1:47800` in a desktop browser.

## Layout

| Path | What it is |
|---|---|
| `src/LockIn.App` | WPF app: .NET 10, Fluent theme (follows Windows light/dark), MVVM with CommunityToolkit.Mvvm, Generic Host for DI, tray icon via H.NotifyIcon |
| `src/LockIn.Remote` | The phone page: Kestrel server, key/pairing auth, WebSocket live feed, and the page itself (`wwwroot/`, plain HTML/CSS/JS embedded in the assembly) |
| `src/LockIn.Core` | UI-independent logic. `Lcu/` connection & event feed · `ChampSelect/` the decision engine (`ChampSelectPlanner`, pure and unit-tested) and the service that runs it · `Matchmaking/` auto-accept · `Loadouts/` runes & spells · `Champions/` champion catalog · `Settings/` |
| `tests/LockIn.Core.Tests` | xUnit v3. Planner unit tests, plus end-to-end tests running the real services against a fake League client |
| `tools/` | Fake client demo, publish script, icon generator |

Package versions are managed centrally in `Directory.Packages.props`.

## License

[MIT](LICENSE): free for anyone to use, change and share, for any purpose.

## Acknowledgements

Inspired by [LeagueAutoAccept](https://github.com/sweetriverfish/LeagueAutoAccept) (MIT). LockIn is a from-scratch rewrite.

## Disclaimer

LockIn isn't endorsed by Riot Games and doesn't reflect the views or opinions of Riot Games or anyone
officially involved in producing or managing Riot Games properties. Riot Games and all associated
properties are trademarks or registered trademarks of Riot Games, Inc. The LCU API is unofficial and can
change with any client patch. Automating champ select is a gray area under Riot's terms. Use at your own risk.
