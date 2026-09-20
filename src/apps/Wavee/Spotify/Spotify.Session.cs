// ── Spotify/Spotify.Session.cs ─────────────────────────────────────────────────────────────────────────────────────
// AP socket, login5, tokens, dealer loop, audio keys
//
// Role: SHELL
// Owner: D
// Wave: 2
// Budget: 1600 lines
// Spec: plan
//
// THE SOCKETS. Everything in this file is I/O: two named threads (the AP channel and the dealer websocket), a third
// that serves the 30 s keepalive and the server-clock re-probe, the three HTTP mints (apresolve, clienttoken,
// login5), the audio-key request table, and the interactive sign-in's two flows (§13: the PKCE loopback listener and the
// device-grant poll, each on its own named thread). Every DECISION they need is in `Spotify.cs` / `Spotify.OAuth.cs`
// and is pure.
//
// The rules this file is written under (C1-C10):
//   C1  this file never writes a table, an edge or a signal, and never touches `Entities.Strings`, from a shell
//       thread. Everything it learns becomes a `SessionEvent` handed to `Post`; `Apply` runs on the UI thread, folds
//       it with `Step`, publishes the new session and writes the signals. The ONE place text is interned is inside a
//       posted action — i.e. on the UI thread — which is why `PublishWelcome` takes `string`s and not `StringId`s.
//   C4  every thread runs for ONE epoch OF ITS OWN TRANSPORT (B4). The AP thread runs for `Session.ApEpoch` under the
//       AP's `CancellationTokenSource`; the dealer thread and its keepalive for `Session.Epoch` under the dealer's. A drop
//       bumps only its own transport's epoch, cancels only its own source and closes only its own socket; everything a
//       thread publishes is stamped with its epoch, so a late word from an abandoned one is refused by the fold
//       (`IsStale`) and never reaches the other transport. Only the END of the login (a sign-out, a disconnect, a
//       refusal) closes both — and cancels the login's own source, which the token mints watch. Every thread exit is one
//       always-on `ap.exit` / `dealer.exit` line naming its reason and epoch.
//   C8  bounded everywhere: 32 audio-key slots (a full table refuses rather than queues), one dealer frame at a time,
//       a fixed 256 KiB dealer scratch buffer.
//   C9  no shell call ever blocks the UI thread. `Login`, `Logout` and `Reply` return immediately; `AccessToken` and
//       `RequestAudioKey` BLOCK and are for shell threads only (their doc comments say so).
//   P10 three timers, all named: the dealer keepalive (30 s), the server-clock re-sync (10 min), and the AP keepalive's
//       one-shot (B6, `ApPulse`), re-armed to the pure `ApKeepAlive` fold's next deadline — the held pong or a watchdog.
//       Nothing polls.
//
// ASYNC IS NOT USED, ON PURPOSE. These threads exist to block: a socket read, a key wait, a 30 s tick. `HttpClient`
// has a synchronous `Send`, and the one API that does not (`ClientWebSocket`) is awaited with `GetAwaiter().GetResult()`
// ON THE DEALER'S OWN THREAD. That keeps the shell readable as one procedure per connection and keeps the app free of
// async state machines outside the engine (P9 bans them in core; here they would simply buy nothing).
//
// WHAT IS NOT HERE: the metadata decode (owner E, `Spotify.Decode*.cs`), the request functions and the audio stream
// (owner F, `Spotify.Api.cs` / `Spotify.Audio.cs`), and the Connect glue (owner F, `Spotify.Connect.cs`) — the dealer
// loop hands every non-protocol frame to `Connect.OnDealer` and knows nothing about what it means.
//
// WHAT COMES FROM `Platform`, DIRECTLY (no seam, no delegate): the protected credential slot
// (`TryLoadCredential`/`SaveCredential`/`ClearCredential`), the launch-stable `DeviceId`, the launch locale
// (`Locale.SpotifyLanguage`), the account redaction (`Redact`) and the always-on log (`Log.Info/Warn`). `Platform.Boot`
// runs before `Spotify.Boot`, and the dependency is one-way: Platform never calls into Spotify.

using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentGpu.Signals;
using Google.Protobuf;

namespace Wavee;

public static partial class Spotify
{
    // ── 1. the post seam, the credential map, the log ─────────────────────────────────────────────────────────────────

    /// <summary>THE marshaller back to the UI thread (the same contract as <c>Store.Post</c>): <c>App.cs</c> sets it to
    /// <c>AppHost.Post</c>, a test leaves it, and the default runs the action inline — which is what makes every unit
    /// test in this file's suite single-threaded and deterministic.</summary>
    public static Action<Action> Post { get; set; } = static a => a();

    // LOGGING is `Platform`'s always-on stream (`Log.Info/Warn/Error`), under the category "spotify". There is no
    // sink of our own and no level switch: CLAUDE.md's "always-on logs, no environment switches" is the rule, and a
    // second logger would be a second place to look. NOTHING here may carry a token, a credential or a key — the byte
    // COUNT is the most that is ever printed, and an account name goes through `Platform.Redact`.

    /// <summary>How a credential was obtained, and therefore how the AP wants it presented.</summary>
    public enum CredentialKind : byte
    {
        None = 0,
        /// <summary>The reusable blob the APWelcome handed back — base64 in the store, raw bytes on the wire.</summary>
        ReusableBlob = 1,
        /// <summary>A fresh OAuth access token (the first login, before a blob exists).</summary>
        OAuthToken = 2,
    }

    /// <summary>What a login needs. The secret is never logged and never leaves this file.</summary>
    public readonly record struct Credential(CredentialKind Kind, string Username, string Secret)
    {
        public bool IsEmpty => Kind == CredentialKind.None || string.IsNullOrEmpty(Secret);
    }

    /// <summary>The persisted credential, from <c>Platform</c>'s protected slot. The two types are deliberately
    /// separate — <c>Platform.Credential</c> is what is on DISK (it also carries login5's unread <c>Refresh</c>) and
    /// this is what a LOGIN takes — and the kinds are numbered to match, so the map is a cast and not a switch that
    /// can drift (<c>Platform/Platform.cs</c> §5 says so from its side).
    /// <para>Empty when the slot was never opened (a unit test) or holds nothing: the fold answers
    /// <see cref="SessionFault.NoCredential"/> and no socket is opened.</para></summary>
    static Credential LoadCredential()
    {
        lock (InteractiveGate) if (!s_interactive.IsEmpty) return s_interactive;
        return Platform.TryLoadCredential(out var stored) && !stored.IsEmpty
            ? new Credential((CredentialKind)(byte)stored.Kind, stored.Username, stored.Secret)
            : default;
    }

    /// <summary>The OAuth access token an interactive sign-in just obtained (§13). IN MEMORY ONLY — 0.2.9 never persisted
    /// the OAuth token either: the AP login presents it once, and the welcome's reusable blob is what reaches the slot.
    /// It wins over the slot while present (the user just signed in, whatever was stored), survives a network retry, and
    /// is dropped by the welcome that replaced it, a sign-out, or a verdict that clears the credential.</summary>
    static Credential s_interactive;
    static readonly Lock InteractiveGate = new();

    static void ClearInteractive()
    {
        lock (InteractiveGate) s_interactive = default;
    }

    // ── 2. the client identity ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The desktop client identity Spotify's gateway checks. ONE definition — the spclient headers, login5,
    /// the client-token attestation and the Connect device all present the same id, or the account sees mismatched
    /// clients. Pinned to Spotify 1.2.94.583, both values observed on the wire. No environment overrides: 0.3 has no
    /// environment switches for behaviour (CLAUDE.md), so a pin moves by editing this block.</summary>
    public static class Identity
    {
        /// <summary>Spotify's public desktop ("keymaster") client id.</summary>
        public const string ClientId = "65b708073fc0480ea92a077233ca87bd";
        public const string AppVersion = "129400583";
        public const string ClientVersion = "1.2.94.583.g60394bd5";
        public const string DesktopSemver = "1.2.94.583";
        /// <summary>The web-player build token the two web-bundle pathfinder operations send. Not a semver, observed
        /// verbatim, and it does not move when the desktop pin moves.</summary>
        public const string WebPlayerAppVersion = "896000000";

        public static string AppPlatform { get; } =
            RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "Win32_ARM64" : "Win32_x86_64";

        /// <summary>spclient / pathfinder User-Agent.</summary>
        public static string UserAgent { get; } = "Spotify/" + AppVersion + " " + AppPlatform + "/" + OsDescriptor();

        /// <summary>clienttoken.spotify.com uses a shorter OS stub.</summary>
        public static string ClientTokenUserAgent { get; } = "Spotify/" + AppVersion + " " + AppPlatform + "/0 (PC laptop)";

        public static string OsDescriptor()
        {
            if (!OperatingSystem.IsWindows()) return Environment.OSVersion.VersionString;
            var v = Environment.OSVersion.Version;
            string arch = RuntimeInformation.OSArchitecture switch
            {
                Architecture.Arm64 => "ARM",
                Architecture.X64 => "x64",
                _ => RuntimeInformation.OSArchitecture.ToString(),
            };
            return "Windows " + v.Major + " (" + v.ToString(3) + "; " + arch + ")";
        }
    }

    // ── 3. the text arena ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>APPEND-ONLY UTF-8 storage for the session's text (tokens, hosts, the connection id, the device id).
    /// Written by whichever shell thread learnt the value, read as a span by any thread, forever: an entry is never
    /// moved or overwritten, and growth copies into a LARGER array while leaving the old one intact, so a reader
    /// holding the old array keeps reading the same bytes at the same offsets. That is what lets the HTTP shell read a
    /// bearer with no lock, no interner and no string (file header of `Spotify.cs`).</summary>
    static class Text
    {
        static readonly Lock Gate = new();
        static byte[] _buffer = new byte[8192];
        static int _length;

        public static TokenRef Add(ReadOnlySpan<byte> utf8)
        {
            if (utf8.IsEmpty) return default;
            lock (Gate)
            {
                if (_length + utf8.Length > _buffer.Length)
                {
                    int size = Math.Max(_length + utf8.Length, _buffer.Length * 2);
                    var grown = new byte[size];
                    _buffer.AsSpan(0, _length).CopyTo(grown);
                    Volatile.Write(ref _buffer, grown);
                }
                utf8.CopyTo(_buffer.AsSpan(_length));
                var r = new TokenRef(_length, utf8.Length);
                _length += utf8.Length;
                return r;
            }
        }

        public static TokenRef Add(string s) => s.Length == 0 ? default : Add(Encoding.UTF8.GetBytes(s));

        public static ReadOnlySpan<byte> Span(TokenRef r)
            => r.IsEmpty ? default : Volatile.Read(ref _buffer).AsSpan(r.Offset, r.Length);
    }

    /// <summary>The bytes behind a session <see cref="TokenRef"/>. Safe from any thread (see <see cref="Text"/>).</summary>
    public static ReadOnlySpan<byte> Utf8(TokenRef r) => Text.Span(r);

    /// <summary>The text behind a session <see cref="TokenRef"/> as a string. ALLOCATES — for a header value or a url,
    /// not for a per-row path.</summary>
    public static string TextOf(TokenRef r) => r.IsEmpty ? string.Empty : Encoding.UTF8.GetString(Text.Span(r));

    /// <summary>Do two arena slices hold the same BYTES? A slice is a position, not a value: the same text learnt twice — a
    /// connection id redelivered by a second pusher hello — lands at two offsets, and comparing the <see cref="TokenRef"/>s
    /// says "different". Safe from any thread and deterministic (an entry never moves or changes once written), which is
    /// why the pure <see cref="Step"/> may call it. No allocation.</summary>
    public static bool SameText(TokenRef a, TokenRef b)
        => a == b || (a.Length == b.Length && Text.Span(a).SequenceEqual(Text.Span(b)));

    /// <summary>Write <paramref name="s"/> into the session text arena and return its slice. Safe from any thread (see
    /// <see cref="Text"/>). Public for the facts (this assembly has no <c>InternalsVisibleTo</c>): a fold that compares
    /// arena TEXT (<see cref="SameText"/>) is pinned over real slices, not made-up offsets.</summary>
    public static TokenRef AddSessionText(string s) => Text.Add(s);

    // ── 4. the published session ─────────────────────────────────────────────────────────────────────────────────────

    sealed class Box
    {
        public readonly Session Value;
        public Box(in Session value) => Value = value;
    }

    static Box s_box = new(default);

    /// <summary>The session, as of the last fold. A struct copy out of an IMMUTABLE box, so a reader on any thread
    /// sees a whole transition or none of it.</summary>
    public static Session Current => Volatile.Read(ref s_box).Value;

    /// <summary>The phase, as a signal, for the shell's connection chrome. UI THREAD (it is written inside the post).</summary>
    public static Signal<SessionPhase> Status { get; } = new(SessionPhase.Offline);

    /// <summary>The last fault, as a signal — what the "can't connect" card reads.</summary>
    public static Signal<SessionFault> Fault { get; } = new(SessionFault.None);

    /// <summary>Hand an event to the UI thread (C1). Every shell thread reports through here and nowhere else.</summary>
    static void Publish(SessionEvent e) => Post(() => Apply(e));

    /// <summary>UI THREAD: fold the event, publish the session, write the signals, run the effects that are not part
    /// of a running thread's own procedure.
    /// <para>Which effects are executed here and which are a RECORD: the AP thread is one straight-line procedure
    /// (resolve → connect → handshake → login → mint → pump), so <c>OpenAp</c> and <c>Mint*</c> describe what that
    /// procedure is already doing and are asserted by the tests rather than dispatched twice. Everything that
    /// CROSSES a thread is executed: <c>ResolveHosts</c> starts the AP thread (one per AP epoch), <c>ApBackoff</c> /
    /// <c>DealerBackoff</c> arm that transport's one-shot retry timer, <c>OpenDealer</c> starts the websocket thread,
    /// <c>CloseAp</c> abandons the AP epoch alone, <c>CloseDealer</c> the dealer epoch alone (and empties the Connect
    /// mailbox: every queued cluster describes a connection that no longer exists — G-036), both at once (<c>CloseAll</c>,
    /// the login ended) the login's token as well, the two credential effects call the store seam, <c>SignedOut</c> tears
    /// the account's surfaces down, <c>Welcome</c> adopts the account's market and catalog scope, and
    /// <c>AnnounceDevice</c> asks the Connect glue for the hello PUT.</para>
    /// <para>A transport's word from an epoch it already abandoned (<see cref="IsStale"/>) folds to nothing — and says so
    /// in one line, so a log shows the other transport was never touched by it (B4).</para>
    /// <para>The credential slot moves BEFORE the signals do: an observer folding "the phase" with "is a credential
    /// stored" (the shell's auth fold) must see the slot as of this transition, not the one before it.</para></summary>
    internal static void Apply(in SessionEvent e)
    {
        Session s = Current;
        if (IsStale(in s, in e))
        {
            Log.Info("spotify", "session.stale kind=" + e.Kind + " epoch=" + e.Epoch
                + " live=" + (LinkOf(e.Kind) == SessionLink.Ap ? s.ApEpoch : s.Epoch) + " — ignored");
            return;
        }
        SessionEffects fx = Step(ref s, e);
        Volatile.Write(ref s_box, new Box(s));

        if ((fx & SessionEffects.ClearCredential) != 0)
        {
            ClearInteractive();
            ForgetAccount();
            Platform.ClearCredential();
            Log.Info("spotify", "stored credential cleared");
        }
        if ((fx & SessionEffects.SaveCredential) != 0) SaveWelcomeCredential();

        Status.Value = s.Phase;
        Fault.Value = s.Fault;

        if ((fx & SessionEffects.CloseAll) == SessionEffects.CloseAll) CloseLogin();
        if ((fx & SessionEffects.CloseAp) != 0) CloseAp();
        if ((fx & SessionEffects.CloseDealer) != 0) { CloseDealer(); Connect.Clear(); }
        if ((fx & SessionEffects.SignedOut) != 0) SignOutTeardown();
        if ((fx & SessionEffects.Welcome) != 0) AdoptWelcome(in s);
        if ((fx & SessionEffects.ResolveHosts) != 0) StartAp(s.ApEpoch);
        if ((fx & SessionEffects.OpenDealer) != 0) StartDealer(s.Epoch);
        if ((fx & SessionEffects.ApBackoff) != 0) ArmApRetry(in s);
        if ((fx & SessionEffects.DealerBackoff) != 0) ArmDealerRetry(in s);
        if ((fx & SessionEffects.AnnounceDevice) != 0) Connect.AnnounceDevice();
    }

    /// <summary>UI THREAD (the <see cref="SessionEffects.SignedOut"/> effect): the account is gone from this PC. Playback
    /// gives up ownership — only where a player host exists, which is exactly when <c>Playback.Boot</c> has set
    /// <c>Connect.Hello</c> — and the account's OS surfaces come down: the jump list's recents and every scheduled toast
    /// (G-036/G-037, ch 14 DATA GAP 8). The OS half runs only where there is a window; a headless run and a unit test own
    /// no jump list and no toast registration to take down.</summary>
    static void SignOutTeardown()
    {
        if (Connect.Hello is not null) Playback.Post(Playback.Input.Release(Playback.ReleaseCause.Logout));
        if (FluentGpu.FluentApp.WindowHandle != 0) SignOutOsSurfaces();
    }

    /// <summary>The credential is gone, so the login it produced is too: <see cref="AccessToken"/> must not keep minting
    /// bearers from the reusable blob of an account that signed out or was refused. The client token stays — it attests
    /// the device, not the account.</summary>
    static void ForgetAccount()
    {
        // No TokenGate here: a login5 refresh holds it for a whole HTTP round trip and this runs on the UI thread (C9). A
        // refresh racing past these writes is harmless — `AccessToken` refuses to answer once the login is gone.
        Volatile.Write(ref s_login, null);
        Volatile.Write(ref s_accessToken, null);
        Volatile.Write(ref s_accessExpiresAtMs, 0);
    }

    /// <summary>Its own method, so a host with no window never even compiles a reference to the OS surfaces.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static void SignOutOsSurfaces()
    {
        Playback.Os.SignedOut();
        Notify.SignedOut();
        Log.Info("spotify", "signed out: playback released, jump list and scheduled toasts cleared");
    }

    /// <summary>UI THREAD (the <see cref="SessionEffects.Welcome"/> effect): the ONE place the signed-in account reaches
    /// the rest of the app. The market goes to <c>Api.Market</c> (metadata requests stop going out market-less) and the
    /// catalog switches to <see cref="WelcomeScope"/> — once per real change: a reconnect's welcome for the same account,
    /// market and tier leaves the table set alone. A host that never booted <c>Entities</c> (a unit test) switches
    /// nothing.</summary>
    static void AdoptWelcome(in Session s)
    {
        // A new AP session is a new answer from the audio-key service: an earlier refusal latch (and the keys cached under
        // another account) must not outlive the login that produced them (G-034).
        Audio.ResetKeyLatch();

        string account = s.Username.IsEmpty ? "" : Entities.Strings.Resolve(s.Username);
        string market = WelcomeMarket(s.Country.IsEmpty ? "" : Entities.Strings.Resolve(s.Country),
                                      Entities.Current?.Key ?? default, account);
        Api.Market = market;
        // `session.lastAccount` (G-031): the scope's fallback when the slot is empty, and the account-switch witness. The
        // library itself needs no reset here — the catalog scope is partitioned by account, so a switch below opens the
        // other account's table set.
        if (RememberAccount(Platform.Settings, account) == AccountChange.Switched)
            Log.Info("spotify", "account switched (" + Platform.Redact(account) + ")");

        var scope = Entities.Current;
        if (scope is null) return;
        CatalogScope next = WelcomeScope(scope.Key, account, market, s.Tier);
        if (next == scope.Key) return;
        Entities.Switch(next);
        Playback.Rebind();               // G-241: the restored deck follows the scope switch before the next drain
        Log.Info("spotify", "catalog scope adopted (" + Platform.Redact(next.Account) + ", market " + next.Market
            + ", tier " + s.Tier + ")");
    }

    /// <summary>The market a welcome settles on. PURE. A welcome whose country packet (0x1b) missed its window arrives with
    /// no country; on a RECONNECT of the same account that must keep the market already held — adopting "" would switch the
    /// catalog scope (and so the whole table set) because of a late packet, the same class of defect as a late ProductInfo
    /// ending the login (B6 follow-up A). A first login, or another account, takes what arrived, empty included.</summary>
    public static string WelcomeMarket(string arrived, CatalogScope held, string account)
        => arrived.Length > 0 || account.Length == 0 || held.Account != account ? arrived : held.Market ?? "";

    // ── 5. boot, login, logout ───────────────────────────────────────────────────────────────────────────────────────

    static readonly Lock BootGate = new();
    static bool s_booted;

    /// <summary>Statics and signals, once. NO login and no socket: <c>App.cs</c> calls this during composition, and a
    /// session only starts when <see cref="Login"/> is called (or <c>--fake</c> never calls either).</summary>
    public static void Boot()
    {
        lock (BootGate)
        {
            if (s_booted) return;
            s_booted = true;
            string device = Platform.DeviceId;
            string language = Platform.Locale.SpotifyLanguage;
            var s = new Session
            {
                DeviceId = Text.Add(device),
                ClientId = Text.Add(Identity.ClientId),
                Locale = Text.Add(language),
            };
            Volatile.Write(ref s_box, new Box(s));
            for (int i = 0; i < s_keys.Length; i++) s_keys[i] = new KeySlot();
            Log.Info("spotify", "session boot (device " + (device.Length >= 8 ? device[..8] : "none")
                + "…, language " + language + ")");
        }
    }

    /// <summary>Start a session with the stored credential. Returns immediately (C9): the work happens on the AP
    /// thread, and the caller watches <see cref="Status"/>.
    /// <para>With nothing to resume the fold answers <see cref="SessionFault.NoCredential"/> AND an interactive sign-in is
    /// requested (<see cref="SignIn.Requests"/>): every "Sign in" affordance in the app calls this one method, and the
    /// GUI's sign-in door (<c>Setup.SignInDoor</c>) opens the surface on the request. A headless host mounts no door and
    /// reads the fault instead.</para></summary>
    public static void Login()
    {
        Boot();
        bool resumable = !LoadCredential().IsEmpty;
        Publish(new SessionEvent(SessionEventKind.Login, Flag: resumable));
        if (!resumable) Post(static () => SignIn.Requests.Value = SignIn.Requests.Peek() + 1);
    }

    /// <summary>UI THREAD: log in with a token an interactive flow just obtained (§13). A session still running (a
    /// refused account, a reconnect in progress) is closed first, WITHOUT touching the slot; the AP thread then presents
    /// the token, and the welcome's reusable blob replaces it in the slot.</summary>
    static void LoginWithToken(string accessToken)
    {
        Boot();
        lock (InteractiveGate) s_interactive = new Credential(CredentialKind.OAuthToken, "", accessToken);
        if (Current.Phase is not (SessionPhase.Offline or SessionPhase.Failed)) Apply(new SessionEvent(SessionEventKind.Disconnect));
        Apply(new SessionEvent(SessionEventKind.Login, Flag: true));
    }

    /// <summary>`--fake` (G-065): present the chrome as signed in with the seeded profile — no socket, no credential
    /// slot, no login5, and <see cref="SignIn.Requests"/> is never bumped, so the sign-in door never learns there was
    /// "nothing to resume" (<c>Screens/Setup.UI.cs</c>'s door also short-circuits on <c>Platform.Args.Fake</c> directly,
    /// belt and braces). Called once, from <c>App.cs</c>, in <see cref="Login"/>'s place — the exact call site is in
    /// this batch's report, since <c>App.cs</c> is not this batch's file. The identity is <see cref="Entities.FakeAccount"/>,
    /// the same literal <c>Platform.Scope</c>'s fake arm already put in <c>CatalogScope.Account</c>, so nothing here
    /// switches the catalog scope — the seed is already the account's whole "backend".</summary>
    public static void BootFake()
    {
        Boot();
        Post(() =>
        {
            var e = new SessionEvent(SessionEventKind.FakeOnline,
                Id: Entities.Intern(Encoding.UTF8.GetBytes(Entities.FakeAccount)),
                Id2: Entities.Intern(Encoding.UTF8.GetBytes("US")),
                Id3: Entities.Intern(Encoding.UTF8.GetBytes("premium")));
            Apply(e);
        });
        Log.Info("spotify", "fake session online (" + Entities.FakeAccount + ")");
    }

    /// <summary>Sign out: tear every socket down, forget the tokens, wipe the stored credential.</summary>
    public static void Logout() => Connect.RetireThen(static () => Publish(new SessionEvent(SessionEventKind.Logout)));   // G-036: BecameInactive leaves before the session clears

    /// <summary>Close the session WITHOUT signing out: every socket down and the tokens forgotten, the stored credential
    /// kept, so the next <see cref="Login"/> resumes. What a shutdown or a headless run's exit calls. Returns immediately.</summary>
    public static void Disconnect() => Publish(new SessionEvent(SessionEventKind.Disconnect));

    // ── 6. epochs and threads ────────────────────────────────────────────────────────────────────────────────────────

    static readonly Lock ThreadGate = new();

    // THREE sources, one per lifetime (B4). The AP thread watches `s_apCts`; the dealer thread and its keepalive watch
    // `s_dealerCts`; the token mints on api threads (and the dealer's own `AccessToken` call) watch `s_sessionCts` — a
    // bearer belongs to the LOGIN, so neither transport's reset may cancel a re-mint in flight: on the old single source
    // an AP reset cancelled a connecting dealer's bearer mint along with everything else.
    static CancellationTokenSource s_sessionCts = new(), s_apCts = new(), s_dealerCts = new();
    static int s_apRunning, s_dealerRunning, s_keepaliveRunning;
    /// <summary>A start that arrived while that transport's previous thread was still unwinding (a re-login right after a
    /// close, a retry racing a slow teardown): the unwinding thread starts it from its <c>finally</c>, so the request is
    /// deferred rather than lost. 0 = none (an epoch is ≥ 1 once any login ran).</summary>
    static uint s_apRestartEpoch, s_dealerRestartEpoch;
    static Timer? s_apRetryTimer, s_dealerRetryTimer;

    /// <summary>The token the LOGIN's work watches — the bearer and attestation mints (<see cref="AccessToken"/>,
    /// <see cref="ClientToken"/>). Replaced whole by <see cref="CloseLogin"/>, never by a transport's drop.</summary>
    static CancellationToken SessionToken => Volatile.Read(ref s_sessionCts).Token;

    /// <summary>What every thread exit that its own epoch's abandonment caused logs as its reason.</summary>
    const string ExitEpochCancelled = "epoch-cancelled";

    /// <summary>Swap <paramref name="slot"/> for a fresh source and cancel the old one. The old one is NOT disposed: the
    /// threads of that epoch still hold its token, and <c>ct.WaitHandle</c> — which the keepalive tick waits on — throws
    /// once the source is disposed. One dead source per epoch is cheaper than that race, and an epoch is a connection,
    /// not a frame.</summary>
    static void Abandon(ref CancellationTokenSource slot)
    {
        CancellationTokenSource old;
        lock (ThreadGate)
        {
            old = slot;
            Volatile.Write(ref slot, new CancellationTokenSource());
        }
        try { old.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>UI THREAD (<see cref="SessionEffects.CloseAp"/>): abandon the AP epoch (C4) — cancel its token, close its
    /// socket (which unblocks the pump's read), and fail every pending audio-key waiter so its caller can retry on the
    /// next channel. The dealer, its socket and the Connect mailbox are not touched (B4).</summary>
    static void CloseAp()
    {
        Abandon(ref s_apCts);
        CloseApSocket();
        FailAllKeys();
    }

    /// <summary>UI THREAD (<see cref="SessionEffects.CloseDealer"/>): abandon the dealer epoch (C4) — cancel its token
    /// (the dealer thread and its keepalive), close its socket (which unblocks the receive). The caller empties the
    /// Connect mailbox. The AP channel is not touched (B4).</summary>
    static void CloseDealer()
    {
        Abandon(ref s_dealerCts);
        CloseDealerSocket();
    }

    /// <summary>UI THREAD (both close bits in ONE fold — the login ended: a sign-out, a disconnect, a refusal, a login over a
    /// running one): the login's own token goes too, so a bearer or attestation mint in flight on an api thread stops
    /// rather than landing on a login that is gone. A transport's drop never reaches here.</summary>
    static void CloseLogin() => Abandon(ref s_sessionCts);

    /// <summary>Start the AP thread for this AP epoch, once. The guard is a flag the thread itself clears in its
    /// <c>finally</c> rather than <c>Thread.IsAlive</c>, because a retry can arrive while the previous thread is
    /// still unwinding and "alive" would refuse exactly the restart the session is waiting for.</summary>
    static void StartAp(uint epoch)
    {
        lock (ThreadGate)
        {
            if (Interlocked.CompareExchange(ref s_apRunning, 1, 0) != 0) { s_apRestartEpoch = epoch; return; }
            s_apRestartEpoch = 0;
            var ct = s_apCts.Token;
            new Thread(() => ApLoop(epoch, ct)) { IsBackground = true, Name = "wavee-spotify-ap" }.Start();
        }
    }

    /// <summary>Start the dealer thread for this dealer epoch, once — deferred exactly like <see cref="StartAp"/> when the
    /// previous one is still unwinding (it used to be DROPPED, which would now leave <see cref="Session.Dealer"/> Opening
    /// with no thread behind it and the session never Online).</summary>
    static void StartDealer(uint epoch)
    {
        lock (ThreadGate)
        {
            if (Interlocked.CompareExchange(ref s_dealerRunning, 1, 0) != 0) { s_dealerRestartEpoch = epoch; return; }
            s_dealerRestartEpoch = 0;
            var ct = s_dealerCts.Token;
            new Thread(() => DealerLoop(epoch, ct)) { IsBackground = true, Name = "wavee-spotify-dealer" }.Start();
        }
    }

    /// <summary>UI THREAD (<see cref="SessionEffects.ApBackoff"/>): arm the AP's one-shot reconnect, stamped with the AP
    /// epoch it was armed for, so a retry that outlives that epoch (a sign-out, a fresh login) folds to nothing. The
    /// ladder is <see cref="BackoffMs"/> — pure, and therefore tested; this only holds the timer (P10: a named one-shot,
    /// never a polling loop). The line names what the drop left standing: <c>session Online, dealer Up</c> is B4 working.</summary>
    static void ArmApRetry(in Session s)
    {
        int delayMs = BackoffMs(s.ApAttempt);
        s_apRetryTimer?.Dispose();
        s_apRetryTimer = new Timer(static state => Publish(new SessionEvent(SessionEventKind.ApRetry, Epoch: (uint)state!)),
            s.ApEpoch, delayMs, Timeout.Infinite);
        Log.Info("spotify", "ap reconnecting in " + (delayMs / 1000) + "s (attempt " + s.ApAttempt + ", epoch " + s.ApEpoch
            + ", session " + s.Phase + ", dealer " + s.Dealer + ")");
    }

    /// <summary>UI THREAD (<see cref="SessionEffects.DealerBackoff"/>): the dealer's own one-shot reconnect, on its own
    /// ladder, stamped with the dealer epoch it was armed for.</summary>
    static void ArmDealerRetry(in Session s)
    {
        int delayMs = BackoffMs(s.DealerAttempt);
        s_dealerRetryTimer?.Dispose();
        s_dealerRetryTimer = new Timer(static state => Publish(new SessionEvent(SessionEventKind.DealerRetry, Epoch: (uint)state!)),
            s.Epoch, delayMs, Timeout.Infinite);
        Log.Info("spotify", "dealer reconnecting in " + (delayMs / 1000) + "s (attempt " + s.DealerAttempt + ", epoch " + s.Epoch
            + ", ap " + s.Ap + ")");
    }

    // ── 7. HTTP (the three mints and the clock probe) ────────────────────────────────────────────────────────────────

    static readonly HttpClient Http = new(Wire.Handler("session", new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        AutomaticDecompression = System.Net.DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(15),
    }))
    { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>A synchronous protobuf POST. Shell threads only — it blocks (C9).</summary>
    static byte[] PostProto(string url, byte[] body, string? clientToken, string? userAgent, CancellationToken ct)
    {
        using var msg = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(body) };
        msg.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
        msg.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/x-protobuf"));
        if (!string.IsNullOrEmpty(clientToken)) msg.Headers.TryAddWithoutValidation("client-token", clientToken);
        if (!string.IsNullOrEmpty(userAgent)) msg.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        using var resp = Http.Send(msg, ct);
        resp.EnsureSuccessStatusCode();
        using var stream = resp.Content.ReadAsStream(ct);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    static byte[] Get(string url, CancellationToken ct) => Get(url, ct, authed: false);

    /// <summary><paramref name="authed"/> adds the session's bearer and client-token — the pair every spclient route
    /// needs. The server-clock probe went out bare and answered 401 three times per session and per reconnect
    /// (2026-09-16 logs), so the clock offset never synced past the passive bootstrap.</summary>
    static byte[] Get(string url, CancellationToken ct, bool authed)
    {
        using var msg = new HttpRequestMessage(HttpMethod.Get, url);
        msg.Headers.TryAddWithoutValidation("User-Agent", Identity.UserAgent);
        if (authed)
        {
            Session cur = Current;
            string bearer = TextOf(cur.AccessToken), clientToken = TextOf(cur.ClientToken);
            if (bearer.Length > 0) msg.Headers.TryAddWithoutValidation("Authorization", "Bearer " + bearer);
            if (clientToken.Length > 0) msg.Headers.TryAddWithoutValidation("client-token", clientToken);
        }
        using var resp = Http.Send(msg, ct);
        resp.EnsureSuccessStatusCode();
        using var stream = resp.Content.ReadAsStream(ct);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>The AP socket's read timeout — since B6 only the BACKSTOP behind the keepalive (<see cref="ApKeepAlive"/>,
    /// run by <see cref="ApPulse"/>), which calls a silent channel dead at most <see cref="ApKeepAlive.PingTimeoutMs"/>
    /// (80 s) into any one read: the longest of its windows (a held pong plus its ack window is the same 80 s). Twice that,
    /// so it never fires while the keepalive works, and it still bounds a handshake that stalls before the keepalive
    /// exists. History: the first cut used 90 s ("longer than the AP's ping interval" — it was not) and dropped an idle
    /// channel ~90 s after its last packet, every time — 199 and 188 flaps in two logs of 2026-09-16, each a re-login, a
    /// dealer reconnect and a <c>put-state NewDevice</c>; it then became two missed pings plus a minute (300 s), the only
    /// watchdog until B6.</summary>
    public const int ApReadTimeoutMs = 2 * ApKeepAlive.PingTimeoutMs;

    // ── 8. the AP thread ─────────────────────────────────────────────────────────────────────────────────────────────
    //
    // ONE socket serves login AND audio keys: the handshake's socket and its negotiated codec are kept and become the
    // persistent channel (0.2.9 opened a second one; the AP tracks connections per account and a second handshake is
    // both slower and noisier). The login reads the welcome and the two post-login trailers (0x1b country, 0x50 product);
    // the pump routes 0x0d/0x0e to the key table and hands the AP's pings and pong-acks to the keepalive (B6), which holds
    // each pong 60 s the way librespot does rather than answering it on the spot.

    sealed class ApLogin
    {
        public string Username = "";
        public string Country = "";
        public string Product = "";
        public Tier Tier;
        public byte[] Reusable = [];
        /// <summary>The AP's first Ping arrived inside the login's read loop (it rides right behind the welcome), at
        /// <see cref="PingAtMs"/> (<c>Environment.TickCount64</c>). Not answered there: the pump's keepalive holds its pong
        /// from that instant (B6).</summary>
        public bool Pinged;
        public long PingAtMs;
    }

    /// <summary>One AP AuthFailure (cmd 0xAD), with its <c>keyexchange.proto</c> error code (<c>-1</c> = unreadable). Not yet
    /// a verdict: <see cref="ConnectAndLogin"/> runs it through the D24 ladder (<see cref="OnApReject"/>). A transport
    /// failure is a plain exception and merely fails over to the next access point.</summary>
    sealed class ApRejectedException(int code, string message) : Exception(message)
    {
        public int Code { get; } = code;
    }

    /// <summary>The ladder's terminal answer: the login stops with this verdict, which decides whether the credential survives.</summary>
    sealed class ApRefusedException(RejectVerdict verdict, string message) : Exception(message)
    {
        public RejectVerdict Verdict { get; } = verdict;
    }

    /// <summary>The AP asked us to connect elsewhere (login_failed = TryAnotherAP): retry the NEXT access point.</summary>
    sealed class ApTryAnotherException(string message) : Exception(message);

    /// <summary>The keepalive called the channel dead (B6) — OUR verdict, not the server's reset. Thrown by
    /// <see cref="PumpAp"/> (the fold's timer cannot throw into the pump's read, so it closes the socket and the pump
    /// rethrows this) and mapped by <see cref="ApLoop"/> to <c>ap.exit reason=keepalive-timeout</c> and an epoch-stamped
    /// <c>ApDropped</c>, which reconnects the AP alone (B4).</summary>
    sealed class ApKeepAliveException(ApKeepAlive.DeadReason reason, Exception? inner)
        : Exception("the AP keepalive expired (" + ApKeepAlive.Word(reason) + ")", inner)
    {
        public ApKeepAlive.DeadReason Reason { get; } = reason;
    }

    static TcpClient? s_apSocket;
    static NetworkStream? s_apStream;
    static ApCodec s_apCodec;
    static readonly Lock ApSendGate = new();
    static ApLogin? s_login;

    static void CloseApSocket()
    {
        try { s_apStream?.Dispose(); } catch (IOException) { }
        try { s_apSocket?.Dispose(); } catch (SocketException) { }
        s_apStream = null;
        s_apSocket = null;
    }

    /// <summary>ONE attempt, start to finish, for ONE AP epoch. It never loops: a failure is published as
    /// <c>ApDropped</c> stamped with this epoch, the fold answers <c>CloseAp | ApBackoff</c>, and the AP's own retry timer
    /// starts a fresh thread — the dealer never hears of it (B4). That keeps the ladder in one place (a pure function plus
    /// a timer) instead of two.
    /// <para>Every exit is ONE always-on line, <c>ap.exit reason= epoch=</c>: <c>epoch-cancelled</c> (the epoch was
    /// abandoned under the thread — its own drop's fold, a sign-out, a refusal; whatever the socket threw on the way down is
    /// the teardown's echo, not a failure), <c>dropped</c> (the channel failed under us — a server reset, a read error),
    /// <c>keepalive-timeout</c> (our own keepalive found it silent, B6), <c>refused</c>, <c>no-credential</c>. The old
    /// <c>catch (OperationCanceledException) {}</c> / <c>catch (ObjectDisposedException) {}</c> left without a word — and
    /// also swallowed an OperationCanceledException that was NOT the epoch's (an HttpClient timeout in apresolve or login5
    /// is a TaskCanceledException), leaving a dead AP thread and no reconnect. Now only this epoch's own cancellation is
    /// quiet; anything else is a drop.</para></summary>
    static void ApLoop(uint epoch, CancellationToken ct)
    {
        string exit = "faulted";
        try
        {
            Credential cred = LoadCredential();
            if (cred.IsEmpty)
            {
                exit = "no-credential";
                Publish(new SessionEvent(SessionEventKind.Login, Flag: false));
                return;
            }

            ResolveAndPublishHosts(epoch, ct, out var accessPoints);
            ApLogin login = ConnectAndLogin(cred, accessPoints, epoch, ct);
            MintTokens(ct);
            PumpAp(epoch, login, ct);                                // returns only when this epoch is cancelled
            ct.ThrowIfCancellationRequested();
            throw new IOException("the AP channel closed");
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            exit = ExitEpochCancelled;
        }
        catch (ApRefusedException ex)
        {
            Log.Warn("spotify", "the AP refused the login (" + ex.Verdict + "): " + ex.Message
                + (ex.Verdict == RejectVerdict.Transient ? " — the credential is kept" : ""));
            exit = "refused";
            Publish(new SessionEvent(SessionEventKind.AuthRejected, Number: (long)ex.Verdict, Epoch: epoch));
        }
        catch (ApKeepAliveException)
        {
            // B6: our own verdict (the pulse already said which window lapsed, `ap.keepalive dead`). The recovery is a
            // reset's — the AP reconnects alone — but the exit names it, so the log keeps the server's resets (`dropped`,
            // `ap channel failed`) and our timeouts apart.
            exit = "keepalive-timeout";
            Publish(new SessionEvent(SessionEventKind.ApDropped, Number: (long)SessionFault.Network, Epoch: epoch));
        }
        catch (Exception ex)
        {
            // A cancellation that lands between the filter above and this post leaves an ApDropped stamped with an epoch
            // the fold has already moved past: `IsStale` refuses it, which is exactly what the stamp is for.
            Log.Warn("spotify", "ap channel failed (epoch " + epoch + ")", ex);
            exit = "dropped";
            Publish(new SessionEvent(SessionEventKind.ApDropped, Number: (long)SessionFault.Network, Epoch: epoch));
        }
        finally
        {
            CloseApSocket();
            uint restart;
            lock (ThreadGate)
            {
                Volatile.Write(ref s_apRunning, 0);
                restart = s_apRestartEpoch;
                s_apRestartEpoch = 0;
            }
            Log.Info("spotify", "ap.exit reason=" + exit + " epoch=" + epoch);
            if (restart != 0 && restart != epoch && restart == Current.ApEpoch) StartAp(restart);
        }
    }

    /// <summary>apresolve, once per attempt: the access points to try (":4070 first", then the rest — the failover
    /// order 0.2.9 settled on), plus the spclient and dealer hosts the session carries. Published under this AP
    /// <paramref name="epoch"/>.</summary>
    static void ResolveAndPublishHosts(uint epoch, CancellationToken ct, out List<(string Host, int Port)> accessPoints)
    {
        byte[] json = Get("https://apresolve.spotify.com/?type=accesspoint&type=spclient&type=dealer-g2", ct);
        accessPoints = [];
        var rest = new List<(string, int)>();
        Span<Range> ranges = stackalloc Range[32];

        int n = ParseHosts(json, "accesspoint"u8, ranges);
        for (int i = 0; i < n; i++)
        {
            var entry = json.AsSpan(ranges[i]);
            var (hostLength, port) = SplitHostPort(entry, 4070);
            var pair = (Encoding.UTF8.GetString(entry[..hostLength]), port);
            if (port == 4070) accessPoints.Add(pair); else rest.Add(pair);
        }
        accessPoints.AddRange(rest);
        if (accessPoints.Count == 0) throw new IOException("apresolve returned no access points");

        n = ParseHosts(json, "spclient"u8, ranges);
        TokenRef spclient = n > 0 ? Text.Add(FirstHost(json, ranges[0])) : default;
        n = ParseHosts(json, "dealer-g2"u8, ranges);
        TokenRef dealer = n > 0 ? Text.Add(FirstHost(json, ranges[0])) : Text.Add("dealer.spotify.com");
        Publish(new SessionEvent(SessionEventKind.Hosts, Text: spclient, Text2: dealer, Epoch: epoch));

        static string FirstHost(byte[] json, Range r)
        {
            var entry = json.AsSpan(r);
            var (hostLength, _) = SplitHostPort(entry, 443);
            return Encoding.UTF8.GetString(entry[..hostLength]);
        }
    }

    /// <summary>Walk the access points until one logs in. A refusal goes through the D24 ladder (<see cref="OnApReject"/>):
    /// the FIRST bad-credentials answer buys one more attempt against a fresh access point (the next one in the list,
    /// wrapping), the second is the definitive verdict; "try another AP" is plain failover; anything else stops with the
    /// credential kept. A retry that then fails on transport is a network failure, never a verdict. Every step is published
    /// under this AP <paramref name="epoch"/>. Returns the login the channel now carries (the pump's keepalive reads its
    /// first Ping from it).</summary>
    static ApLogin ConnectAndLogin(in Credential cred, List<(string Host, int Port)> accessPoints, uint epoch, CancellationToken ct)
    {
        Exception? last = null;
        int badCredentials = 0;
        int budget = accessPoints.Count;
        for (int attempt = 0; attempt < budget; attempt++)
        {
            var (host, port) = accessPoints[attempt % accessPoints.Count];
            ct.ThrowIfCancellationRequested();
            TcpClient? tcp = null;
            try
            {
                tcp = new TcpClient { NoDelay = true };
                tcp.Connect(host, port);
                var stream = tcp.GetStream();
                stream.ReadTimeout = ApReadTimeoutMs;                 // the backstop behind the keepalive (see the constant)
                Publish(new SessionEvent(SessionEventKind.Connected, Epoch: epoch));

                var codec = Negotiate(stream, ct);
                Publish(new SessionEvent(SessionEventKind.HandshakeOk, Epoch: epoch));

                var login = Authenticate(stream, ref codec, cred, ct);
                s_apSocket = tcp;
                s_apStream = stream;
                s_apCodec = codec;
                s_login = login;
                tcp = null;                                           // the session owns it now
                PublishWelcome(login, epoch);
                PublishPreferredLocale();
                return login;
            }
            catch (ApRejectedException ex)
            {
                tcp?.Dispose();
                switch (OnApReject(ex.Code, badCredentials, out RejectVerdict verdict))
                {
                    case RejectStep.RetryFreshAccessPoint:
                        badCredentials++;
                        budget = Math.Max(budget, attempt + 2);   // exactly one more attempt, even on a one-AP list
                        Log.Warn("spotify", "access point " + host + " refused the credential (" + ex.Message
                            + ") — one retry against a fresh access point before believing it");
                        last = new IOException("the retry against a fresh access point did not complete");
                        continue;
                    case RejectStep.NextAccessPoint:
                        last = ex;
                        Log.Info("spotify", "access point " + host + " asked for another — trying the next");
                        continue;
                    default:
                        throw new ApRefusedException(verdict, ex.Message);
                }
            }
            catch (OperationCanceledException) { tcp?.Dispose(); throw; }
            catch (Exception ex)
            {
                last = ex;
                Log.Info("spotify", "access point " + host + " failed (" + ex.Message + ") — trying the next");
                tcp?.Dispose();
            }
        }
        throw last ?? new IOException("all access points failed");
    }

    /// <summary>The DH exchange and the key derivation: ClientHello → APResponse (RSA-verified) → ClientResponse. The
    /// crypto is <c>Spotify.cs</c>'s; this is the socket half.</summary>
    static ApCodec Negotiate(NetworkStream stream, CancellationToken ct)
    {
        Span<byte> priv = stackalloc byte[Handshake.PrivateKeySize];
        RandomNumberGenerator.Fill(priv);
        Span<byte> pub = stackalloc byte[Handshake.ModulusSize];
        Handshake.PublicKey(priv, pub);

        Span<byte> nonce = stackalloc byte[16];
        RandomNumberGenerator.Fill(nonce);
        var hello = new Wavee.Protocol.ClientHello
        {
            BuildInfo = new Wavee.Protocol.BuildInfo
            {
                Product = Wavee.Protocol.Product.Client,
                Platform = Wavee.Protocol.Platform.Win32X86,
                Version = 124200447,
            },
            CryptosuitesSupported = { Wavee.Protocol.Cryptosuite.Shannon },
            LoginCryptoHello = new Wavee.Protocol.LoginCryptoHelloUnion
            {
                DiffieHellman = new Wavee.Protocol.LoginCryptoDiffieHellmanHello
                {
                    Gc = ByteString.CopyFrom(pub),
                    ServerKeysKnown = 1,
                },
            },
            ClientNonce = ByteString.CopyFrom(nonce),
            Padding = ByteString.CopyFrom(new byte[] { 0x1e }),
        };

        byte[] helloProto = hello.ToByteArray();
        var accumulator = new MemoryStream(helloProto.Length + 1024);
        byte[] frame = new byte[6 + helloProto.Length];
        Handshake.WriteHelloFrame(helloProto, frame);
        accumulator.Write(frame, 0, frame.Length);
        stream.Write(frame, 0, frame.Length);

        Span<byte> size4 = stackalloc byte[4];
        stream.ReadExactly(size4);
        accumulator.Write(size4);
        int bodyLength = Handshake.ApResponseBodyLength(size4);
        if (bodyLength <= 0) throw new InvalidDataException("APResponse frame size out of range");
        byte[] body = new byte[bodyLength];
        stream.ReadExactly(body);
        accumulator.Write(body, 0, body.Length);
        ct.ThrowIfCancellationRequested();

        var response = Wavee.Protocol.APResponseMessage.Parser.ParseFrom(body);
        if (response.Challenge is null)
        {
            if (response.LoginFailed?.ErrorCode == Wavee.Protocol.ErrorCode.TryAnotherAp)
                throw new ApTryAnotherException("the AP asked for another access point");
            throw new InvalidDataException("the AP sent no challenge");
        }

        var dh = response.Challenge.LoginCryptoChallenge.DiffieHellman;
        byte[] gs = dh.Gs.ToByteArray();
        // SECURITY: raw TCP — verify the server's signature over gs BEFORE deriving keys, or an active MITM
        // negotiates the channel and reads the login packet (which carries the credential).
        if (!Handshake.VerifyGs(gs, dh.GsSignature.Span))
            throw new InvalidDataException("AP server signature verification failed — aborting handshake");

        Span<byte> secret = stackalloc byte[Handshake.ModulusSize];
        Handshake.SharedSecret(priv, gs, secret);
        Span<byte> challenge = stackalloc byte[20];
        Span<byte> sendKey = stackalloc byte[32];
        Span<byte> receiveKey = stackalloc byte[32];
        Handshake.DeriveKeys(secret, accumulator.GetBuffer().AsSpan(0, (int)accumulator.Length), challenge, sendKey, receiveKey);

        var plain = new Wavee.Protocol.ClientResponsePlaintext
        {
            LoginCryptoResponse = new Wavee.Protocol.LoginCryptoResponseUnion
            {
                DiffieHellman = new Wavee.Protocol.LoginCryptoDiffieHellmanResponse { Hmac = ByteString.CopyFrom(challenge) },
            },
            PowResponse = new Wavee.Protocol.PoWResponseUnion(),
            CryptoResponse = new Wavee.Protocol.CryptoResponseUnion(),
        };
        byte[] plainProto = plain.ToByteArray();
        byte[] plainFrame = new byte[4 + plainProto.Length];
        Handshake.WriteResponseFrame(plainProto, plainFrame);
        stream.Write(plainFrame, 0, plainFrame.Length);

        return new ApCodec(sendKey, receiveKey);
    }

    /// <summary>Present the credential over the Shannon channel and read the welcome plus the two trailers the AP
    /// pushes on success. Bounded: a missing trailer must not hang the login, so the product/country wait is short and
    /// a missing product reads as <see cref="Tier.Unknown"/> — on a first login treated as Free, never optimistically as
    /// Premium; on an AP reconnect the fold keeps the tier the login's first welcome settled. The AP's first Ping, which
    /// rides behind the welcome, is only NOTED here (<see cref="ApLogin.Pinged"/>): its pong is the keepalive's (B6).</summary>
    static ApLogin Authenticate(NetworkStream stream, ref ApCodec codec, in Credential cred, CancellationToken ct)
    {
        var credentials = new Wavee.Protocol.LoginCredentials
        {
            Typ = cred.Kind == CredentialKind.ReusableBlob
                ? Wavee.Protocol.AuthenticationType.AuthenticationStoredSpotifyCredentials
                : Wavee.Protocol.AuthenticationType.AuthenticationSpotifyToken,
            AuthData = ByteString.CopyFrom(cred.Kind == CredentialKind.ReusableBlob
                ? Convert.FromBase64String(cred.Secret)
                : Encoding.UTF8.GetBytes(cred.Secret)),
        };
        if (cred.Kind == CredentialKind.ReusableBlob && !string.IsNullOrEmpty(cred.Username)) credentials.Username = cred.Username;

        var login = new Wavee.Protocol.ClientResponseEncrypted
        {
            LoginCredentials = credentials,
            SystemInfo = new Wavee.Protocol.SystemInfo
            {
                CpuFamily = Wavee.Protocol.CpuFamily.CpuX8664,
                Os = Wavee.Protocol.Os.Windows,
                SystemInformationString = "wavee-fluentgpu",
                DeviceId = Platform.DeviceId,
            },
        };

        byte[] proto = login.ToByteArray();
        byte[] frame = new byte[ApCodec.FrameLength(proto.Length)];
        codec.Encode(Handshake.CmdLogin, proto, frame);
        stream.Write(frame, 0, frame.Length);

        var result = new ApLogin();
        byte[] buffer = new byte[64 * 1024];
        bool welcome = false;
        int trailers = 0;
        int previousTimeout = stream.ReadTimeout;
        stream.ReadTimeout = 15_000;
        try
        {
            while (!welcome || trailers < 2)
            {
                ct.ThrowIfCancellationRequested();
                byte cmd = ReadPacket(stream, ref codec, ref buffer, out int payloadLength);
                var payload = buffer.AsSpan(0, payloadLength);
                switch (cmd)
                {
                    case Handshake.CmdApWelcome:
                        var w = Wavee.Protocol.APWelcome.Parser.ParseFrom(payload);
                        result.Username = w.CanonicalUsername;
                        result.Reusable = w.ReusableAuthCredentials.ToByteArray();
                        welcome = true;
                        stream.ReadTimeout = 3_000;                 // a brief window for the trailers, then proceed
                        break;
                    case Handshake.CmdAuthFailure:
                        string detail;
                        int code = -1;
                        try
                        {
                            var f = Wavee.Protocol.APLoginFailed.Parser.ParseFrom(payload);
                            code = (int)f.ErrorCode;
                            detail = f.ErrorCode.ToString();
                        }
                        catch (InvalidProtocolBufferException) { detail = "unreadable failure, " + payload.Length + " bytes"; }
                        throw new ApRejectedException(code, detail);
                    case Handshake.CmdCountryCode:
                        result.Country = Encoding.UTF8.GetString(payload);
                        trailers++;
                        break;
                    case Handshake.CmdProductInfo:
                        result.Tier = ProductXml.TierOf(payload);
                        var type = ProductXml.Value(payload, "type"u8);
                        result.Product = type.IsEmpty ? "unknown" : Encoding.UTF8.GetString(type);
                        trailers++;
                        break;
                    case Handshake.CmdPing:
                        // B6: held, not answered — the pump's keepalive sends this ping's pong 60 s from NOW.
                        result.Pinged = true;
                        result.PingAtMs = Environment.TickCount64;
                        break;
                }
            }
        }
        catch (IOException) when (welcome)
        {
            // The trailer window elapsed. Proceed with what arrived (an absent 0x50 stays Tier.Unknown).
        }
        finally { stream.ReadTimeout = previousTimeout; }

        if (!welcome) throw new InvalidDataException("no APWelcome and no AuthFailure after login");
        return result;
    }

    /// <summary>Read one AP packet into <paramref name="buffer"/> (grown if the payload needs it) and return the
    /// command; the plaintext is <c>buffer[..length]</c> (a tuple cannot carry a span). A failed MAC is fatal: a Shannon
    /// channel cannot resynchronise.</summary>
    static byte ReadPacket(NetworkStream stream, ref ApCodec codec, ref byte[] buffer, out int length)
    {
        Span<byte> header = stackalloc byte[3];
        stream.ReadExactly(header);
        (byte cmd, length) = codec.BeginDecode(header);
        if (buffer.Length < length + 4) buffer = new byte[Math.Max(length + 4, buffer.Length * 2)];
        stream.ReadExactly(buffer.AsSpan(0, length + 4));
        if (!codec.EndDecode(buffer.AsSpan(0, length), buffer.AsSpan(length, 4)))
            throw new InvalidDataException("AP packet MAC check failed");
        return cmd;
    }

    /// <summary>Send one AP packet. Serialised: the pump thread and an audio-key caller share the socket, and Shannon
    /// counts packets — two encodes racing would desync the channel.</summary>
    static void SendAp(NetworkStream stream, ref ApCodec codec, byte cmd, ReadOnlySpan<byte> payload)
    {
        int n = ApCodec.FrameLength(payload.Length);
        byte[]? heap = n > 1024 ? new byte[n] : null;                 // a packet this client sends is ~50 bytes
        Span<byte> frame = heap is not null ? heap : stackalloc byte[n];
        lock (ApSendGate)
        {
            codec.Encode(cmd, payload, frame);
            stream.Write(frame[..n]);
        }
        Wire.NoteSocket("ap", "cmd=0x" + cmd.ToString("x2"), n);
    }

    /// <summary>Send on the SESSION's channel (the audio-key path and the locale publish). False when there is none.</summary>
    static bool SendOnChannel(byte cmd, ReadOnlySpan<byte> payload)
    {
        var stream = s_apStream;
        if (stream is null) return false;
        try { SendAp(stream, ref s_apCodec, cmd, payload); return true; }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException) { return false; }
    }

    /// <summary>Spotify wants the 0x0f random preamble immediately before the 0x74 key/value packet.</summary>
    static void PublishPreferredLocale()
    {
        Span<byte> preamble = stackalloc byte[20];
        RandomNumberGenerator.Fill(preamble);
        if (!SendOnChannel(Handshake.CmdLocalePreamble, preamble)) return;
        string spotifyLanguage = Platform.Locale.SpotifyLanguage;
        ReadOnlySpan<char> language = spotifyLanguage.AsSpan(0, Math.Min(2, spotifyLanguage.Length));
        Span<byte> ascii = stackalloc byte[2];
        for (int i = 0; i < language.Length; i++) ascii[i] = (byte)language[i];
        Span<byte> body = stackalloc byte[32];
        int n = Handshake.WritePreferredLocale(ascii[..language.Length], body);
        SendOnChannel(Handshake.CmdPreferredLocale, body[..n]);
    }

    /// <summary>The persistent channel, for ONE AP <paramref name="epoch"/>: route key replies, hand the AP's pings and
    /// pong-acks to the keepalive (<see cref="ApPulse"/>, B6), ignore everything else (mercury and the legacy packets are
    /// not used by this client). Returns only when this AP epoch is cancelled; a channel that drops THROWS — a reset, the
    /// backstop read timeout, a failed MAC, or <see cref="ApKeepAliveException"/> when the keepalive called it dead — which
    /// <see cref="ApLoop"/> publishes as <c>ApDropped</c>: the AP reconnects alone (B4).
    /// <para>The keepalive starts HERE, when something reads the channel — not at the welcome: between the two the
    /// thread mints tokens over HTTPS (seconds) and nothing could see an ack, so a window started at the welcome could
    /// expire on a live channel. The Ping that rode behind the welcome (<see cref="ApLogin.Pinged"/>) is fed at the
    /// instant it arrived, so its pong is still held from the ping.</para></summary>
    static void PumpAp(uint epoch, ApLogin login, CancellationToken ct)
    {
        var stream = s_apStream ?? throw new IOException("no AP channel");
        byte[] buffer = new byte[64 * 1024];
        Span<byte> key = stackalloc byte[AudioKey.KeyLength];
        using var pulse = new ApPulse(stream, s_apSocket, epoch, ct);
        pulse.Feed(ApKeepAlive.Input.Connected, Environment.TickCount64);
        if (login.Pinged) pulse.Feed(ApKeepAlive.Input.PingReceived, login.PingAtMs);
        while (!ct.IsCancellationRequested)
        {
            // A verdict that landed while the last packet was being handled: the socket is already closed.
            ApKeepAlive.DeadReason dead = pulse.Dead;
            if (dead != ApKeepAlive.DeadReason.None) throw new ApKeepAliveException(dead, null);

            byte cmd;
            int payloadLength;
            try { cmd = ReadPacket(stream, ref s_apCodec, ref buffer, out payloadLength); }
            catch (Exception ex) when (pulse.Dead != ApKeepAlive.DeadReason.None)
            {
                // The pulse closed the socket under this read: what the read threw is that close's echo, the verdict is why.
                throw new ApKeepAliveException(pulse.Dead, ex);
            }
            var payload = buffer.AsSpan(0, payloadLength);
            switch (cmd)
            {
                case Handshake.CmdAesKey:
                    if (AudioKey.TryReadKey(payload, out uint okSeq, key)) CompleteKey(okSeq, key, 0);
                    break;
                case Handshake.CmdAesKeyError:
                    if (AudioKey.TryReadError(payload, out uint badSeq, out int code)) CompleteKey(badSeq, default, code == 0 ? -1 : code);
                    break;
                case Handshake.CmdPing:
                    pulse.Feed(ApKeepAlive.Input.PingReceived, Environment.TickCount64);   // held 60 s, never answered here
                    break;
                case Handshake.CmdPongAck:
                    pulse.Feed(ApKeepAlive.Input.PongAckReceived, Environment.TickCount64);
                    break;
            }
        }
    }

    /// <summary>THE AP KEEPALIVE'S SHELL HALF (B6), for ONE AP epoch: the pure <see cref="ApKeepAlive"/> fold, fed by the
    /// pump (a Ping, a PongAck) and by ONE one-shot timer re-armed to the fold's next deadline after every step (P10: a
    /// named timer, never a poll — per two-minute cycle it wakes once, to send the held pong). The tick is a timer and not
    /// the socket's read timeout: a read that times out can land mid-packet, which a Shannon channel cannot survive, and
    /// Winsock calls a socket whose <c>SO_RCVTIMEO</c> fired indeterminate.
    /// <para>What it executes. <c>SendPong</c>: the HELD pong goes out on the stream this epoch's pump reads, under this
    /// epoch's token, both captured at construction — never <see cref="SendOnChannel"/>'s <c>s_apStream</c> read at fire
    /// time, which by then could be the next epoch's channel. The codec is <c>s_apCodec</c>, and it is this epoch's for as
    /// long as the timer can fire: the next epoch's thread cannot install its own until this one has exited, and this one
    /// exits only after <see cref="Dispose"/> returned — which takes the gate, so a callback mid-send finishes first and
    /// every later one finds the pulse closed. <c>Dead</c>: the reason is latched and the socket closed, which unblocks the
    /// pump's read; the pump then throws <see cref="ApKeepAliveException"/> (a timer cannot throw into another thread).</para>
    /// <para>The always-on lines, three per two-minute cycle at Info: <c>ap.keepalive ping</c> (and the server's cadence),
    /// <c>ap.keepalive pong</c> (how long it was held — the <c>wire.send channel=ap cmd=0x49</c> line follows it),
    /// <c>ap.keepalive ack</c> (how fast the server answered); and <c>ap.keepalive dead</c> at Warn. A Ping or ack out of
    /// turn says <c>unexpected</c>, as librespot warns.</para></summary>
    sealed class ApPulse : IDisposable
    {
        readonly Lock _gate = new();
        readonly NetworkStream _stream;
        readonly TcpClient? _socket;
        readonly uint _epoch;
        readonly CancellationToken _ct;
        readonly Timer _timer;
        ApKeepAlive.State _state;
        ApKeepAlive.DeadReason _dead;
        bool _closed, _pinged, _ponged;
        long _pingAtMs, _pongAtMs;

        public ApPulse(NetworkStream stream, TcpClient? socket, uint epoch, CancellationToken ct)
        {
            _stream = stream;
            _socket = socket;
            _epoch = epoch;
            _ct = ct;
            _timer = new Timer(static self => ((ApPulse)self!).Feed(ApKeepAlive.Input.Tick, Environment.TickCount64),
                this, Timeout.Infinite, Timeout.Infinite);
        }

        /// <summary>The fold's verdict, once it reached one (<see cref="ApKeepAlive.DeadReason.None"/> until then).</summary>
        public ApKeepAlive.DeadReason Dead
        {
            get { lock (_gate) return _dead; }
        }

        /// <summary>Fold one input at <paramref name="nowMs"/> (<c>Environment.TickCount64</c>), run its effect, log the
        /// transition, re-arm the timer. The pump thread and the timer both call it; the gate serialises them.</summary>
        public void Feed(ApKeepAlive.Input input, long nowMs)
        {
            lock (_gate)
            {
                if (_closed) return;
                ApKeepAlive.Phase was = _state.Phase;
                var (next, effect) = ApKeepAlive.Step(in _state, input, nowMs);
                _state = next;
                string turn = ApKeepAlive.Expected(was, input) ? "" : " unexpected (was " + was + ")";
                switch (input)
                {
                    case ApKeepAlive.Input.PingReceived when was != ApKeepAlive.Phase.Idle:
                        Log.Info("spotify", "ap.keepalive ping epoch=" + _epoch
                            + (_pinged ? " sinceLastPingMs=" + unchecked(nowMs - _pingAtMs) : " first")
                            + " pongInMs=" + ApKeepAlive.DueInMs(in _state, Environment.TickCount64) + turn);
                        _pinged = true;
                        _pingAtMs = nowMs;
                        break;
                    case ApKeepAlive.Input.PongAckReceived when was != ApKeepAlive.Phase.Idle:
                        Log.Info("spotify", "ap.keepalive ack epoch=" + _epoch
                            + (_ponged ? " afterPongMs=" + unchecked(nowMs - _pongAtMs) : "") + turn);
                        break;
                }
                switch (effect.Kind)
                {
                    case ApKeepAlive.EffectKind.SendPong:
                        if (_ct.IsCancellationRequested) break;               // the epoch is going: its channel owes nothing
                        try { SendAp(_stream, ref s_apCodec, Handshake.CmdPong, [0, 0, 0, 0]); }   // librespot's payload
                        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
                        {
                            // The channel is already going down; the pump's read says so, and the ack window will too.
                        }
                        _ponged = true;
                        _pongAtMs = nowMs;
                        Log.Info("spotify", "ap.keepalive pong epoch=" + _epoch
                            + (_pinged ? " heldMs=" + unchecked(nowMs - _pingAtMs) : "")
                            + " ackWithinMs=" + ApKeepAlive.PongAckTimeoutMs);
                        break;
                    case ApKeepAlive.EffectKind.Dead:
                        _dead = effect.Reason;
                        Log.Warn("spotify", "ap.keepalive dead epoch=" + _epoch + " reason=" + ApKeepAlive.Word(effect.Reason)
                            + " (was " + was + (_pinged ? ", last ping " + unchecked(nowMs - _pingAtMs) + "ms ago" : ", no ping yet")
                            + ") — closing the channel; the AP reconnects alone");
                        Abort();
                        break;
                }
                _timer.Change(ApKeepAlive.DueInMs(in _state, nowMs), Timeout.Infinite);   // -1 while Idle: disarmed
            }
        }

        /// <summary>Close this epoch's socket — the captured one, never whatever <c>s_apStream</c> holds by now — so the
        /// pump's blocked read throws. Idempotent with <see cref="CloseApSocket"/>, which the AP thread runs on its way out.</summary>
        void Abort()
        {
            try { _stream.Dispose(); } catch (IOException) { }
            try { _socket?.Dispose(); } catch (SocketException) { }
        }

        /// <summary>PUMP THREAD, on its way out: no step runs after this returns (see the class).</summary>
        public void Dispose()
        {
            lock (_gate)
            {
                if (_closed) return;
                _closed = true;
            }
            _timer.Dispose();
        }
    }

    /// <summary>The welcome, stamped with the AP <paramref name="epoch"/> that earned it: a welcome from a thread whose
    /// epoch was abandoned while it walked the ladder is refused by the fold rather than signing anything in.</summary>
    static void PublishWelcome(ApLogin login, uint epoch)
    {
        // The interning happens INSIDE the post — i.e. on the UI thread, the only thread that may touch the
        // interner (C1, and `Entities/Store.cs`'s "the store thread NEVER resolves").
        Post(() =>
        {
            var e = new SessionEvent(SessionEventKind.Welcome,
                Id: Entities.Intern(Encoding.UTF8.GetBytes(login.Username)),
                Id2: Entities.Intern(Encoding.UTF8.GetBytes(login.Country)),
                Id3: Entities.Intern(Encoding.UTF8.GetBytes(login.Product)),
                Number: (long)login.Tier, Epoch: epoch);
            Apply(e);
        });
        // An empty product is a 0x50 that missed its window: a first login is refused for it, a reconnect keeps its tier.
        Log.Info("spotify", "logged in (" + Platform.Redact(login.Username) + ", "
            + (login.Product.Length == 0 ? "product late" : login.Product) + ", "
            + login.Country + ", " + login.Reusable.Length + "-byte reusable credential, ap epoch " + epoch + ")");
    }

    /// <summary>UI THREAD (a <see cref="SessionEffects.SaveCredential"/> effect): persist the reusable blob the welcome
    /// carried. The bytes are never logged, only their count.</summary>
    static void SaveWelcomeCredential()
    {
        var login = s_login;
        if (login is null || login.Reusable.Length == 0) return;
        Platform.SaveCredential(new Wavee.Credential(Wavee.CredentialKind.ReusableBlob, login.Username,
            Convert.ToBase64String(login.Reusable), Refresh: null));
        ClearInteractive();   // the blob supersedes an interactive sign-in's token: every later login resumes from the slot
    }

    // ── 9. the tokens (client-token attestation, then login5) ────────────────────────────────────────────────────────

    static readonly Lock TokenGate = new();
    static string? s_clientToken, s_accessToken;
    static long s_clientTokenExpiresAtMs, s_accessExpiresAtMs;

    static long NowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    static void MintTokens(CancellationToken ct)
    {
        string? clientToken = MintClientToken(ct);
        Publish(new SessionEvent(SessionEventKind.ClientTokenMinted,
            Text: Text.Add(clientToken ?? string.Empty), Number: s_clientTokenExpiresAtMs));

        string access = MintAccessToken(force: true, ct);
        Publish(new SessionEvent(SessionEventKind.AccessTokenMinted,
            Text: Text.Add(access), Number: s_accessExpiresAtMs));
    }

    /// <summary>The attestation clienttoken.spotify.com hands out. Best effort: a missing one surfaces downstream as a
    /// 403 the caller can report, which is more useful than failing the whole login here.</summary>
    static string? MintClientToken(CancellationToken ct)
    {
        try
        {
            var request = new Wavee.Protocol.ClientToken.ClientTokenRequest
            {
                RequestType = Wavee.Protocol.ClientToken.ClientTokenRequestType.RequestClientDataRequest,
                ClientData = new Wavee.Protocol.ClientToken.ClientDataRequest
                {
                    ClientVersion = Identity.ClientVersion,
                    ClientId = Identity.ClientId,
                    ConnectivitySdkData = BuildConnectivity(),
                },
            };

            for (int attempt = 0; attempt < 3; attempt++)
            {
                byte[] bytes = PostProto("https://clienttoken.spotify.com/v1/clienttoken",
                    request.ToByteArray(), null, Identity.ClientTokenUserAgent, ct);
                var response = Wavee.Protocol.ClientToken.ClientTokenResponse.Parser.ParseFrom(bytes);
                switch (response.ResponseType)
                {
                    case Wavee.Protocol.ClientToken.ClientTokenResponseType.ResponseGrantedTokenResponse:
                        var granted = response.GrantedToken;
                        lock (TokenGate)
                        {
                            s_clientToken = granted.Token;
                            s_clientTokenExpiresAtMs = NowMs + 1000L * (granted.RefreshAfterSeconds > 0 ? granted.RefreshAfterSeconds : 7200);
                        }
                        Log.Info("spotify", "client-token minted");
                        return granted.Token;
                    case Wavee.Protocol.ClientToken.ClientTokenResponseType.ResponseChallengesResponse:
                        request = SolveClientTokenChallenge(response.Challenges);
                        break;
                    default:
                        return null;
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidProtocolBufferException or IOException)
        {
            Log.Warn("spotify", "client-token failed — continuing without one (spclient may 403)", ex);
        }
        return null;
    }

    static Wavee.Protocol.ClientToken.ConnectivitySdkData BuildConnectivity()
    {
        // device_id here is the local-machine SID prefix on desktop, NOT the Spotify Connect device id.
        var data = new Wavee.Protocol.ClientToken.ConnectivitySdkData
        {
            DeviceId = MachineSid() ?? Platform.DeviceId,
            PlatformSpecificData = new Wavee.Protocol.ClientToken.PlatformSpecificData(),
        };
        if (OperatingSystem.IsWindows())
        {
            data.PlatformSpecificData.DesktopWindows = new Wavee.Protocol.ClientToken.NativeDesktopWindowsData
            {
                OsVersion = 10,
                OsBuild = Environment.OSVersion.Version.Build,
                PlatformId = 2,
                UnknownValue5 = 12,           // wire-observed on a 1.2.93.667 ARM64 desktop
                UnknownValue6 = 12,
                PeMachine = RuntimeInformation.ProcessArchitecture switch
                {
                    Architecture.X86 => 332,
                    Architecture.X64 => 34404,
                    Architecture.Arm => 452,
                    Architecture.Arm64 => 43620,
                    _ => 34404,
                },
                // image_file_machine + unknown_value_10 are intentionally omitted (genuine-client signature).
            };
        }
        return data;
    }

    /// <summary>The local machine SID prefix (S-1-5-21-…) the desktop client sends as the attestation's device id.
    /// THE ONE Windows-registry read left in this file: <c>Platform</c> has no member for it (it persists nothing and
    /// nothing else in the app wants it), so a seam would be a public surface with exactly one caller. Null on any
    /// failure and on a non-Windows run — the caller then sends the Connect device id, which the 0.2.9 client also
    /// did whenever the read failed.</summary>
    static string? MachineSid()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var profiles = Microsoft.Win32.Registry.LocalMachine
                .OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList");
            if (profiles is null) return null;
            foreach (string name in profiles.GetSubKeyNames())
            {
                if (!name.StartsWith("S-1-5-21-", StringComparison.Ordinal)) continue;
                int lastDash = name.LastIndexOf('-');
                if (lastDash <= "S-1-5-21".Length) continue;
                return name[..lastDash];
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // A locked-down profile: the attestation takes the device id instead.
        }
        return null;
    }

    static Wavee.Protocol.ClientToken.ClientTokenRequest SolveClientTokenChallenge(Wavee.Protocol.ClientToken.ChallengesResponse challenges)
    {
        var request = new Wavee.Protocol.ClientToken.ClientTokenRequest
        {
            RequestType = Wavee.Protocol.ClientToken.ClientTokenRequestType.RequestChallengeAnswersRequest,
            ChallengeAnswers = new Wavee.Protocol.ClientToken.ChallengeAnswersRequest { State = challenges.State },
        };
        Span<byte> suffix = stackalloc byte[16];
        Span<char> hex = stackalloc char[32];
        foreach (var challenge in challenges.Challenges)
        {
            if (challenge.Type != Wavee.Protocol.ClientToken.ChallengeType.ChallengeHashCash) continue;
            var parameters = challenge.EvaluateHashcashParameters;
            if (parameters is null) continue;
            RandomNumberGenerator.Fill(suffix);
            Hashcash.Solve([], Convert.FromHexString(parameters.Prefix), parameters.Length, suffix);
            Hex.Encode(suffix, hex);
            request.ChallengeAnswers.Answers.Add(new Wavee.Protocol.ClientToken.ChallengeAnswer
            {
                ChallengeType = Wavee.Protocol.ClientToken.ChallengeType.ChallengeHashCash,
                HashCash = new Wavee.Protocol.ClientToken.HashCashAnswer { Suffix = new string(hex).ToUpperInvariant() },
            });
        }
        return request;
    }

    /// <summary>login5 exchanges the reusable blob for the bearer spclient accepts (the OAuth token is a Web-API
    /// audience and spclient refuses it). Solves the hashcash if challenged — this variant hashes context =
    /// login_context with a RAW-byte suffix, where the client-token variant uses an empty context and hex.</summary>
    static string MintAccessToken(bool force, CancellationToken ct)
    {
        lock (TokenGate)
        {
            if (!force && s_accessToken is { Length: > 0 } && NowMs < s_accessExpiresAtMs - 120_000) return s_accessToken;

            var login = s_login ?? throw new InvalidOperationException("login5 before a welcome");
            var request = new Wavee.Protocol.Login.LoginRequest
            {
                ClientInfo = new Wavee.Protocol.Login.ClientInfo { ClientId = Identity.ClientId, DeviceId = Platform.DeviceId },
                StoredCredential = new Wavee.Protocol.Login.StoredCredential
                {
                    Username = login.Username,
                    Data = ByteString.CopyFrom(login.Reusable),
                },
            };

            for (int attempt = 0; attempt < 4; attempt++)
            {
                byte[] bytes = PostProto("https://login5.spotify.com/v3/login", request.ToByteArray(),
                    s_clientToken, Identity.UserAgent, ct);
                var response = Wavee.Protocol.Login.LoginResponse.Parser.ParseFrom(bytes);
                if (response.Ok is { } ok)
                {
                    s_accessToken = ok.AccessToken;
                    s_accessExpiresAtMs = NowMs + 1000L * (ok.AccessTokenExpiresIn > 0 ? ok.AccessTokenExpiresIn : 3600);
                    Log.Info("spotify", "access token minted (expires in "
                        + (ok.AccessTokenExpiresIn > 0 ? ok.AccessTokenExpiresIn : 3600) + "s)");
                    return ok.AccessToken;
                }
                if (response.Challenges is { } challenges && challenges.Challenges_.Count > 0)
                {
                    SolveLogin5Challenges(request, response);
                    continue;
                }
                if (response.Error != Wavee.Protocol.Login.LoginError.UnknownError)
                    throw new InvalidOperationException("login5 error: " + response.Error);
            }
            throw new InvalidOperationException("login5: no access token after retries");
        }
    }

    static void SolveLogin5Challenges(Wavee.Protocol.Login.LoginRequest request, Wavee.Protocol.Login.LoginResponse response)
    {
        var solutions = new Wavee.Protocol.Login.ChallengeSolutions();
        Span<byte> suffix = stackalloc byte[16];
        foreach (var challenge in response.Challenges.Challenges_)
        {
            if (challenge.Hashcash is not { } hashcash) continue;
            RandomNumberGenerator.Fill(suffix);
            long started = Stopwatch.GetTimestamp();
            Hashcash.Solve(response.LoginContext.Span, hashcash.Prefix.Span, hashcash.Length, suffix);
            var elapsed = Stopwatch.GetElapsedTime(started);
            solutions.Solutions.Add(new Wavee.Protocol.Login.ChallengeSolution
            {
                Hashcash = new Wavee.Protocol.Login.HashcashSolution
                {
                    Suffix = ByteString.CopyFrom(suffix),
                    Duration = Google.Protobuf.WellKnownTypes.Duration.FromTimeSpan(elapsed),
                },
            });
        }
        request.ChallengeSolutions = solutions;
        request.LoginContext = response.LoginContext;
    }

    /// <summary>The bearer for an spclient / pathfinder request. SHELL THREADS ONLY — it blocks while a refresh is in
    /// flight (C9). <paramref name="force"/> is the 401 path: a cached provider would hand back the rejected token.
    /// Returns null when there is no session to mint from.</summary>
    public static string? AccessToken(bool force = false)
    {
        if (s_login is null) return null;
        try
        {
            string token = MintAccessToken(force, SessionToken);
            if (force)
            {
                Publish(new SessionEvent(SessionEventKind.AccessTokenMinted,
                    Text: Text.Add(token), Number: s_accessExpiresAtMs));
            }
            return token;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or IOException)
        {
            Log.Warn("spotify", "access-token refresh failed", ex);
            return null;
        }
    }

    /// <summary>The attestation header value, or null. Cheap while it is fresh; within <see cref="TokenRefreshLeadMs"/> of
    /// its expiry (or when the login's mint failed) the FIRST caller re-mints it and the others wait for that one answer
    /// (G-035 — minted once per AP attempt, it used to be sent past its expiry). SHELL THREADS ONLY — the re-mint blocks.
    /// A failed re-mint keeps the old value and backs off for <see cref="ClientTokenRetryMs"/>, so a dead
    /// clienttoken.spotify.com costs one attempt a minute, not one per request.</summary>
    public static string? ClientToken()
    {
        string? token;
        long expiresAt;
        lock (TokenGate) { token = s_clientToken; expiresAt = s_clientTokenExpiresAtMs; }
        if (s_login is null || !TokenDue(NowMs, expiresAt)) return token;

        lock (ClientTokenRefreshGate)
        {
            lock (TokenGate)
            {
                // Another api thread refreshed it while this one waited for the gate.
                if (!TokenDue(NowMs, s_clientTokenExpiresAtMs)) return s_clientToken;
            }
            string? fresh = null;
            try { fresh = MintClientToken(SessionToken); }
            catch (OperationCanceledException) { }                   // the login closed mid-mint: the stale value stands
            if (fresh is null)
            {
                lock (TokenGate) s_clientTokenExpiresAtMs = NowMs + ClientTokenRetryMs + TokenRefreshLeadMs;
                return token;
            }
            Publish(new SessionEvent(SessionEventKind.ClientTokenMinted, Text: Text.Add(fresh), Number: s_clientTokenExpiresAtMs));
            return fresh;
        }
    }

    /// <summary>The back-off after a failed client-token re-mint (P10: a named interval, no timer — the next caller retries).</summary>
    public const long ClientTokenRetryMs = 60_000;

    static readonly Lock ClientTokenRefreshGate = new();

    /// <summary>The spclient base url the request runner prefixes onto a folded path.</summary>
    public static string SpclientBaseUrl()
    {
        var host = Current.SpclientHost;
        return host.IsEmpty ? "https://spclient.wg.spotify.com" : "https://" + TextOf(host);
    }

    /// <summary>The dealer's connection id — the <c>X-Spotify-Connection-Id</c> a PutState must carry.</summary>
    public static string ConnectionId() => TextOf(Current.ConnectionId);

    // ── 10. audio keys ───────────────────────────────────────────────────────────────────────────────────────────────

    public enum AudioKeyResult : byte { Ok = 0, Offline, Timeout, Rejected, Busy }

    /// <summary>One outstanding 0x0c. Bounded table (C8): 32 slots, and a full table REFUSES rather than queues — the
    /// caller (the audio stream) has a fallback path and a queue would only add latency to a failure.</summary>
    sealed class KeySlot
    {
        public readonly ManualResetEventSlim Done = new(false);
        public readonly byte[] Key = new byte[AudioKey.KeyLength];
        public uint Seq;
        public bool InUse;
        public int Error;
    }

    static readonly KeySlot?[] s_keys = new KeySlot?[32];
    static readonly Lock KeyGate = new();
    static uint s_keySeq;

    /// <summary>Fetch the 16-byte AES key for a file over the persistent AP channel. BLOCKS up to
    /// <paramref name="timeoutMs"/> — a shell thread's call (C9), never the UI thread's.</summary>
    public static AudioKeyResult RequestAudioKey(ReadOnlySpan<byte> fileId, ReadOnlySpan<byte> trackGid,
        Span<byte> key16, int timeoutMs = 5000)
    {
        if (key16.Length < AudioKey.KeyLength) throw new ArgumentException("needs 16 bytes", nameof(key16));
        if (s_apStream is null) return AudioKeyResult.Offline;

        KeySlot? slot = null;
        uint seq;
        lock (KeyGate)
        {
            for (int i = 0; i < s_keys.Length; i++)
            {
                var candidate = s_keys[i];
                if (candidate is null || candidate.InUse) continue;
                slot = candidate;
                break;
            }
            if (slot is null) return AudioKeyResult.Busy;
            seq = s_keySeq++;
            slot.InUse = true;
            slot.Seq = seq;
            slot.Error = 0;
            slot.Done.Reset();
        }

        try
        {
            Span<byte> body = stackalloc byte[AudioKey.RequestLength];
            int n = AudioKey.WriteRequest(fileId, trackGid, seq, body);
            if (!SendOnChannel(Handshake.CmdAesKeyRequest, body[..n])) return AudioKeyResult.Offline;
            if (!slot.Done.Wait(timeoutMs)) return AudioKeyResult.Timeout;
            if (slot.Error != 0) return AudioKeyResult.Rejected;
            slot.Key.CopyTo(key16);
            return AudioKeyResult.Ok;
        }
        finally
        {
            lock (KeyGate) slot.InUse = false;
        }
    }

    /// <summary>AP PUMP THREAD: hand a reply to whoever is waiting for that sequence.</summary>
    static void CompleteKey(uint seq, ReadOnlySpan<byte> key, int error)
    {
        lock (KeyGate)
        {
            for (int i = 0; i < s_keys.Length; i++)
            {
                var slot = s_keys[i];
                if (slot is null || !slot.InUse || slot.Seq != seq) continue;
                if (error == 0 && key.Length >= AudioKey.KeyLength) key[..AudioKey.KeyLength].CopyTo(slot.Key);
                slot.Error = error;
                slot.Done.Set();
                return;
            }
        }
    }

    /// <summary>The channel dropped: release every waiter so its caller can retry on the next connection.</summary>
    static void FailAllKeys()
    {
        lock (KeyGate)
        {
            for (int i = 0; i < s_keys.Length; i++)
            {
                var slot = s_keys[i];
                if (slot is null || !slot.InUse) continue;
                slot.Error = -1;
                slot.Done.Set();
            }
        }
    }

    // ── 11. the dealer websocket ─────────────────────────────────────────────────────────────────────────────────────
    //
    // ONE firehose: cluster pushes, library/playlist events and the Connect REQUEST commands all arrive here. This
    // loop owns the protocol half only — ping/pong, the connection id, the half-open watchdog and the reconnect — and
    // hands every other frame to `Spotify.Connect.OnDealer` (owner F), which decides what it means.

    /// <summary>The keepalive interval (P10 names this timer).</summary>
    public const int DealerPingIntervalMs = 30_000;

    /// <summary>No frame at all — not even a pong to our pings — for this long means the TCP socket is dead but not
    /// closed ("half open"): abort it so the blocked receive throws and the loop reconnects.</summary>
    public const int DealerDeadAfterMs = 70_000;

    /// <summary>Big enough for a full cluster push plus its inflated form (the parse decodes into the head and
    /// inflates into the tail).</summary>
    const int DealerScratchBytes = 256 * 1024;

    static ClientWebSocket? s_dealer;
    static long s_lastDealerTick;
    static readonly Lock DealerSendGate = new();

    static void CloseDealerSocket()
    {
        var ws = s_dealer;
        s_dealer = null;
        try { ws?.Abort(); } catch (ObjectDisposedException) { }
        ws?.Dispose();
    }

    /// <summary>The dealer thread, for ONE dealer epoch (<see cref="Session.Epoch"/>). Every exit is one always-on line,
    /// <c>dealer.exit reason= epoch=</c> — <c>epoch-cancelled</c> or <c>dropped</c> — and a start that arrived while it
    /// was unwinding is honoured from here (<see cref="StartDealer"/>).</summary>
    static void DealerLoop(uint epoch, CancellationToken ct)
    {
        string exit = "faulted";
        try { exit = DealerLoopCore(epoch, ct); }
        finally
        {
            uint restart;
            lock (ThreadGate)
            {
                Volatile.Write(ref s_dealerRunning, 0);
                restart = s_dealerRestartEpoch;
                s_dealerRestartEpoch = 0;
            }
            Log.Info("spotify", "dealer.exit reason=" + exit + " epoch=" + epoch);
            if (restart != 0 && restart != epoch && restart == Current.Epoch) StartDealer(restart);
        }
    }

    /// <summary>Connect, receive until the socket dies, and say why it ended. A drop is published as
    /// <c>DealerDropped</c> stamped with this epoch: the fold answers <c>CloseDealer | DealerBackoff</c> and the dealer's
    /// OWN retry timer starts the next thread — the AP channel never hears of it (B4). Only this epoch's own cancellation
    /// ends the loop quietly; any other exception — an HttpClient timeout's TaskCanceledException out of a bearer re-mint
    /// included, which the old <c>catch (OperationCanceledException) { return; }</c> swallowed into a dead dealer with no
    /// reconnect — is a drop. Returns the exit reason.</summary>
    static string DealerLoopCore(uint epoch, CancellationToken ct)
    {
        byte[] scratch = new byte[DealerScratchBytes];
        byte[] receive = new byte[64 * 1024];
        var frame = new MemoryStream(64 * 1024);
        bool forceToken = false;
        bool retried = false;

        while (true)
        {
            if (ct.IsCancellationRequested) return ExitEpochCancelled;
            try
            {
                string? token = AccessToken(forceToken);
                forceToken = false;
                if (token is null) throw new IOException("no access token for the dealer");

                string host = TextOf(Current.DealerHost);
                if (host.Length == 0) host = "dealer.spotify.com";

                var ws = new ClientWebSocket();
                s_dealer = ws;
                try
                {
                    ws.ConnectAsync(new Uri("wss://" + host + "/?access_token=" + Uri.EscapeDataString(token)), ct)
                      .GetAwaiter().GetResult();
                }
                catch (Exception) when (!ct.IsCancellationRequested)
                {
                    // A failed wss handshake is indistinguishable from a rejected (expired) token at this layer, and
                    // the plain provider only re-mints near expiry: force one for the next attempt.
                    forceToken = true;
                    throw;
                }

                Volatile.Write(ref s_lastDealerTick, Environment.TickCount64);
                Log.Info("spotify", "dealer connected (" + host + ", epoch " + epoch + ")");
                StartKeepalive(ws, ct);
                Receive(ws, frame, receive, scratch, epoch, ct);
                throw new IOException("the dealer closed the socket");
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                // The epoch was abandoned under this thread (its own drop's fold, a sign-out, a disconnect): whatever the
                // socket threw on the way down is the teardown's echo. `CloseDealer` already closed it; this is idempotent.
                CloseDealerSocket();
                return ExitEpochCancelled;
            }
            catch (Exception ex)
            {
                Log.Warn("spotify", "dealer dropped (epoch " + epoch + ")", ex);
                CloseDealerSocket();
                if (forceToken && !retried)
                {
                    // The wss handshake failed and the token is the likeliest reason: ONE retry with a force-minted
                    // bearer before the dealer epoch is given up (0.2.9's G6 fix, kept).
                    retried = true;
                    continue;
                }
                // A cancellation landing between the filter above and this post leaves a drop stamped with an epoch the
                // fold has already moved past: `IsStale` refuses it.
                Publish(new SessionEvent(SessionEventKind.DealerDropped, Number: (long)SessionFault.Network, Epoch: epoch));
                return "dropped";    // the dealer's own backoff owns the next attempt; the AP is untouched (B4)
            }
        }
    }

    static void Receive(ClientWebSocket ws, MemoryStream frame, byte[] receive, byte[] scratch, uint epoch, CancellationToken ct)
    {
        var segment = new ArraySegment<byte>(receive);
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            frame.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = ws.ReceiveAsync(segment, ct).GetAwaiter().GetResult();
                if (result.MessageType == WebSocketMessageType.Close) return;
                frame.Write(receive, 0, result.Count);
            }
            while (!result.EndOfMessage);

            Volatile.Write(ref s_lastDealerTick, Environment.TickCount64);   // any frame means the link is alive
            var utf8 = frame.GetBuffer().AsSpan(0, (int)frame.Length);
            Dispatch(utf8, scratch, epoch);
        }
    }

    /// <summary>Protocol frames are answered here; everything else goes to `Connect.OnDealer` (owner F), which parses
    /// it again with the same pure parser. The re-parse is deliberate: it keeps this loop ignorant of what a topic
    /// means, and a frame the Connect glue cares about arrives a few times a second at most. The pusher's hello is
    /// stamped with this dealer <paramref name="epoch"/>: a dead connection's late hello is refused by the fold.</summary>
    static void Dispatch(ReadOnlySpan<byte> utf8, byte[] scratch, uint epoch)
    {
        var message = DealerFrame.Parse(utf8, scratch);
        switch (message.Kind)
        {
            case DealerFrameKind.Ping:
                SendDealerText(DealerPong);
                return;
            case DealerFrameKind.Pong:
            case DealerFrameKind.Unknown:
                return;
        }

        if (!message.ConnectionId.IsEmpty && message.Uri.StartsWith("hm://pusher/"u8))
        {
            // The pusher's hello, and the ONE frame that carries the connection id a PutState must quote. It has no
            // payload anyone folds, so it never reaches the Connect glue.
            Publish(new SessionEvent(SessionEventKind.DealerOnline, Text: Text.Add(message.ConnectionId), Epoch: epoch));
            return;
        }

        Connect.OnDealer(utf8);
    }

    static readonly byte[] DealerPing = "{\"type\":\"ping\"}"u8.ToArray();
    static readonly byte[] DealerPong = "{\"type\":\"pong\"}"u8.ToArray();

    static void SendDealerText(ReadOnlySpan<byte> utf8)
    {
        var ws = s_dealer;
        if (ws is null || ws.State != WebSocketState.Open) return;
        byte[] copy = utf8.ToArray();   // the async send outlives the span
        Wire.NoteSocket("dealer", Wire.DealerKind(utf8), copy.Length);
        lock (DealerSendGate)
        {
            try { ws.SendAsync(copy.AsMemory(), WebSocketMessageType.Text, true, CancellationToken.None).GetAwaiter().GetResult(); }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or OperationCanceledException)
            {
                // A failed send drops the connection: the receive loop exits and the dealer reconnects on its own (B4).
            }
        }
    }

    /// <summary>Ack a dealer REQUEST. Called by <c>Spotify.Connect</c> (owner F) after it has folded the command.</summary>
    public static void Reply(ReadOnlySpan<byte> key, bool ok)
    {
        Span<byte> buffer = stackalloc byte[256];
        int n = DealerFrame.WriteReply(buffer, key, ok);
        if (n > 0) SendDealerText(buffer[..n]);
    }

    /// <summary>The 30 s keepalive plus the half-open watchdog plus the 10-minute server-clock re-sync. One named
    /// thread for all three, because all three are "wake up on a schedule and do one small thing" (P10).</summary>
    static void StartKeepalive(ClientWebSocket ws, CancellationToken ct)
    {
        lock (ThreadGate)
        {
            if (Interlocked.CompareExchange(ref s_keepaliveRunning, 1, 0) != 0) return;
            new Thread(() => KeepaliveLoop(ws, ct)) { IsBackground = true, Name = "wavee-spotify-keepalive" }.Start();
        }
    }

    static void KeepaliveLoop(ClientWebSocket ws, CancellationToken ct)
    {
        try { KeepaliveTicks(ws, ct); }
        finally { Volatile.Write(ref s_keepaliveRunning, 0); }
    }

    static void KeepaliveTicks(ClientWebSocket ws, CancellationToken ct)
    {
        long nextClockSync = Environment.TickCount64;   // the first probe happens on the first tick
        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            if (ct.WaitHandle.WaitOne(DealerPingIntervalMs)) return;
            if (Environment.TickCount64 - Volatile.Read(ref s_lastDealerTick) > DealerDeadAfterMs)
            {
                Log.Warn("spotify", "dealer half-open (no traffic) — forcing a reconnect");
                try { ws.Abort(); } catch (ObjectDisposedException) { }
                return;
            }
            SendDealerText(DealerPing);
            if (Environment.TickCount64 >= nextClockSync)
            {
                SyncServerClock(ct);
                nextClockSync = Environment.TickCount64 + ServerClockResyncMs;
            }
        }
    }

    // ── 12. the server clock ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>How often the clock is re-probed (P10 names this timer).</summary>
    public const int ServerClockResyncMs = 10 * 60 * 1000;

    const int ClockSamplesPerRound = 3;

    /// <summary>One NTP-style round over <c>/melody/v1/time</c>, folded by <see cref="ObserveClockProbe"/>. A total
    /// failure is a no-op: the previous offset (or the passive bootstrap) stands.</summary>
    static void SyncServerClock(CancellationToken ct)
    {
        Session s = Current;
        long bestRtt = long.MaxValue;
        for (int i = 0; i < ClockSamplesPerRound && !ct.IsCancellationRequested; i++)
        {
            try
            {
                long t1 = NowMs;
                long serverMs = FetchServerTimeMs(ct);
                long t2 = NowMs;
                ObserveClockProbe(ref s, t1, serverMs, t2, ref bestRtt);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException)
            {
                Log.Warn("spotify", "server-clock probe failed", ex);
            }
        }
        if (bestRtt == long.MaxValue) return;
        Session folded = s;
        Post(() =>
        {
            Session current = Current;
            current.ClockOffsetMs = folded.ClockOffsetMs;
            current.ClockRttMs = folded.ClockRttMs;
            current.ClockSynced = true;
            current.ClockProbed = true;
            Volatile.Write(ref s_box, new Box(current));
        });
        Log.Info("spotify", "server-clock synced offset=" + folded.ClockOffsetMs + "ms rtt=" + folded.ClockRttMs + "ms");
    }

    static long FetchServerTimeMs(CancellationToken ct)
    {
        Span<char> path = stackalloc char[64];
        var request = Build(Current, RequestKind.ServerTime, new RequestArgs(), path);
        byte[] body = Get(SpclientBaseUrl() + new string(request.Path), ct, authed: true);
        var reader = new Utf8JsonReader(body, new JsonReaderOptions { MaxDepth = 4 });
        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.PropertyName) continue;
            bool wanted = reader.ValueTextEquals("timestamp"u8);
            reader.Read();
            if (wanted && reader.TokenType == JsonTokenType.Number) return reader.GetInt64();
        }
        return 0;
    }

    /// <summary>A free clock sample: every cluster push carries the server's emit time. Called by
    /// <c>Spotify.Connect</c> (owner F) on the dealer thread; the fold is pure and the write is posted (C1).</summary>
    public static void ObserveClusterTimestamp(long serverTimestampMs)
    {
        if (serverTimestampMs <= 0) return;
        long now = NowMs;
        Post(() =>
        {
            Session s = Current;
            bool drift = ObservePassiveClock(ref s, serverTimestampMs, now);
            Volatile.Write(ref s_box, new Box(s));
            if (drift) Log.Info("spotify", "server-clock drift — the next keepalive tick re-probes");
        });
    }

    // ── 13. the interactive sign-in (G-030, D2) ──────────────────────────────────────────────────────────────────────
    //
    // A fresh profile's way in. Two flows, each one attempt on its own named background thread, each cancellable, and
    // either may run beside the other (the surface shows the pairing code while the browser is open): the first token
    // wins, cancels its sibling and is handed to the session (`LoginWithToken`). Everything decided along the way is
    // `Spotify.OAuth` (pure); what the surface renders is `SignIn.State` (UI thread only, written through `Post`).
    // LOGGING: stages and HTTP statuses only — never a token, a code, a verifier or a pairing code.

    /// <summary>The interactive sign-in: PKCE loopback (primary) and the device grant (fallback). SHELL.</summary>
    public static class SignIn
    {
        /// <summary>What the sign-in surface renders. UI THREAD.</summary>
        public static Signal<SignInState> State { get; } = new(SignInState.Idle);

        /// <summary>Bumped (UI thread) each time something asked for the sign-in surface — <see cref="Login"/> with nothing to
        /// resume. The GUI's door watches it; nothing else does.</summary>
        public static Signal<int> Requests { get; } = new(0);

        /// <summary>Opens the authorize url. The OS default handler unless a host swaps it; throwing means "no browser".</summary>
        public static Action<string> OpenBrowser { get; set; } = static url =>
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();

        /// <summary>How long the loopback listener waits for the browser to come back (P10: a named window, not a poll).</summary>
        public const int BrowserWindowMs = 10 * 60 * 1000;

        /// <summary>A redirect's request arrives the moment its connection opens; a connection that stays silent this long is a
        /// browser pre-connect, and waiting longer would hold the real redirect in the backlog.</summary>
        const int RequestHeadBytes = 8192, RequestReadTimeoutMs = 2_000;

        static readonly Lock Gate = new();
        static CancellationTokenSource? s_browser, s_code;

        /// <summary>Start (or restart) one flow. Returns immediately (C9). A restart cancels that flow's previous attempt; the
        /// other flow is left alone.</summary>
        public static void Start(SignInMethod method)
        {
            Spotify.Boot();
            var cts = new CancellationTokenSource();
            CancellationTokenSource? previous;
            lock (Gate)
            {
                if (method == SignInMethod.Browser) { previous = s_browser; s_browser = cts; }
                else { previous = s_code; s_code = cts; }
            }
            previous?.Cancel();
            Post(() =>
            {
                if (IsCurrent(method, cts))
                    State.Value = State.Peek().With(method, SignInStage.Starting) with { Handed = false };
            });
            var thread = method == SignInMethod.Browser
                ? new Thread(() => BrowserFlow(cts)) { Name = "wavee-signin-browser" }
                : new Thread(() => DeviceFlow(cts)) { Name = "wavee-signin-code" };
            thread.IsBackground = true;
            thread.Start();
            Log.Info("spotify", "sign-in: " + (method == SignInMethod.Browser ? "browser" : "device code") + " flow started");
        }

        /// <summary>Stop one flow (the surface's Cancel while the browser is out). Its rung returns to Idle.</summary>
        public static void Cancel(SignInMethod method)
        {
            CancellationTokenSource? cts;
            lock (Gate)
            {
                if (method == SignInMethod.Browser) { cts = s_browser; s_browser = null; }
                else { cts = s_code; s_code = null; }
            }
            cts?.Cancel();
            Post(() => State.Value = State.Peek().With(method, SignInStage.Idle));
        }

        /// <summary>Stop both flows and forget the state — the surface closed. A token already handed to the session is not
        /// recalled: that login finishes (or fails) on its own.</summary>
        public static void Reset()
        {
            CancellationTokenSource? browser, code;
            lock (Gate) { browser = s_browser; code = s_code; s_browser = null; s_code = null; }
            browser?.Cancel();
            code?.Cancel();
            Post(static () => State.Value = SignInState.Idle);
        }

        static bool IsCurrent(SignInMethod method, CancellationTokenSource cts)
        {
            lock (Gate) return ReferenceEquals(method == SignInMethod.Browser ? s_browser : s_code, cts);
        }

        static void Report(SignInMethod method, CancellationTokenSource cts, SignInStage stage, SignInError error = SignInError.None)
            => Post(() => { if (IsCurrent(method, cts)) State.Value = State.Peek().With(method, stage, error); });

        /// <summary>A flow holds a token: on the UI thread, if that attempt is still the current one, cancel the sibling and
        /// log in with it.</summary>
        static void Handoff(SignInMethod method, CancellationTokenSource cts, string accessToken)
        {
            Log.Info("spotify", "sign-in: authorized (" + (method == SignInMethod.Browser ? "browser" : "device code") + ") — logging in");
            Post(() =>
            {
                if (!IsCurrent(method, cts)) return;          // cancelled while the exchange was in flight
                CancellationTokenSource? sibling;
                lock (Gate)
                {
                    if (method == SignInMethod.Browser) { sibling = s_code; s_code = null; }
                    else { sibling = s_browser; s_browser = null; }
                }
                sibling?.Cancel();
                // Both rungs rest: the sibling's pairing code died with its flow, and from here the session's phase is the
                // progress. A code the user wants after a failed login is a fresh one.
                State.Value = SignInState.Idle with { Handed = true };
                LoginWithToken(accessToken);
            });
        }

        // ── the browser: authorization code + PKCE over a 127.0.0.1 loopback redirect ─────────────────────────────────

        static void BrowserFlow(CancellationTokenSource cts)
        {
            CancellationToken ct = cts.Token;
            using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
            window.CancelAfter(BrowserWindowMs);
            TcpListener? listener = null;
            try
            {
                Span<char> buffer = stackalloc char[64];
                Pkce.NewVerifier(buffer);
                string verifier = new(buffer);
                Span<char> challengeChars = stackalloc char[64];
                string challenge = new(challengeChars[..Pkce.Challenge(verifier, challengeChars)]);
                Pkce.NewVerifier(buffer[..43]);
                string state = new(buffer[..43]);

                // A raw TCP listener on an OS-assigned loopback port: no http.sys URL reservation, no firewall prompt, and
                // the port is held from bind to close (no probe-then-bind race).
                listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
                listener.Start(4);
                int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
                string redirect = OAuth.RedirectUri(port);
                using var stop = window.Token.Register(static l => { try { ((TcpListener)l!).Stop(); } catch (SocketException) { } }, listener);

                try { OpenBrowser(OAuth.AuthorizeUrl(Identity.ClientId, redirect, challenge, state)); }
                catch (Exception ex)                                      // a host-swappable seam: whatever it throws means "no browser"
                {
                    Log.Warn("spotify", "sign-in: no browser could be opened", ex);
                    Report(SignInMethod.Browser, cts, SignInStage.Failed, SignInError.BrowserUnavailable);
                    return;
                }
                Report(SignInMethod.Browser, cts, SignInStage.Waiting);
                Log.Info("spotify", "sign-in: browser opened (loopback port " + port + ")");

                byte[] head = new byte[RequestHeadBytes];
                string code;
                while (true)
                {
                    using TcpClient client = listener.AcceptTcpClient();   // Stop() on cancel/window unblocks it
                    client.ReceiveTimeout = RequestReadTimeoutMs;
                    client.SendTimeout = RequestReadTimeoutMs;
                    NetworkStream stream = client.GetStream();
                    int length;
                    try { length = ReadHead(stream, head); }
                    catch (IOException) { continue; }                     // a pre-connect that never spoke
                    ReadOnlySpan<byte> target = OAuth.RequestTarget(head.AsSpan(0, length));
                    if (target.IsEmpty) continue;
                    OAuth.Redirect outcome = OAuth.ParseRedirect(target, state, out code);
                    if (outcome == OAuth.Redirect.Code) { Respond(stream, 200, signedIn: true); break; }
                    if (outcome == OAuth.Redirect.Denied)
                    {
                        Respond(stream, 200, signedIn: false);
                        Log.Info("spotify", "sign-in: the browser sign-in was declined");
                        Report(SignInMethod.Browser, cts, SignInStage.Denied);
                        return;
                    }
                    Respond(stream, outcome == OAuth.Redirect.NotOurs ? 404 : 400, signedIn: false);
                }

                Report(SignInMethod.Browser, cts, SignInStage.Exchanging);
                var (status, body) = PostForm(OAuth.TokenEndpoint, OAuth.CodeExchangeForm(code, redirect, Identity.ClientId, verifier), ct);
                OAuth.TokenAnswer answer = OAuth.ParseToken(status, body, out string access, out _);
                if (answer == OAuth.TokenAnswer.Token) { Handoff(SignInMethod.Browser, cts, access); return; }
                Log.Warn("spotify", "sign-in: the authorization code was not exchanged (" + status + ", " + answer + ")");
                Report(SignInMethod.Browser, cts, SignInStage.Failed,
                    answer == OAuth.TokenAnswer.Refused ? SignInError.Refused : SignInError.Network);
            }
            catch (Exception ex)                                          // the flow's own thread: nothing may escape it
            {
                if (ct.IsCancellationRequested) return;
                if (window.IsCancellationRequested)
                {
                    Log.Info("spotify", "sign-in: the browser did not come back inside its window");
                    Report(SignInMethod.Browser, cts, SignInStage.Expired);
                    return;
                }
                Log.Warn("spotify", "sign-in: the browser flow failed (" + ex.GetType().Name + ")");
                Report(SignInMethod.Browser, cts, SignInStage.Failed, SignInError.Network);
            }
            finally
            {
                try { listener?.Stop(); } catch (SocketException) { }
            }
        }

        /// <summary>Read an HTTP request head (up to the blank line, the buffer, or the peer's close). Returns the bytes read.</summary>
        static int ReadHead(NetworkStream stream, byte[] head)
        {
            int length = 0;
            while (length < head.Length)
            {
                int n = stream.Read(head, length, head.Length - length);
                if (n <= 0) break;
                length += n;
                if (head.AsSpan(0, length).IndexOf("\r\n\r\n"u8) >= 0) break;
            }
            return length;
        }

        /// <summary>The one page the browser shows before its tab is closed. English on purpose: it is served before, and
        /// independently of, the app's localization, and it carries no data.</summary>
        static void Respond(NetworkStream stream, int status, bool signedIn)
        {
            string html = status != 200 ? ""
                : "<!doctype html><meta charset=utf-8><title>Wavee</title>"
                + "<body style=\"font:16px Segoe UI,system-ui,sans-serif;text-align:center;padding:56px;background:#0b0b0c;color:#f5f5f4\">"
                + (signedIn
                    ? "<h2>You're signed in to Wavee.</h2><p style=\"color:#a8a29e\">You can close this tab and return to the app.</p>"
                    : "<h2>Sign-in was cancelled.</h2><p style=\"color:#a8a29e\">You can close this tab and try again in the app.</p>")
                + "</body>";
            byte[] body = Encoding.UTF8.GetBytes(html);
            string reason = status switch { 200 => "OK", 404 => "Not Found", _ => "Bad Request" };
            byte[] header = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + " " + reason
                + "\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: " + body.Length
                + "\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
            try
            {
                stream.Write(header, 0, header.Length);
                stream.Write(body, 0, body.Length);
            }
            catch (IOException) { }                                     // the tab closed first: nothing to tell it
        }

        // ── the device grant: a pairing code and a QR ────────────────────────────────────────────────────────────────

        static void DeviceFlow(CancellationTokenSource cts)
        {
            CancellationToken ct = cts.Token;
            try
            {
                var (status, body) = PostForm(OAuth.DeviceAuthorizeEndpoint, OAuth.DeviceAuthorizeForm(Identity.ClientId), ct);
                if (!OAuth.TryParseDeviceCode(status, body, out OAuth.DeviceCode grant))
                {
                    Log.Warn("spotify", "sign-in: no pairing code (" + status + ")");
                    Report(SignInMethod.DeviceCode, cts, SignInStage.Failed, SignInError.Network);
                    return;
                }
                long expiresAt = NowMs + grant.ExpiresInSeconds * 1000L;
                Post(() =>
                {
                    if (!IsCurrent(SignInMethod.DeviceCode, cts)) return;
                    State.Value = State.Peek() with
                    {
                        Code = SignInStage.Waiting, Error = SignInError.None, UserCode = grant.UserCode,
                        VerificationUri = grant.VerificationUri, VerificationUriComplete = grant.VerificationUriComplete,
                        CodeExpiresAtMs = expiresAt,
                    };
                });
                Log.Info("spotify", "sign-in: pairing code issued (expires in " + grant.ExpiresInSeconds + "s)");

                string form = OAuth.DevicePollForm(grant.Code, Identity.ClientId);
                int intervalMs = grant.IntervalSeconds * 1000;
                while (NowMs < expiresAt)
                {
                    if (ct.WaitHandle.WaitOne(intervalMs)) return;
                    OAuth.TokenAnswer answer;
                    string access;
                    try
                    {
                        var (pollStatus, pollBody) = PostForm(OAuth.TokenEndpoint, form, ct);
                        answer = OAuth.ParseToken(pollStatus, pollBody, out access, out _);
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested
                                               && ex is HttpRequestException or IOException or OperationCanceledException)
                    {
                        answer = OAuth.TokenAnswer.Transient;             // one blip must not kill a multi-minute pairing
                        access = "";
                    }
                    if (answer == OAuth.TokenAnswer.Token) { Handoff(SignInMethod.DeviceCode, cts, access); return; }
                    if (answer == OAuth.TokenAnswer.Expired) break;
                    if (!OAuth.KeepsPolling(answer))
                    {
                        Log.Info("spotify", "sign-in: the pairing ended (" + answer + ")");
                        Report(SignInMethod.DeviceCode, cts,
                            answer == OAuth.TokenAnswer.Denied ? SignInStage.Denied : SignInStage.Failed,
                            answer == OAuth.TokenAnswer.Refused ? SignInError.Refused : SignInError.None);
                        return;
                    }
                    intervalMs = OAuth.NextPollIntervalMs(answer, intervalMs);
                }
                Log.Info("spotify", "sign-in: the pairing code expired before it was approved");
                Report(SignInMethod.DeviceCode, cts, SignInStage.Expired);
            }
            catch (Exception ex)                                          // the flow's own thread: nothing may escape it
            {
                if (ct.IsCancellationRequested) return;
                Log.Warn("spotify", "sign-in: the pairing flow failed (" + ex.GetType().Name + ")");
                Report(SignInMethod.DeviceCode, cts, SignInStage.Failed, SignInError.Network);
            }
        }

        /// <summary>One form POST. The status is returned, never thrown: an OAuth error is a 4xx with a JSON body the fold
        /// reads. Transport failures throw.</summary>
        static (int Status, byte[] Body) PostForm(string url, string form, CancellationToken ct)
        {
            using var msg = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(Encoding.ASCII.GetBytes(form)) };
            msg.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");
            msg.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var resp = Http.Send(msg, ct);
            using var stream = resp.Content.ReadAsStream(ct);
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return ((int)resp.StatusCode, buffer.ToArray());
        }
    }
}
