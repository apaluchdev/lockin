# LockIn

A Windows desktop app that helps out in League of Legends champ select: it accepts a found match and handles your picks and bans. It talks to the League client's local API (LCU), the same one the client's own interface uses. It doesn't read the screen or send any input.

> Status: early scaffold. Features are still to come.

## Requirements

- Windows 10/11
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (pinned in `global.json`)

## Build & run

```bash
dotnet build
dotnet run --project src/LockIn.App
dotnet test
```

## Layout

| Path | What it is |
|---|---|
| `src/LockIn.App` | WPF app (.NET 10, Fluent theme, MVVM via CommunityToolkit.Mvvm, Generic Host for DI) |
| `src/LockIn.Core` | UI-independent logic: the LCU connection, champ select rules, settings |
| `tests/LockIn.Core.Tests` | xUnit v3 tests for `LockIn.Core` |

Package versions are managed centrally in `Directory.Packages.props`.

## Acknowledgements

Inspired by [LeagueAutoAccept](https://github.com/sweetriverfish/LeagueAutoAccept) (MIT). LockIn is a from-scratch rewrite.

## Disclaimer

LockIn isn't endorsed by Riot Games and doesn't reflect the views or opinions of Riot Games or anyone officially involved in producing or managing Riot Games properties. Riot Games and all associated properties are trademarks or registered trademarks of Riot Games, Inc. The LCU API is unofficial and can change with any client patch. Use at your own risk.
