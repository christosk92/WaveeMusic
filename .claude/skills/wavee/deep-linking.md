# Wavee deep linking

Every activation surface (jump list, toast click, thumbnail-toolbar deep-link, future App Actions / widgets) **must**
emit a `wavee://` verb and land in `DeepLinkChannel`. Do not invent a second intake. Do not register `spotify:` here
(that is a later opt-in setting).

Source: `src/apps/Wavee/App/DeepLink.cs`. Boot wiring: `src/apps/Wavee/Program.cs`.

## Verb map

| URI | Kind | Fields |
|---|---|---|
| `wavee://open?route=<name>&arg=<value>` | `DeepLinkKind.Open` | `Route`, optional `Arg` |
| `wavee://play?ctx=<spotify-context-uri>` | `DeepLinkKind.Play` | `Context` |
| `wavee://play?link=<http(s)-url>` | `DeepLinkKind.Play` | `Link` — the playback-module intake (YouTube / Twitch / radio), same router as Play ▸ Link… |
| `wavee://resume` | `DeepLinkKind.Resume` | (none) |
| `wavee://pause` | `DeepLinkKind.Pause` | (none) |
| `spotify:` album / playlist / artist / show | `DeepLinkKind.Open` | translated to the shell's route names (`album` / `pl` / `artist` / `show`) |
| `spotify:track:<id>` | `DeepLinkKind.Play` | `Context` = the track uri |

Unknown verbs, missing required args (`open` without `route`, `play` without `ctx`), and garbage are **ignored** — the
parser never throws. Percent-encoding is decoded. A raw command line that *contains* a `wavee://` token is accepted.

`route` / `arg` compose the shell's opaque nav keys:

- pages: `home` `search` `library` `recents` `settings` — `arg` unused
- entities: `album` `pl` `artist` `show` `prerelease` — `arg` is the Spotify URI; the consumer builds `{route}:{arg}`
  (e.g. `album:spotify:album:…`). `route` may also already be the full key (`album:spotify:album:…`) with no `arg`.

## Boot order (normal windowed path only)

Probes / `--screenshot` / `--frames` skip this entire block.

1. **Gate** — `new SingleInstanceGate(); TryAcquire("Wavee", "FluentGpuWindow", payload)`. Secondary forwards via
   `WM_COPYDATA` and exits 0. Keep the gate alive for process lifetime.
2. **Register** — `ProtocolRegistrar.RegisterProtocol("wavee", exe, "Wavee", iconPath: WaveeAppIcon.Path())`.
   try/catch: registration failure must never block launch. HKCU; no-ops when packaged. The OPT-IN `spotify:` handler
   follows immediately via `DeepLink.SyncSpotifySchemeRegistration(settings.Get(WaveeSettings.HandleSpotifyLinks))`,
   which registers *or unregisters* so turning the setting off actually hands the scheme back.
3. **Classify** — `ActivationArgs.FromCurrentProcess("wavee")`. `Protocol` / `File` / `ToastActivated` →
   `DeepLinkChannel.Post(argument)`.
4. **Subscribe** — `FluentApp.ActivationRedirected += raw => { DeepLinkChannel.Post(raw); DeepLink.WakeWindow(); }`
   (static event; subscribe **before** `FluentAppHarness.Run`).
5. **Run**.

## Drain (shell consumer)

`Pending` is a monotonic `Signal<int>` (same shape as `OpenVideoOverrides` / `_searchFocusRequest`). Read `.Value` so
the effect re-runs; drain on the **first** tick too (cold-start verbs are already queued before the shell mounts).

```csharp
UseEffect(() =>
{
    _ = DeepLinkChannel.Pending.Value;
    while (DeepLinkChannel.TryDequeue(out DeepLinkVerb verb))
        Apply(verb);   // Open → nav key; Play → ctx; Resume → last session
});
```

Members: `DeepLinkChannel.Post(string?)`, `DeepLinkChannel.TryDequeue(out DeepLinkVerb)`, `DeepLinkChannel.Pending`,
`DeepLink.TryParse`, `DeepLink.WakeWindow()`, `DeepLinkVerb(Kind, Route, Arg, Context)`, `DeepLinkKind`.
