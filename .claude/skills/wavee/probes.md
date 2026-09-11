# Headless CLI probes

The 11 headless-probe flags (`CliRun.ProbeFlags` in `src/apps/Wavee/Diagnostics/CliRun.cs` is the source of
truth — this list mirrors it): `--backend-selftest`, `--qr-dump`, `--spotify-login`, `--spotify-metadata`,
`--spotify-video-manifest`, `--spotify-video-traits`, `--spotify-playlist`, `--spotify-rootlist`,
`--spotify-collection` (`[liked|albums|artists|shows|episodes|pins]`), `--spotify-sync`, `--connect-live`.

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
