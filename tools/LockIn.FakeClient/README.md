# LockIn.FakeClient

A pretend League client for trying LockIn without queueing. It serves the client's local API on
`127.0.0.1` and plays out a scripted ranked game:

1. Queue → match found (LockIn should auto-accept)
2. Planning (LockIn shows your top pick as intent)
3. Simultaneous bans (a teammate hovers Yasuo; LockIn should avoid it)
4. Nine picks before yours. Enemies pick Ahri, Lux and others.
5. **Your last-pick turn, with a timer snapshot that's never refreshed.** This is the case where the
   real client's timer goes stale. LockIn must lock in from its own clock.
6. Finalization → game start

## Run it

```bash
dotnet run --project tools/LockIn.FakeClient
```

Then in LockIn, set **Settings → League install folder** to the folder it prints
(`%TEMP%\LockIn-FakeClient` by default) and give yourself a Mid pick list. Set `FAKE_SPEED=2` to run
the timers twice as fast. Clear the install folder setting again when you're done.

The fake also backs the end-to-end tests in `tests/LockIn.Core.Tests` (same source file).
