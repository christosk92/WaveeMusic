// ── Screens/RateNudge.Host.cs ──────────────────────────────────────────────────────────────────────────────────────
// the live side of the rate/star nudge: the seams, boot, the one track-boundary entry, listening accrual, the
// once-a-second check, Act (every button's verb) and Commit (the one place the ledger is written and logged).
//
// Role: SHELL
// Owner: R
// Budget: 260 lines
// Spec: docs/plans/wavee/rate-nudge-implementation.md §4
//
// DECIDES NOTHING. Every branch here is on a Decision, an Event or a seam's result. The facts are read with Peek: the
// layer's effects subscribe to exactly the signals that should wake it (§5), nothing else.

using System.Globalization;
using System.Runtime.InteropServices;
using FluentGpu;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.WindowsApi.Packaging;
using FluentGpu.WindowsApi.Store;

namespace Wavee;

public static partial class RateNudge
{
    const string Cat = "ratenudge";

    // ══ 1. SEAMS: what the inspector swaps, always as one record ═══════════════════════════════════════════════════

    public sealed record Seams(
        Func<DateTimeOffset> Now,                                   // calendar time: install age, cooldowns, trouble
        Func<long> MonoMs,                                          // monotonic ms: since-track-change, accrual, the tuck
        Func<long> IdleMs,                                          // ms since the last press/key/wheel; -1 = none yet
        Func<WindowFact> Window,
        Func<nint, CancellationToken, Task<StoreAnswer>> StoreDialog,
        Func<string, bool> Launch,                                  // open a URL or URI; false when nothing opened
        Func<CancellationToken, Task<int?>> FetchStars)             // the repo's star count; null on any failure
    {
        public static readonly Seams Live = new(
            Now: static () => DateTimeOffset.Now,
            MonoMs: static () => Environment.TickCount64,
            IdleMs: static () => FluentApp.MsSinceInteraction,       // engine relay added by this change (§7)
            Window: LiveWindow,
            StoreDialog: LiveStoreDialog,
            Launch: LiveLaunch,
            FetchStars: LiveFetchStars);
    }

    static Seams s_seams = Seams.Live;

    /// <summary>The seams in force (the inspector's fixture writer wraps these to swap one at a time).</summary>
    internal static Seams CurrentSeams => s_seams;

    /// <summary>Swap the seams (the inspector). <see cref="ResetForInspect"/> sets them too, with the rest of the state.</summary>
    internal static void UseSeams(Seams s) => s_seams = s;

    // ══ 2. STATE ═══════════════════════════════════════════════════════════════════════════════════════════════════

    static Ledger s_ledger = Ledger.Fresh;
    static bool s_updatedThisLaunch, s_shownThisRun;
    static long? s_trackChangedMs;        // armed by a track boundary, cleared when the card shows
    static long s_playMarkMs = -1;        // the start of the stretch now playing (-1: not playing)
    static long s_bootMs;                 // MonoMs at boot: the session's age (the launch ask's hold in the update session)
    static string s_lastBlocker = "";
    static Action<Action>? s_post;        // the UI thread, from the layer's UsePost

    /// <summary>The UI lane's layer hands the host the UI-thread poster (Shell.Overlays' UsePost). Until then, posts run inline.</summary>
    internal static void BindPost(Action<Action> post) => s_post = post;

    public static Policy Rules { get; private set; } = Policy.Default;
    public static Ledger Current => s_ledger;
    public static InstallChannel Channel { get; private set; }

    /// <summary>The last Decision the host evaluated (the one that showed the card, when one did). The inspector reports it.</summary>
    public static Decision? LastDecision { get; private set; }

    static Stars? s_stars;

    /// <summary>The star count to show (the GitHub card's line, the About door's caption), or null. Never on a Store install.</summary>
    public static int? StarCount => StarsShown(Channel, s_stars, s_seams.Now(), Rules);

    /// <summary>The card on screen (null: none). The layer binds it; only Show and Answer write it.</summary>
    public static readonly Signal<Card?> Shown = new(null);

    // ══ 3. BOOT (App startup, after Update.Host.Start: UpdatedFrom and PreviousRunVersion are known) ═══════════════

    public static void Boot(IAppSettings settings)
    {
        Channel = Platform.Version.ChannelFor(PackageIdentity.IsPackaged);
        s_updatedThisLaunch = Update.Host.UpdatedFrom.Length > 0;
        string raw = settings.Get(Platform.Keys.RateNudgeLedger);
        if (!TryDecode(raw, out var ledger) && raw.Length > 0) Log.Warn(Cat, "unreadable ledger, starting fresh: '" + raw + "'", null);
        bool predates = raw.Length == 0 && Update.Host.PreviousRunVersion.Length > 0;   // ran before, never saw this feature
        s_bootMs = s_seams.MonoMs();
        ledger = Seed(ledger, s_seams.Now(), predates, Rules);
        if (Crash.Host.ThisLaunch is { } crash) ledger = Troubled(ledger, new DateTimeOffset(crash.StampLocal));
        Commit(ledger, "boot (" + Channel + ")");
        s_stars = DecodeStars(settings.Get(Platform.Keys.RateNudgeStars));
        RefreshStarsIfDue();
    }

    /// <summary>Once a day at most, off the UI thread, through the app's GitHub pool (the same client and User-Agent the
    /// release notes use). A failure keeps the old count; the next launch tries again. Offline: Wire refuses, null.</summary>
    internal static void RefreshStarsIfDue()
    {
        if (!StarsDue(Channel, s_stars, s_seams.Now(), Rules)) return;
        _ = Task.Run(async () =>
        {
            int? fetched = await s_seams.FetchStars(CancellationToken.None).ConfigureAwait(false);
            Post(() =>
            {
                if (fetched is { } n)
                {
                    s_stars = new Stars(n, s_seams.Now());
                    Platform.Settings.Set(Platform.Keys.RateNudgeStars, EncodeStars(s_stars.Value));
                }
                Log.Info(Cat, "stars: " + (fetched?.ToString(CultureInfo.InvariantCulture) ?? "fetch failed, kept " + (s_stars?.Count.ToString(CultureInfo.InvariantCulture) ?? "none")));
            });
        });
    }

    // ══ 4. GATHER: raw readings only ═══════════════════════════════════════════════════════════════════════════════

    internal static Facts Gather(bool overlayOpen)
    {
        long now = s_seams.MonoMs();
        var moment = new Moment(
            SinceTrackChangeMs: s_trackChangedMs is { } t ? now - t : null,
            IdleMs: s_seams.IdleMs(),
            Window: s_seams.Window(),
            SearchFocused: Shell.SearchFocused,
            OverlayOpen: overlayOpen,
            RouteKey: Shell.RouteKeyOf(Shell.Current.Peek()),
            StageOpen: Shell.Ui.ImmersiveLyrics.Peek(),
            VideoUp: Video.State.MainHostFullscreen.Peek() || Video.State.DetachedFullscreen.Peek(),
            WindowFullScreen: InputHooks.Current.Default.IsWindowFullscreen?.Invoke() ?? false);
        return new Facts(Channel, s_ledger, s_updatedThisLaunch, Setup.WizardOpen.Peek(),
            Shell.Auth.Peek() == Shell.AuthState.SignInRequired, now - s_bootMs, s_shownThisRun, moment, s_seams.Now());
    }

    static WindowFact LiveWindow()
        => !FluentApp.WindowVisible ? WindowFact.Tray
         : IsIconic(FluentApp.WindowHandle) ? WindowFact.Minimized
         : InputHooks.Current.Default.IsWindowActive?.Invoke() == true ? WindowFact.Front : WindowFact.Behind;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(nint hwnd);

    // ══ 5. THE ENTRIES (the layer's effects and interval, and the inspector, call exactly these) ═══════════════════

    /// <summary>The ONE track-boundary entry: the layer's effect on Playback.CurrentId and `nudge fire=true`.</summary>
    internal static void OnTrackBoundary()
    {
        AccrueNow();
        s_trackChangedMs = s_seams.MonoMs();
    }

    internal static void OnPlaying(bool playing)
    {
        if (!playing) { AccrueNow(); return; }
        if (s_playMarkMs < 0) s_playMarkMs = s_seams.MonoMs();
    }

    /// <summary>Close the stretch now playing: App exit calls it (a session that ends mid-track still counts), and the
    /// inspector's `nudge` calls it before replying so the ledger it prints is current.</summary>
    internal static void Flush() => AccrueNow();

    static void AccrueNow()
    {
        if (s_playMarkMs < 0) return;
        long now = s_seams.MonoMs();
        double seconds = (now - s_playMarkMs) / 1000d;
        s_playMarkMs = Playback.IsPlaying.Peek() ? now : -1;
        Commit(Accrue(s_ledger, seconds, DateOnly.FromDateTime(s_seams.Now().LocalDateTime), Rules), null);
    }

    internal static void NoteTrouble(string source) => Commit(Troubled(s_ledger, s_seams.Now()), "trouble: " + source);

    /// <summary>Once a second from the layer (and `nudge tick=true`): tuck a due card, or evaluate while armed.</summary>
    internal static Decision? Tick(bool overlayOpen)
    {
        if (Shown.Peek() is { } card)
        {
            if (card.TuckDue(s_seams.MonoMs(), Rules)) Answer(Event.Ignored, Origin.Card);
            return null;
        }
        if (s_trackChangedMs is null) return null;
        return Evaluate(overlayOpen);
    }

    internal static Decision Evaluate(bool overlayOpen)
    {
        var d = Decide(Gather(overlayOpen), Rules);
        LastDecision = d;
        string blocker = Explain(d);
        if (blocker != s_lastBlocker) { s_lastBlocker = blocker; Log.Info(Cat, blocker); }
        if (d.Show)
        {
            s_shownThisRun = true;
            s_trackChangedMs = null;
            Commit(Apply(s_ledger, ShownEvent(d.Kind), Origin.Card, s_seams.Now()), "shown " + d.Ask + " " + d.Kind + " ask " + d.AskNumber);
            Shown.Value = CardFor(d, s_seams.MonoMs());
        }
        else if (!d.WaitingForMoment) s_trackChangedMs = null;      // nothing this track can change: stop checking
        return d;
    }

    internal static void Hold(bool on) { if (Shown.Peek() is { } c) Shown.Value = c.Hold(on, s_seams.MonoMs()); }

    // ══ 6. ACT: every button's verb ════════════════════════════════════════════════════════════════════════════════

    /// <summary>Rate or Star, from the card or a door. The card leaves as the Store dialog or the browser arrives.</summary>
    public static async Task PrimaryAsync(Origin origin)
    {
        Ask ask = origin == Origin.Card ? Shown.Peek()?.Ask ?? Ask.None : DoorAsk(Channel);
        if (origin == Origin.Card) Shown.Value = null;
        Event e;
        if (ask == Ask.Rate)
        {
            var answer = await s_seams.StoreDialog(FluentApp.WindowHandle, CancellationToken.None).ConfigureAwait(false);
            bool link = WantsLink(answer) && s_seams.Launch(ReviewUri(ReleaseNotes.Links.StoreIdOrFallback(Platform.Version.StoreId)));
            e = AfterStore(answer, link);
            Log.Info(Cat, "store: " + answer + (WantsLink(answer) ? (link ? ", review link opened" : ", review link failed") : ""));
        }
        else e = AfterLaunch(s_seams.Launch(ReleaseNotes.Links.RepoUrl));
        Post(() =>
        {
            Answer(e, origin);
            if (SaysThanks(e)) Notify.Say(Loc.Get(Strings.RateNudge.Thanks), dedupeKey: "rate-nudge:thanks");
        });
    }

    public static void Answer(Event e, Origin origin)
    {
        if (origin == Origin.Card && Shown.Peek() is not null) Shown.Value = null;
        Commit(Apply(s_ledger, e, origin, s_seams.Now()), e + " (" + origin + ")");
    }

    /// <summary>The Settings → About switch's writer. True: the page re-reads the switch's value from the ledger.</summary>
    public static bool SetEnabled(bool on) { Answer(on ? Event.SwitchedOn : Event.SwitchedOff, Origin.Door); return true; }

    // ══ 7. COMMIT: the one write ═══════════════════════════════════════════════════════════════════════════════════

    static void Commit(Ledger next, string? why)
    {
        if (next == s_ledger) return;
        s_ledger = next;
        Platform.Settings.Set(Platform.Keys.RateNudgeLedger, Encode(next));
        if (why is not null) Log.Info(Cat, why + ": " + Describe(next));
    }

    static void Post(Action a) { if (s_post is { } p) p(a); else a(); }

    // ══ 8. THE LIVE PLATFORM SEAMS ═════════════════════════════════════════════════════════════════════════════════

    static async Task<StoreAnswer> LiveStoreDialog(nint owner, CancellationToken ct)
    {
        var status = await StoreRating.RequestAsync(owner, ct).ConfigureAwait(false);
        return status switch
        {
            StoreRatingStatus.Succeeded => StoreAnswer.Succeeded,
            StoreRatingStatus.CanceledByUser => StoreAnswer.CanceledByUser,
            StoreRatingStatus.NetworkError => StoreAnswer.NetworkError,
            StoreRatingStatus.Error => StoreAnswer.Error,
            _ => StoreAnswer.Unavailable,
        };
    }

    static async Task<int?> LiveFetchStars(CancellationToken ct)
    {
        try
        {
            using var r = await ReleaseNotes.GitHubHttp.GetAsync(RepoApiUrl, ct).ConfigureAwait(false);
            if (!r.IsSuccessStatusCode) return null;
            return TryParseStars(await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false), out int n) ? n : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return null; }
    }

    /// <summary>Through the app's one external-open door (<c>Actions.Services.OpenExternal</c>, bound in App.BindOverlayServices
    /// to <c>hooks.OpenUri</c>). `ms-windows-store:` must pass it: some doors only let http(s) through.</summary>
    static bool LiveLaunch(string uri)
    {
        if (Actions.Services.OpenExternal is not { } open) return false;
        try { open(uri); return true; } catch (Exception ex) { Log.Warn(Cat, "open failed: " + uri, ex); return false; }
    }

    // ══ 9. THE INSPECTOR'S DOOR (Diagnostics.Inspect.Nudge.cs only) ════════════════════════════════════════════════

    internal static void ResetForInspect(Seams seams, Policy policy, Ledger ledger, InstallChannel channel, bool updatedThisLaunch,
        long sessionAgeMs, Stars? stars)
    {
        s_seams = seams; Rules = policy; Channel = channel; s_updatedThisLaunch = updatedThisLaunch; s_stars = stars;
        s_ledger = ledger; s_shownThisRun = false; s_trackChangedMs = null; s_playMarkMs = -1; s_lastBlocker = "";
        s_bootMs = seams.MonoMs() - sessionAgeMs;
        LastDecision = null;
        Shown.Value = null;
        Platform.Settings.Set(Platform.Keys.RateNudgeLedger, Encode(ledger));
    }

    internal static Ledger ReloadForInspect(IAppSettings settings)
    {
        string raw = settings.Get(Platform.Keys.RateNudgeLedger);
        if (!TryDecode(raw, out var l)) { Log.Warn(Cat, "unreadable ledger, starting fresh: '" + raw + "'", null); l = Seed(Ledger.Fresh, s_seams.Now(), false, Rules); }
        s_ledger = l;
        return l;
    }
}
