# Headless CLI probes

The windowless CLI probe flags (`Diagnostics.Probe.TryRunCliArm` in `src/apps/Wavee/Screens/Diagnostics.Probe.Arms.cs`
is the source of truth — this list mirrors it): `--qr-dump [text] [out.png]`, `--relaunch-after <pid>` (the restart
broker — a courier, not a diagnostic) and `--log-sessions`. `--headless` (the scripted no-window host,
`Diagnostics.Probe.cs`) and the window-bound arms (`--perf-bench`, `--startup-bench`, `--crash-probe`,
`--lyrics-advance-probe`) are separate doors. The 0.2.9 `CliRun.ProbeFlags` set (`--backend-selftest`, `--spotify-*`,
`--connect-live`) is NOT in the 0.3 tree; drive Spotify through a `--headless` script instead.

**`--log-sessions`** (`src/apps/Wavee/Diagnostics/LogSessions.Probe.cs`) needs no login and opens no window. It runs the
real `WaveeLogSessions.ListPastSessions(Log.BasePath, pid)` on the real log folder and prints the base path, files seen,
lines seen, files skipped, sessions, elapsed ms, the newest 10 sessions (start, pid, event count, sid) and the exception
if the walk faulted (exit code 1) — also as one `probe` / `log.sessions` line in the log. It is how an empty session
picker is told apart from "nothing on disk" and "the walk failed"; launch the packaged exe with it to read a packaged
LocalCache (the startup line's `logResolved=` names the folder), or pass `--profile <dir>` for a scratch profile.

## Build and run

Build to a scratch folder, never the live `bin/` a running Debug instance may lock:

```powershell
dotnet build src/apps/Wavee/Wavee.csproj -c Release -o C:\scratch\wavee-probe
```

`Wavee.exe` is a WinExe, so a bare PowerShell launch never waits and a Claude Code `!` prompt has no console to
print into. Use `Start-Process -Wait -NoNewWindow` instead:

```powershell
Start-Process C:\scratch\wavee-probe\Wavee.exe -ArgumentList '--spotify-collection','pins' -Wait -NoNewWindow
```

Output goes to the terminal (echoed, Debug and Release alike) **and** to
`%LOCALAPPDATA%\Wavee\logs\wavee-yyyyMMdd.log`.

## Credential caveat

Probes share the running app's DPAPI credential store — there is only one signed-in credential on the machine.
A rejected stored login no longer wipes it (probes pass `clearStoredOnReject: false`), so a probe run is safe
to leave the login intact even on failure. Never run a probe to test logout — use `ClearStoredCredential`
in-app for that. Never launch the scratch build as the normal GUI (no CLI flag) against the live profile: an
unpackaged run may migrate `library.db`.
