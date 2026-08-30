# Wavee Login / Logout — Technical Implementation (fluent-gpu)

**How this fits the current codebase.** Three layers exist today but are *unconnected for auth*: (1) a **real device-code login** (`SpotifyLiveLogin.LoginAsync`, `SpotifyLiveLogin.cs:47`) that builds its own `AuthFlow([stored, device])` (`:60`) and a real `AuthState` stream carrying the QR/code/phase; (2) a **coarse UI session seam** (`ISpotifySession` → `PlaybackBridge.Auth/User`, `PlaybackBridge.cs:52-53`) that only sees `AuthStatus {LoggedOut, Authenticating, Authenticated, Error}`; and (3) the **app gate** (`WaveeApp.Render`, `WaveeApp.cs:19`) that always mounts `WaveeShell`. They never meet: the rich `AuthState` is consumed **only by a console `ChallengeLogger`** (`SpotifyLiveLogin.cs:61,137`), and the real backend never even constructs the class that re-exposes it — `Services.CreateReal` (`Services.cs:120`) wires `new FakeSpotifySession()` behind `SwitchableSession`, and `FakeSpotifySession.ConnectAsync` (`FakeSpotifySession.cs:17`) flips to a fake **Premium** user in 250 ms. So the app is *always logged in*, there is no UI for the code/QR, and `ProfileChip` (`ShellToolbar.cs:129`) has an empty click handler (`:141`), no logout, no error/expiry/premium states.

This plan threads the existing `AuthState` **out of the SpotifyLive login pipeline** into a new **Core-level projection** + one `PlaybackBridge` signal, gates the shell on the coarse `AuthStatus`, and adds logout/teardown over the **already-present switchable facades** (`SwitchableSession.SetInner`, `Switchable.cs:129`; `Services.GoLive`, `Services.cs:171`). The protocol is **unchanged**. What is **reused**: `AuthFlow`/`AuthState`/`AuthChallenge`/`DeviceCodeProvider` (`Backend/Spotify/Auth.cs`), the `LoginResult.CredStore` handle (`SpotifyLiveLogin.cs:22,54`), the `SwitchableSession`/`SwitchablePlayer`/`SwitchableDevices` swap mechanism, `GoLive` (`Services.cs:171`), `Overlay.Service` + `ContentDialog` (already mounted by `WaveeShell.cs:326`), and the declarative animation surface. What is **added**: the Core `LoginSnapshot` projection + sink, an observer thread through `LoginAsync→ConnectAsync→StartAsync`, a single-flight bootstrap, `Services.GoOffline/AttachLive/LogoutAsync`, the `LoginView` takeover, the account `MenuFlyout`, an in-bootstrap premium gate, and (next iteration) a PKCE loopback provider.

**Verification corrections folded in** (from an adversarial review of the design — these are wiring/race bugs, not architecture):
1. **Silent resume must resolve to Welcome, not Error.** With only `[stored]`, `AuthFlow.AcquireAsync` emits `Failed` (`Auth.cs:79`) and `LoginAsync` returns null **identically** for "no cred on disk" and "cred present but AP handshake failed" (`SpotifyLiveLogin.cs:64`). The common first run would land on the Error card. Distinguish via `credStore.Load()!=null` and a post-attempt re-check (§4).
2. **Single-flight the bootstrap.** "Continue", "Get a new code", "Try again" each call `StartAsync` while a prior `Task.Run` (`WaveeApp.cs:52`) may still be unwinding → **double `GoLive`** → duplicate dealer sockets. Also the silent-`[stored]` vs interactive-`[device]` race (§4).
3. **Attach teardown handles BEFORE `GoLive`.** `StartAsync` calls `GoLive` at `LiveSessionHost.cs:41` (→ `Authenticated` → shell → logout reachable) but returns the host only at `:85`. Logout in that window makes `credStore.Clear()`/`DisposeAsync()` no-ops → creds persist + transport leaks (§5).
4. **`Finalizing` has no event source.** Nobody calls `flow.Connecting()`/`Connected()` in the live path (only `SpotifyAuthSession.cs:39/50`, never instantiated). It must be reported by the bootstrap via an explicit `onCredentialAcquired` hook (§3).
5. **Premium gate runs *after* the blob is persisted.** `credStore.Save(...)` at `SpotifyLiveLogin.cs:119` precedes any tier gate, so a Free account silent-resumes back into the wall. Clear on gate-fail (§6).
6. **Hydration ignores cancellation.** `Task.Run(() => HydratePlaylistHeadersAsync(..., ct))` (`LiveSessionHost.cs:82`) is passed `CancellationToken.None` today (`WaveeApp.cs:54`); logout must cancel it (§5).

---

## 1. Current state (exact anchors)

| Concern | Where | State today |
|---|---|---|
| Device-code protocol | `Backend/Spotify/Auth.cs:95-189` (`DeviceCodeProvider`) | Real, unit-tested vs `FakeHttpPost`; **not** E2E-verified (`SpotifyAuthSession.cs:8`). Client id `65b708073fc0480ea92a077233ca87bd` (`SpotifyLiveLogin.cs:14`). |
| Rich auth state | `Backend/Spotify/Auth.cs:26-29` (`AuthChallenge`, `AuthPhase`, `AuthState`) | Emitted by `AuthFlow.State`; consumed only by `ChallengeLogger` (`SpotifyLiveLogin.cs:61`). |
| Coarse seam | `Wavee.Core/Auth/Auth.cs:3-16` | `AuthStatus`, `WaveeUser`, `ISpotifySession{Status,CurrentUser,StatusChanged,ConnectAsync,LogoutAsync}`. |
| UI signals | `App/PlaybackBridge.cs:52-53,80-84` | `Signal<AuthStatus> Auth`, `Signal<WaveeUser?> User`; subscribed from `StatusChanged`. |
| Only auth UI | `Features/Shell/ShellToolbar.cs:129-148` | `ProfileChip`: "Sign in" → `ConnectAsync` / "Connecting…" / avatar with empty `OnClick`. |
| Gate | `WaveeApp.cs:19-74` | Always mounts `WaveeShell`; mount effect calls `Session.ConnectAsync()` (`:45`) and discards the `LiveSessionHost` (`:52`). |
| Live bootstrap | `SpotifyLive/LiveSessionHost.cs:26-86` | `StartAsync` → login → `GoLive` (`:41`) → hydrate; `DisposeAsync` disposes connect+transport only (`:88-93`). |
| Credentials | `Backend/Persistence/Credentials.cs:30-75` | `ICredentialStore{Save,Load,Clear}`; one key `spotify.credential` (`:39`); only the `ReusableBlob` is persisted. |

---

## 2. The model — a Core projection + the two-enum split

The UI needs the *rich* phase + challenge, but `Wavee.Core` must not depend on `Wavee.Backend.Spotify` (where `AuthChallenge`/`AuthState` live). So add a **Core-level projection** beside `AuthStatus`/`WaveeUser`, driven by a sink the bootstrap reports to:

```csharp
// Wavee.Core/Auth/Auth.cs  (NEW)
public enum LoginPhase { LoggedOut, SilentResume, RequestingCode, AwaitingApproval,
                         ChallengeExpired, Finalizing, Authenticated, Failed, PremiumRequired }
public sealed record LoginChallenge(string UserCode, string VerificationUri, string? VerificationUriComplete, DateTimeOffset Expiry);
public sealed record LoginSnapshot(LoginPhase Phase, LoginChallenge? Challenge = null, string? Error = null, WaveeUser? User = null);
public interface ILoginProgress { void Report(LoginSnapshot snapshot); }   // bootstrap → UI; marshalled to a signal
```

**Two enums, two jobs** (the load-bearing split):
- **Coarse `AuthStatus` drives the GATE.** `bridge.Auth.Value == Authenticated ⇒ WaveeShell`, else `LoginView`. This works *identically* for the fake demo (`FakeSpotifySession`) and the live path (`SwitchableSession`→`LiveSpotifySession`), because both already flip `AuthStatus` through the same `StatusChanged` subscription (`PlaybackBridge.cs:80`).
- **Rich `LoginSnapshot` drives the TAKEOVER's inner card** (which of A–F shows). The takeover never polls; it projects whatever the bootstrap last reported.

```
            bridge.Auth (coarse)            bridge.Login (rich)
   LoggedOut/Authenticating  ── gate ─▶  LoginView ── renders ─▶  A..F by LoginPhase
   Authenticated             ── gate ─▶  WaveeShell (+ avatar morph)
```

`PlaybackBridge` gains one signal + a thread-marshalling adapter (the bootstrap runs off the UI thread):

```csharp
// App/PlaybackBridge.cs  (after :53)
public Signal<LoginSnapshot> Login { get; } = new(new(LoginPhase.LoggedOut));
public ILoginProgress Progress(Action<Action> post) => new SignalProgress(this, post);
sealed class SignalProgress(PlaybackBridge b, Action<Action> post) : ILoginProgress {
    public void Report(LoginSnapshot s) => post(() => b.Login.Value = s);   // UI-thread write
}
```

`Auth`/`User` and the `StatusChanged` subscription (`:80-84`) are **unchanged** — they still drive the chip + the gate.

---

## 3. Sourcing the challenge — thread an observer through the login pipeline

The challenge is built *inside* `LoginAsync` (`SpotifyLiveLogin.cs:60`) and never escapes. Thread an `IObserver<AuthState>` alongside the existing `ChallengeLogger`, plus an explicit credential-acquired hook for `Finalizing` (the live `AuthFlow` never emits `Connecting`, correction #4):

```csharp
// SpotifyLiveLogin.cs:47 — add two optional params (back-compat: CLI passes neither)
public static async Task<LoginResult?> LoginAsync(Action<string> log, CancellationToken ct,
    bool retainChannel = false, bool allowDeviceCode = true,
    IObserver<AuthState>? authObserver = null, Action? onCredentialAcquired = null)
{
    ...
    var providers = allowDeviceCode
        ? new ICredentialProvider[] { new StoredCredentialProvider(() => credStore.Load()), device }
        : new ICredentialProvider[] { new StoredCredentialProvider(() => credStore.Load()) }; // silent resume
    var flow = new AuthFlow(providers);
    using var consoleSub = flow.State.Subscribe(new ChallengeLogger(log));
    using var uiSub = authObserver is null ? null : flow.State.Subscribe(authObserver);   // ← UI projection
    var cred = await flow.AcquireAsync(ct).ConfigureAwait(false);
    if (cred is null) return null;
    onCredentialAcquired?.Invoke();   // ← Finalizing boundary (before AP handshake/login5/dealer)
    ... // AP failover + APWelcome + credStore.Save (unchanged)
}
```

`SpotifyLiveSpclient.ConnectAsync` (`:16`) gains the same `allowDeviceCode`/`authObserver`/`onCredentialAcquired` and forwards them to `LoginAsync` (`:20`); it also surfaces the credential store on its result record:

```csharp
// add to LiveSpclient (Spclient.cs:10): ICredentialStore CredStore  (from login.CredStore)
```

`LiveSessionHost.StartAsync` builds the **adapter** that maps `AuthState → LoginSnapshot` and owns the terminal mapping (Welcome vs Failed vs Expired):

```csharp
sealed class AuthStateAdapter(ILoginProgress p, bool interactive) : IObserver<AuthState> {
    AuthPhase _last = AuthPhase.LoggedOut;
    public void OnNext(AuthState s) {
        _last = s.Phase;
        switch (s.Phase) {
            case AuthPhase.AwaitingCredential:
                p.Report(new(interactive ? LoginPhase.RequestingCode : LoginPhase.SilentResume)); break;
            case AuthPhase.AwaitingUser when s.Challenge is { } c:
                p.Report(new(LoginPhase.AwaitingApproval,
                    new LoginChallenge(c.UserCode, c.VerificationUri, c.VerificationUriComplete, c.Expiry))); break;
            case AuthPhase.ChallengeExpired:
                p.Report(new(LoginPhase.ChallengeExpired)); break;
        }
    }
    public void OnError(Exception e){} public void OnCompleted(){}
    // terminal when LoginAsync returned null and produced no credential
    public LoginSnapshot Terminal(bool credExisted) =>
        _last == AuthPhase.ChallengeExpired ? new(LoginPhase.ChallengeExpired)
      : (!interactive && !credExisted)      ? new(LoginPhase.LoggedOut)              // silent + no cred → Welcome
      :                                       new(LoginPhase.Failed, Error: "We couldn't reach Spotify. Check your connection and try again.");
}
```

`AuthPhase.AwaitingUser` already carries the challenge via `sink.Challenge(...)` (`Auth.cs:136`), and `ChallengeExpired` via `sink.Expired()` (`:180/186`) — the adapter just re-skins them. Poll edges (resilient through 5xx/network, `slow_down` += 5 s, terminal only on `expired_token`/`access_denied`/deadline) stay entirely in `DeviceCodeProvider` (`Auth.cs:143-187`).

**State table** (canonical; the takeover renders by `LoginPhase`):

| # | UI state | `LoginPhase` | `AuthPhase` | `AuthStatus` | Reported by | Screen |
|---|---|---|---|---|---|---|
| 0 | Welcome | `LoggedOut` | `LoggedOut` | `LoggedOut` | resting / silent-miss terminal | A |
| 1 | Silent resume | `SilentResume` | `AwaitingCredential` | `Authenticating` | adapter (`interactive:false`) | B (after 400 ms) |
| 2 | Requesting code | `RequestingCode` | `AwaitingCredential` | `Authenticating` | adapter (`interactive:true`) | B |
| 3 | Awaiting approval | `AwaitingApproval` | `AwaitingUser`+Challenge | `Authenticating` | adapter | C (pairing hero) |
| 4 | Challenge expired | `ChallengeExpired` | `ChallengeExpired` | `Authenticating` | adapter | D |
| 5 | Finalizing | `Finalizing` | — | `Authenticating` | `onCredentialAcquired` | B |
| 6 | Authenticated | `Authenticated` | — | `Authenticated` | bootstrap at `GoLive` | shell + morph |
| 7 | Failed | `Failed` | `Failed` | `Error` | adapter terminal / catch | E |
| 8 | Premium required | `PremiumRequired` | — | `Error` | bootstrap tier gate (§6) | F |

---

## 4. The bootstrap state machine + single-flight

`StartAsync` becomes progress-aware and **single-flight-safe**. The new signature:

```csharp
public static async Task<LiveSessionHost?> StartAsync(Services svc, Action<string> log, CancellationToken ct,
    ILoginProgress? progress = null, bool interactive = true)
```

Flow:
1. `report.Report(new(interactive ? RequestingCode : SilentResume))`.
2. **Silent fast-exit (correction #1):** if `!interactive` and `credStore.Load() is null`, report `LoggedOut` (Welcome) and return null *without* a network attempt. (Requires reading the store before the login attempt; expose a `CredStore` peek on the Spclient bring-up, or have `StartAsync` open the `LocalCredentialStore` itself — it is cheap and the same `FileLocalStore.ForApp("Wavee")`.)
3. `ConnectAsync(..., allowDeviceCode: interactive, authObserver: adapter, onCredentialAcquired: () => report.Report(new(Finalizing)))`.
4. On null: `report.Report(adapter.Terminal(credExisted))` (Welcome on silent-miss, else Failed/Expired). Return null.
5. **Tier gate (§6).** If `live.Session.Tier != Premium`: `live.CredStore.Clear()`, report `PremiumRequired`, dispose the AP channel, return null.
6. Build transport/connect (unchanged `:36-38`), `FetchProfileAsync` (`:39`).
7. **Attach BEFORE GoLive (correction #3):** create the host with an **owned `CancellationTokenSource` linked to `ct`**, then `ct.ThrowIfCancellationRequested()` (the supersede check), then `svc.AttachLive(host, live.CredStore)`, then `svc.GoLive(...)` (`:41`), then `report.Report(new(Authenticated, User: liveSession.CurrentUser))`.
8. Hydration uses **`host.Cts.Token`, not `ct`/`None`** (correction #6): `_ = Task.Run(() => HydratePlaylistHeadersAsync(fetcher, store, log, host.Cts.Token))`.

**Single-flight lives in `WaveeApp`** (it owns the user intents). One CTS ref; every entry point cancels the prior first:

```csharp
var booting = UseRef<CancellationTokenSource?>(null);
async Task StartLogin(bool interactive) {
    var prev = booting.Value;
    prev?.Cancel();                                   // prior StartAsync throws before its GoLive (step 7 check)
    if (prev != null) { try { await prev.WorkDone; } catch { } }  // await unwind to avoid socket churn
    var cts = CancellationTokenSource.CreateLinkedTokenSource(appCt);
    booting.Value = cts;
    try {
        await LiveSessionHost.StartAsync(svc, log, cts.Token, bridge.Progress(post), interactive);
        // host is AttachLive'd inside StartAsync (before GoLive); nothing else to capture here
    } catch (OperationCanceledException) { /* superseded — the winner owns the session */ }
}
```

Because the prior CTS is cancelled and step 7 re-checks `ct` immediately before `GoLive`, a stale flow **cannot** swap the backend — this closes both the button-mash race and the silent-`[stored]` vs interactive-`[device]` race (correction #2). Mount calls `StartLogin(false)` (real backend only); Welcome's "Continue", Expired's "Get a new code", and Error's "Try again" all call `StartLogin(true)`; "Cancel" calls `booting.Value?.Cancel()` → the genuine OCE (`Auth.cs:75`) → adapter reports `LoggedOut`.

The gate, in `WaveeApp.Render`:

```csharp
bool authed = bridge.Auth.Value == AuthStatus.Authenticated;   // subscribe → re-render on flip
Element leaf = authed ? Embed.Comp(() => new WaveeShell(_services.Settings))
                      : Embed.Comp(() => new LoginView(_services));
var root = Ctx.Provide(Services.Slot, _services, Ctx.Provide(PlaybackBridge.Slot, bridge,
           Ctx.Provide(LibraryBridge.Slot, libBridge, Ctx.Provide(LibraryStore.Slot, store, leaf))));
// FPS-HUD ZStack tail unchanged (WaveeApp.cs:80-88)
```

Providers stay **above** the gate so `LibraryBridge`/`LibraryStore`/`PlaybackBridge` subscriptions survive the takeover↔shell swap.

**Fake/`--screenshot` (correction #8):** the mount effect keeps `Session.ConnectAsync()` (`:45`) **only when `!UseRealBackend`** — the fake auto-authenticates → shell, so existing shell-page goldens still render. Drop the auto-connect for the real path; gate on silent resume instead. A `WAVEE_FAKE_CHALLENGE` env seed lets `--screenshot` render the pairing hero deterministically (the fake never emits a real challenge).

---

## 5. Logout & lifecycle (no process restart)

Add to `Services` (the switchables already make this rebuild-free):

```csharp
public LiveSessionHost? LiveHost { get; private set; }
public ICredentialStore? CredStore { get; private set; }
internal void AttachLive(LiveSessionHost host, ICredentialStore cred) { LiveHost = host; CredStore = cred; }

public void GoOffline() {                                   // mirror of GoLive (Services.cs:171)
    (Player  as SwitchablePlayer )?.SetInner(new FakePlaybackProvider());
    (Devices as SwitchableDevices)?.SetInner(new FakeConnectDevices());
    (Session as SwitchableSession)?.SetInner(new FakeSpotifySession());  // starts LoggedOut; NOT auto-connected
    LiveHost = null; CredStore = null;
}

public async Task LogoutAsync() {
    await Session.LogoutAsync();          // LiveSpotifySession.LogoutAsync → AuthStatus.LoggedOut → gate swaps to takeover
    CredStore?.Clear();                   // wipe the one persisted reusable blob (else next launch silent-re-logs-in)
    if (LiveHost is { } h) { LiveHost = null; await Task.Run(() => h.DisposeAsync().AsTask()); }   // off the UI thread
    GoOffline();
}
```

`LiveSessionHost` gains the owned CTS and cancels it on dispose:

```csharp
readonly CancellationTokenSource _cts;
public CancellationToken Token => _cts.Token;
public async ValueTask DisposeAsync() {
    _cts.Cancel();                  // stop hydration / in-flight fetches (correction #6)
    _connect.Dispose(); _transport.Dispose();
    _cts.Dispose();
}
```

**Teardown order** (matters): `Session.LogoutAsync` first (flips the gate → shell unmounts, logout menu gone, takeover up) → `CredStore.Clear()` → `LiveHost.DisposeAsync()` (cancels hydration, closes dealer+AP) → `GoOffline()` (switchables back to fresh fakes; the bridge keeps working). After logout the app sits on the takeover Welcome; "Continue" re-enters `StartLogin(true)` against the **real** path. The fake placeholder is never auto-connected, so it can't bypass the login.

**Account menu + confirm** reuse the shell's overlay host (no new host):

```csharp
// ProfileChip Authenticated branch (ShellToolbar.cs:141) — replace OnClick={} with:
svc.Open(() => anchor.Value, () => MenuFlyout.Build(items, close),
         FlyoutPlacement.BottomEdgeAlignedRight,
         new PopupOptions(FocusTrap:true, DismissBehavior:DismissBehavior.LightDismiss));
// items: "Manage account on the web" (HyperlinkButton spotify.com/account), "Switch account…" (logout→login), "Log out"
// "Log out" → ContentDialog.Create(title:"Log out of Wavee?", message:"You'll need to authorize Wavee again to stream your library.",
//             primaryText:"Log out", closeText:"Cancel", defaultButton:DefaultBtn.Close, PrimaryClick: () => _ = svc.LogoutAsync());
```

Tag the `PersonPicture` (`ShellToolbar.cs:136`) `with { MorphId = "account:me" }` for the success/logout avatar morph (§8).

---

## 6. Premium gate (in-bootstrap, replaces the pre-window MessageBox)

Today `SessionGate.IsAllowed` is enforced *pre-window* in `Program.cs:155` (defaults to Premium since there is no real login) and again in the headless `SpotifyLiveLogin.RunAsync:35` — but **not** in `LiveSessionHost.StartAsync`, which just folds the tier into `IsPremium` (`:40`). Add the gate in `StartAsync` step 5:

```csharp
if (live.Session.Tier != Tier.Premium) {
    live.CredStore.Clear();                       // correction #5 — the blob was already saved (SpotifyLiveLogin.cs:119)
    progress.Report(new(LoginPhase.PremiumRequired));
    live.ApChannel?.Dispose();
    return null;                                   // do NOT GoLive
}
```

`Program.cs:155-160` keeps `PremiumGate.ShowWarning()` **only** for the forced `--free`/`WAVEE_FORCE_FREE` test path; a real Free login now resolves to the in-app `PremiumRequired` card (screen F) with "Upgrade on spotify.com" / "Use another account" / "Log out" — the process stays alive. Copy reuses `SessionGate.WarningTitle`/`WarningBody` verbatim.

---

## 7. Both auth methods (device-code now, PKCE loopback next)

Both methods terminate in the **same** AP login: `DeviceCodeProvider` returns `Credential(OAuthToken, "", accessToken)` which `SpotifyConnection.HandshakeAndLoginAsync` (`SpotifyLiveLogin.cs:99`) consumes; a browser Authorization-Code+PKCE flow yields the **same** `OAuthToken` shape, so it slots into `AuthFlow` as a peer of `DeviceCodeProvider` with **zero** downstream change.

**New** `LoopbackOAuthProvider : ICredentialProvider` (AOT-clean, no NuGet):
1. Bind `HttpListener` on `http://127.0.0.1:0/callback` (OS-assigned free port).
2. PKCE: `code_verifier` = 64 random bytes base64url; `code_challenge` = base64url(SHA256(verifier)); random `state`.
3. Open the system browser to `accounts.spotify.com/authorize?client_id=…&response_type=code&redirect_uri=http://127.0.0.1:<port>/callback&code_challenge_method=S256&code_challenge=…&scope=<Scopes>&state=…` via a PAL shell-open (`Process.Start{UseShellExecute=true}` on Windows; the macOS/Linux seam later).
4. Await the callback; validate `state`; POST `code`+`verifier` to `/api/token`; return `Credential(OAuthToken, "", access_token)`.

**Concurrency model** — the takeover's CTA is method-agnostic ("Continue with Spotify"), so the surface is identical either way:

- **Progressive (recommended for v1, what `docs/prototypes/login.html` shows):** one provider active at a time. `AuthFlow([stored, loopback])` for the primary; a "Use a code instead" affordance restarts as `AuthFlow([stored, device])`. Simplest; no racing.
- **Simultaneous (the two-pane "OR" concept):** show the device-code hero (from `DeviceCodeProvider` raising the challenge + polling in the background) *and* a "Log in via browser" button that fires `LoopbackOAuthProvider` on click. First credential wins; cancel the loser. Implement as a `RacingInteractiveProvider` whose `AcquireAsync` runs both children under a linked CTS and returns the first non-null (`Task.WhenAny`), then cancels the other. This is the only net-new orchestration the two-pane layout needs.

> **Open decision** (carried from the design review): progressive vs simultaneous. The prototype is progressive; the original concept mockup was simultaneous. Either is a layout swap of the same components — pick before Phase 5.

> **Blocking risk** (Phase 0): it is unverified that Spotify's public API honors RFC 8628 device-code for this `client_id`. If `--spotify-login` (`Program.cs:67`) does not mint+poll to a token E2E, build `LoopbackOAuthProvider` **first** and make it primary; the UI is unaffected.

---

## 8. UI — the `LoginView` takeover + chip/menu

**New** `Features/Auth/LoginView.cs` — a `Component` reading `bridge.Login` (+ `bridge.Auth`), rendering one full-window Mica surface → one centered content card (`WaveeColors.Content`, width 440, `Corners=8`, `Shadow=Elevation.Card`, padding 32) → exactly one accent-filled primary per screen. Screens A–F per the state table; copy + wireframes are in the approved design (`design-the-login-fluttering-ocean.md`) and realized in `docs/prototypes/login.html` / `account.html`.

- **Code rendering (a11y, correction #7):** the code (`TextEl{ Size=34, FontFamily=<mono>, CharSpacing=120 }`) gets an **accessible name that spells/groups it** (e.g. "W D J B dash M J H T") so a screen reader doesn't read "wdjbmjht". Display only — Spotify normalizes input.
- **Countdown:** "Expires in mm:ss" from `Challenge.Expiry` driven by a **1 Hz signal write**, never per-frame — this is the zero-alloc tripwire (phases 6–13 must stay green). Plain text, **not** a live region (a 1 Hz polite region would spam announcements). Announce only "Copied" and Failed/Expired.
- **Transitions:** keyed child cross-fade between cards (`Enter=EnterExit(Dy:6,Opacity:0)`, `Exit=EnterExit(Opacity:0)`); success → shell via the takeover root `Exit` (scale 1.04 + blur + opacity) while `WaveeShell` enters through its existing `UseSoftReveal` (`WaveeApp.cs:68`); avatar morph by tagging the success avatar and the toolbar `PersonPicture` (`ShellToolbar.cs:136`) the same `MorphId="account:me"` and calling `SharedTransition.Begin("account:me")` on the `Authenticated` edge. **Reduced-motion** (`Motion.ReducedMotion`) → instant swaps + chip fade, branched inside the generator (never a hook branch), matching the shell.
- **A11y baseline + one net-new primitive:** there is **no UIA live-region announce in the engine today** (verified). Add a `LiveRegion` raise in `FluentGpu.Windows/Uia` (polite for status/"Copied"; assertive for Failed/Expired); until it lands, set `Role`+accessible `Name` on the status node and move focus to the error heading on Failed. Every action (Continue, Copy, Open, Cancel, Get-a-new-code, menu items) needs a visible 2px focus ring; `ContentDialog` already traps+restores focus.

---

## 9. File-by-file change list

| # | File / anchor | Change | Phase |
|---|---|---|---|
| 1 | `Wavee.Core/Auth/Auth.cs` | Add `LoginPhase`/`LoginChallenge`/`LoginSnapshot`/`ILoginProgress`. `ISpotifySession` untouched. | 1 |
| 2 | `App/PlaybackBridge.cs:53` | Add `Signal<LoginSnapshot> Login` + `Progress(post)` adapter. | 1 |
| 3 | `SpotifyLive/SpotifyLiveLogin.cs:47` | `LoginAsync(…, allowDeviceCode, authObserver, onCredentialAcquired)`; build providers conditionally; subscribe observer alongside `ChallengeLogger`; fire `onCredentialAcquired` after `:63`. | 1 |
| 4 | `SpotifyLive/SpotifyLiveSpclient.cs:16` | Thread the three params to `LoginAsync`; add `CredStore` to `LiveSpclient`. | 1 |
| 5 | `SpotifyLive/LiveSessionHost.cs:26` | `StartAsync(…, ILoginProgress?, bool interactive)`; `AuthStateAdapter`; silent fast-exit; tier gate; **AttachLive before GoLive**; report Finalizing/Authenticated/Failed/PremiumRequired; owned CTS; hydration on `host.Token`. | 1, 4, 5 |
| 6 | `App/Services.cs` | `LiveHost`/`CredStore`/`AttachLive`/`GoOffline`/`LogoutAsync`. | 3 |
| 7 | `WaveeApp.cs:40-74` | Stop fake auto-connect on real path; single-flight `StartLogin`; silent resume on mount; gate leaf (`WaveeShell` vs `LoginView`); keep FPS-HUD tail. | 2 |
| 8 | `Features/Shell/ShellToolbar.cs:129` | Account `MenuFlyout` via `Overlay.Service`; `MorphId="account:me"` on the avatar. | 3 |
| 9 | `Program.cs:155-160` | Pre-window gate only for forced `--free`; real Free → in-app `PremiumRequired`. | 4 |
| 10 | **New** `Features/Auth/LoginView.cs` | The takeover (screens A–F); owns Cancel/Retry/Get-a-new-code → `WaveeApp.StartLogin`. | 2 |
| 11 | **New** `Backend/Spotify/LoopbackOAuthProvider.cs` | PKCE loopback `ICredentialProvider` + (optional) `RacingInteractiveProvider`. | 5/next |
| 12 | **New** `Features/Auth/QrGrid.cs` | Dependency-free QR matrix → `BoxEl` grid; encodes `VerificationUriComplete`. | 5 |
| 13 | `assets/loc/en-US.json` | `auth.*` keys → generated `Strings.Auth.*`. | 2–4 |

---

## 10. Phased build order + acceptance gates

- **Phase 0 — Verify (blocking, no UI).** Run `--spotify-login` against live Spotify; confirm device-code mints+polls E2E. Pass → device-code first; fail → `LoopbackOAuthProvider` first (becomes primary). *Gate: a real token in hand, or the decision to flip primary.*
- **Phase 1 — Plumbing (no visible UI).** Files 1–5. *Gate: `--real-backend` run logs `bridge.Login` transitions LoggedOut→…→Authenticated; build clean; VerticalSlice green.*
- **Phase 2 — Working login.** Files 7, 10 (Welcome/Splash/**Pairing hero (code+URL+Copy)**/Expired/Error; no QR/morph/premium). *Gate: real login round-trips to the shell; `--screenshot` of each card in Dark + warm-Light.*
- **Phase 3 — Logout.** Files 6, 8. *Gate: menu→confirm→logout→Welcome with **no process restart**; relaunch → silent resume → shell; creds wiped (no silent re-login).*
- **Phase 4 — Premium + edges.** Files 5(gate), 9; humanized errors; `LiveRegion`. *Gate: Free account → `PremiumRequired` card, process alive.*
- **Phase 5 — Polish + both methods.** Files 11, 12; success morph + reduced-motion; 1 Hz countdown; `WAVEE_FAKE_CHALLENGE`; PKCE loopback → swap headline CTA, demote device-code to "Use a code instead". *Gate: contrast in Dark/warm-Light/Mica-solid; zero-alloc still green.*

**Every phase:** `dotnet build src/FluentGpu.slnx` clean; `dotnet run --project src/FluentGpu.VerticalSlice` → "ALL CHECKS PASSED" (the 1 Hz countdown must be a signal write, not per-frame).

---

## 11. Testing

- **Unit (deterministic, no network):** the existing `FakeHttpPost` (`Auth.cs:221`) + `DeviceCodeProvider` drive pending/`slow_down`/`expired_token`/`access_denied`/success; assert the `AuthStateAdapter` emits the matching `LoginSnapshot` sequence, including silent-miss → `LoggedOut` (correction #1) and Finalizing fired exactly once.
- **Single-flight (correction #2):** spawn N overlapping `StartLogin(true)`; assert exactly one `GoLive` and one live transport (mock `Services.GoLive` counter).
- **Logout (corrections #3, #6):** authenticate → immediate `LogoutAsync`; assert `CredStore.Clear()` ran, `DisposeAsync` cancelled hydration, and a relaunch silent-resume → Welcome (not auto-login).
- **VerticalSlice:** add a headless `LoginView` mount under `WAVEE_FAKE_CHALLENGE` to enforce 0 managed alloc on phases 6–13 with the countdown ticking.
- **`--screenshot`:** every card (A–F) + account menu (G) + logout dialog (H), Dark + warm-Light.
- **Manual E2E:** `dotnet run --project src/FluentGpu.WindowsApp -- --real-backend` → Welcome → Continue → (browser or "Use a code" → approve) → shell; logout round-trip; pull network mid-poll → "Reconnecting…" subline, no flash; Free account → `PremiumRequired`.

---

## 12. Risks & open questions

1. **Device-code E2E (blocking).** Phase 0 resolves it; PKCE loopback is the fallback-as-primary.
2. **Layout variant.** Progressive (prototype) vs simultaneous two-pane (concept). Pick before Phase 5; §7 designs both.
3. **`HttpListener` loopback** needs a free port + the OS firewall allowing `127.0.0.1` (no inbound rule needed for loopback). PKCE keeps it safe without a client secret.
4. **Re-auth over a populated shell** (token revoked mid-session) is only reachable if a live transport demotes the session on a 401 (`LiveSpotifySession` never self-demotes today, `Switchable.cs:147`). Either wire a mid-session auth-failure → `LogoutAsync`-style demote, or scope it out — the catalog persists in SQLite, so a takeover-then-repopulate is acceptable and rare.
5. **macOS/Linux** browser-open + keychain protectors already have seams (`KeyringProtector`, `SpotifyLiveLogin.cs:52`); the loopback `Process.Start` shell-open is the only PAL gap.

---

## 13. Localization keys (`auth.*`)

`welcomeTitle, welcomeTagline, continueWithSpotify, useACode, noPasswordHelper, restoringSession, gettingCode, signingIn, authorizeTitle, step1, step2, copyCode, copied, scanToSkip, waitingApproval, expiresIn, antiPhishing, openPairPage, cancel, codeExpired, codeExpiredBody, getNewCode, couldntSignIn, networkError, tryAgain, back, premiumTitle, premiumBody, upgrade, useAnotherAccount, manageAccount, switchAccount, logOut, logoutConfirmTitle, logoutConfirmBody` → generated `Strings.Auth.*` (compile-safe loc-keys generator).

---

**Related:** approved design + prototypes — `design-the-login-fluttering-ocean.md`, `docs/prototypes/{login,account}.html`. Backend context — `wavee-spotify-connect-playback-analysis.md`, `wavee-native-backend-architecture.md`. App architecture — `app/docs/architecture.md`.
