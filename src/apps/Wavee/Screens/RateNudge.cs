// ── Screens/RateNudge.cs ───────────────────────────────────────────────────────────────────────────────────────────
// the occasional "rate Wavee in the Microsoft Store / star it on GitHub" ask: EVERY rule it follows, as data and pure
// functions. Policy (the numbers) · Ledger (what this install remembers) · Facts (the raw readings of one instant) ·
// Decide (show or stay quiet, with the check behind the answer) · Apply (how an answer moves the ledger) · Seed /
// Accrue / Troubled (the counters) · Card (the shown card's clock) · AfterStore (what a Store answer means) · the route
// list, the session and surface classification · Encode / Decode (the one settings string).
//
// Role: CORE
// Owner: R
// Budget: 640 lines
// Spec: docs/plans/wavee/rate-nudge-implementation.md
//
// ENGINE-FREE BY CONSTRUCTION: System + BCL + three app enums (InstallChannel, Playback.Fault, Spotify.SessionFault). No FluentGpu type, no Loc lookup, no clock
// read, no settings, no I/O. Every instant comes in as a parameter, so one (Facts, Policy) is one Decision: the host acts
// on it, the log line prints it, the inspector's `nudge` verb replies with it. RateNudge.Host.cs, RateNudge.UI.cs and
// Diagnostics.Inspect.Nudge.cs make NO decision of their own: a branch on a Ledger, Policy or Moment field outside this
// file is a review finding.

using System.Collections.Immutable;
using System.Globalization;

namespace Wavee;

public static partial class RateNudge
{
    // ══ 1. POLICY: every number ════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Every number the feature uses. <see cref="Default"/> ships, milestones on (owner, 2026-11-20); the inspector
    /// passes <see cref="Plain"/> to drive the card without a milestone.</summary>
    public sealed record Policy
    {
        public int MinInstallDays { get; init; } = 14;       // from Ledger.ClockStart
        public int MinListenDays { get; init; } = 7;         // local days with at least ListenDayMinutes of playback
        public int ListenDayMinutes { get; init; } = 10;
        public double MinListenHours { get; init; } = 10;
        public int CooldownDays { get; init; } = 120;        // after a Not now, an ignored card or a cancelled Store dialog
        public double CooldownHours { get; init; } = 10;     // ... and this much more listening
        public int CalmDays { get; init; } = 7;              // after a crash, a playback failure or a sign-in fault
        public int RegularAsks { get; init; } = 2;           // per install, ever, at the milestones
        public int LaunchAsks { get; init; } = 1;            // the extra ask on this update, for installs that predate it (owner, 2026-11-20)
        public int MaxAsks => RegularAsks + LaunchAsks;      // 3
        public int LaunchWindowDays { get; init; } = 30;     // the launch ask lapses, uncounted, after this
        public int LaunchHoldMinutes { get; init; } = 60;    // in the update session itself, the launch ask waits this long
        public int IdleSeconds { get; init; } = 20;          // no press, key or wheel since max(last input, the track change)
        public int TuckSeconds { get; init; } = 20;          // an untouched card leaves; counts as Ignored
        public double MaxSliceSeconds { get; init; } = 6 * 3600;  // one accrual slice is never longer (sleep/resume guard)
        public ImmutableArray<double> Milestones { get; init; } = [25, 100];  // ask k waits for Milestones[k-1] hours; empty = off
        public int StarsRefreshHours { get; init; } = 24;    // the GitHub star count is fetched at most once a day
        public int StarsStaleDays { get; init; } = 7;        // an older cached count is not shown

        public static readonly Policy Default = new();
        public static readonly Policy Plain = new() { Milestones = [] };
    }

    // ══ 2. WHAT THE CARD AND THE DOORS ASK FOR ═════════════════════════════════════════════════════════════════════

    public enum Ask : byte { None, Rate, Star }

    /// <summary>The card: a Store install is asked for a Store rating, a GitHub install for a star, a source build never.</summary>
    public static Ask CardAsk(InstallChannel c) => c switch { InstallChannel.Store => Ask.Rate, InstallChannel.Sideload => Ask.Star, _ => Ask.None };

    /// <summary>The doors: every build has one, and only a Store build rates.</summary>
    public static Ask DoorAsk(InstallChannel c) => c == InstallChannel.Store ? Ask.Rate : Ask.Star;

    // ══ 3. THE LEDGER: what this install remembers ═════════════════════════════════════════════════════════════════

    /// <summary>How the last ask ended. (The plan calls this enum Answer; that name is the host's answer verb, so it is Outcome.)</summary>
    public enum Outcome : byte { None, Rated, LinkOpened, Canceled, OpenFailed, NotNow, Ignored, NeverAgain }

    /// <summary>The launch ask: None on a new install (it never gets one), Pending on an install that predates this
    /// release until it is shown or its window closes, Shown once it was.</summary>
    public enum LaunchState : byte { None, Pending, Shown }

    /// <summary>Which ask comes next: the launch ask while it is pending and open, else a regular (milestone) ask.</summary>
    public enum Kind : byte { Regular, Launch }

    /// <summary>Everything the feature remembers, persisted as ONE settings string (<see cref="Encode"/>), so a write is
    /// atomic and the inspector can read it back whole.</summary>
    public sealed record Ledger
    {
        public DateTimeOffset? ClockStart { get; init; }     // first run, or (an older install) the day it updated to this release
        public DateTimeOffset? CountedSince { get; init; }   // when the listening counters started: the milestone's "since"
        public int Asks { get; init; }                       // cards shown that counted (an OpenFailed one is refunded)
        public DateTimeOffset? LastAsk { get; init; }
        public double HoursAtLastAsk { get; init; }
        public Outcome Last { get; init; }
        public bool Done { get; init; }                      // rated, or a review page / the repo was opened: never again
        public bool Enabled { get; init; } = true;           // Settings → About; Don't ask again turns it off
        public double ListenSeconds { get; init; }
        public int ListenDays { get; init; }
        public DateOnly? ListenDay { get; init; }            // the local day ListenDaySeconds belongs to
        public double ListenDaySeconds { get; init; }
        public DateTimeOffset? LastTrouble { get; init; }
        public LaunchState Launch { get; init; }
        public DateTimeOffset? LaunchBy { get; init; }       // the launch ask's window closes here

        public double ListenHours => ListenSeconds / 3600d;
        public static readonly Ledger Fresh = new();
    }

    /// <summary>Regular asks shown so far: every ask but the launch one.</summary>
    public static int RegularAsksShown(Ledger l) => l.Asks - (l.Launch == LaunchState.Shown ? 1 : 0);

    /// <summary>This install's lifetime cap: 3 when it predates this release (the launch ask), 2 when it doesn't.</summary>
    public static int CapFor(Ledger l, Policy p) => p.RegularAsks + (l.Launch == LaunchState.None ? 0 : p.LaunchAsks);

    public static Kind NextKind(Ledger l, DateTimeOffset now)
        => l.Launch == LaunchState.Pending && l.LaunchBy is { } by && now <= by ? Kind.Launch : Kind.Regular;

    // ══ 4. FACTS: the raw readings of one instant (the host reads, this file classifies) ═══════════════════════════

    public enum Session : byte { Ordinary, AfterUpdate, FirstRun }
    public enum Surface : byte { Browse, Other, Stage, Video, FullScreen }
    public enum WindowFact : byte { Front, Behind, Minimized, Tray }

    /// <summary>One instant. Milliseconds are on the host's monotonic clock (the inspector's fixed frames drive it).</summary>
    public readonly record struct Moment(
        long? SinceTrackChangeMs,     // null: no track boundary armed in this run
        long IdleMs,                  // since the last press, key or wheel; -1 = none this run
        WindowFact Window,
        bool SearchFocused,
        bool OverlayOpen,             // a dialog, flyout, menu or popup (never the card itself, which is not an overlay)
        string RouteKey,              // Shell's route key: "home", "album", "settings" …
        bool StageOpen,
        bool VideoUp,                 // video detached, full screen or the main host's large surface
        bool WindowFullScreen);

    public readonly record struct Facts(
        InstallChannel Channel, Ledger Ledger, bool UpdatedThisLaunch, bool WizardOpen, bool SignInRequired,
        long SessionAgeMs, bool ShownThisRun, Moment Moment, DateTimeOffset Now);

    /// <summary>The pages the card may appear over: places you browse, never a place you type, configure or read notes.
    /// These are the keys <c>Shell.RouteKeyOf</c> returns (the s_routes key column, entity prefixes without their colon).</summary>
    public static readonly ImmutableHashSet<string> BrowseRoutes = ImmutableHashSet.Create(StringComparer.Ordinal,
        "home", "browse", "albums", "artists", "playlists", "podcasts", "audiobooks", "liked", "local", "history", "recents",
        "album", "pl", "artist", "show", "episode");

    public static Surface SurfaceOf(in Moment m)
        => m.WindowFullScreen ? Surface.FullScreen
         : m.StageOpen ? Surface.Stage
         : m.VideoUp ? Surface.Video
         : BrowseRoutes.Contains(m.RouteKey) ? Surface.Browse : Surface.Other;

    public static Session SessionOf(bool updatedThisLaunch, bool wizardOpen, bool signInRequired)
        => wizardOpen || signInRequired ? Session.FirstRun : updatedThisLaunch ? Session.AfterUpdate : Session.Ordinary;

    /// <summary>A track boundary: one playing track replaced by another. The first track of a run is not a boundary.</summary>
    public static bool IsTrackBoundary(string? previous, string? next)
        => previous is { Length: > 0 } && next is { Length: > 0 } && !string.Equals(previous, next, StringComparison.Ordinal);

    /// <summary>What restarts the calm week: a playback fault that is Wavee's (never the listener's own network dropping), and a sign-in
    /// fault that is a real failure (never "not signed in yet", never a connectivity drop). A delay, never an exclusion.</summary>
    public static bool IsTrouble(Playback.Fault f) => f is not (Playback.Fault.None or Playback.Fault.Network);

    public static bool IsTrouble(Spotify.SessionFault f)
        => f is not (Spotify.SessionFault.None or Spotify.SessionFault.NoCredential or Spotify.SessionFault.Network);

    // ══ 5. DECIDE ══════════════════════════════════════════════════════════════════════════════════════════════════

    public enum Stage : byte { Who, When, Run, Now }

    public enum Rule : byte
    {
        Channel, Enabled, NotDone, UnderCap,                                  // Who: may this install ever be asked again?
        LaunchWindow,                                                         // When (launch ask): still within its window?
        InstallAge, ListenDays, ListenHours, Milestone, Cooldown, CalmWeek,   // When (regular ask): has enough happened?
        OrdinarySession, OncePerRun,                                          // Run: may this session be interrupted?
        AfterTrackChange, Idle, Foreground, NoTyping, NoOverlay, BrowseSurface, // Now: is this instant quiet?
    }

    public static Stage StageOf(Rule r)
        => r <= Rule.UnderCap ? Stage.Who : r <= Rule.CalmWeek ? Stage.When : r <= Rule.OncePerRun ? Stage.Run : Stage.Now;

    /// <summary>One rule's verdict. <see cref="Detail"/> is English for the log and the inspector, never shown to a user.</summary>
    public readonly record struct Check(Rule Rule, bool Pass, string Detail)
    {
        public Stage Stage => StageOf(Rule);
    }

    public sealed record Decision(Ask Ask, Kind Kind, int AskNumber, double? Milestone, ImmutableArray<Check> Checks, DateTimeOffset? Earliest)
    {
        public bool Show => FirstFail(Stage.Now) is null;
        public Check? Blocker => FirstFail(Stage.Now);
        /// <summary>A Who rule failed: this install keeps only the doors.</summary>
        public bool Closed => FirstFail(Stage.Who) is not null;
        /// <summary>Everything before the instant passes: the host arms its once-a-second check only then.</summary>
        public bool WaitingForMoment => FirstFail(Stage.Run) is null && !Show;

        Check? FirstFail(Stage upTo)
        {
            foreach (var c in Checks) if (c.Stage <= upTo && !c.Pass) return c;
            return null;
        }
    }

    public static Decision Decide(in Facts f, Policy p)
    {
        var l = f.Ledger;
        var m = f.Moment;
        var ask = CardAsk(f.Channel);
        var kind = NextKind(l, f.Now);
        var c = ImmutableArray.CreateBuilder<Check>(19);

        // Who
        c.Add(new Check(Rule.Channel, ask != Ask.None, f.Channel switch
        {
            InstallChannel.Store => "Store install: asks for a Store rating",
            InstallChannel.Sideload => "GitHub install: asks for a star",
            _ => "source build: doors only",
        }));
        c.Add(new Check(Rule.Enabled, l.Enabled, l.Enabled ? "on" : "switched off (Don't ask again, or Settings)"));
        c.Add(new Check(Rule.NotDone, !l.Done, l.Done ? "done: " + l.Last : "not rated or starred"));
        int cap = CapFor(l, p), regular = RegularAsksShown(l);
        c.Add(kind == Kind.Launch
            ? new Check(Rule.UnderCap, l.Asks < cap, $"the launch ask: ask {l.Asks + 1} of {cap}")
            : new Check(Rule.UnderCap, regular < p.RegularAsks && l.Asks < cap, $"asked {l.Asks} of {cap} ({regular} of {p.RegularAsks} regular)"));

        // When: the launch ask only checks its window (these people already use Wavee; the counters start today)
        double? milestone = null;
        if (kind == Kind.Launch)
            c.Add(new Check(Rule.LaunchWindow, true, $"the launch ask is open until {l.LaunchBy:yyyy-MM-dd}"));
        else
        {
            double age = l.ClockStart is { } s ? (f.Now - s).TotalDays : 0;
            c.Add(new Check(Rule.InstallAge, age >= p.MinInstallDays, $"day {Days(age)} of {p.MinInstallDays}"));
            c.Add(new Check(Rule.ListenDays, l.ListenDays >= p.MinListenDays, $"{l.ListenDays} of {p.MinListenDays} listening days"));
            c.Add(new Check(Rule.ListenHours, l.ListenHours >= p.MinListenHours, $"{Hours(l.ListenHours)} of {Hours(p.MinListenHours)} h"));
            milestone = MilestoneFor(l, p);
            if (milestone is { } ms)
                c.Add(new Check(Rule.Milestone, l.ListenHours >= ms, $"{Hours(l.ListenHours)} of the {Hours(ms)} h milestone"));
            if (l.LastAsk is { } last)
            {
                double days = (f.Now - last).TotalDays, since = l.ListenHours - l.HoursAtLastAsk;
                c.Add(new Check(Rule.Cooldown, days >= p.CooldownDays && since >= p.CooldownHours,
                    $"{Days(days)} of {p.CooldownDays} days and {Hours(since)} of {Hours(p.CooldownHours)} h since ask {l.Asks}"));
            }
        }
        double calm = l.LastTrouble is { } t ? (f.Now - t).TotalDays : double.PositiveInfinity;
        c.Add(new Check(Rule.CalmWeek, calm >= p.CalmDays,
            double.IsPositiveInfinity(calm) ? "no trouble on record" : $"trouble {Days(calm)} days ago, waits {p.CalmDays}"));

        // Run
        var session = SessionOf(f.UpdatedThisLaunch, f.WizardOpen, f.SignInRequired);
        long hold = p.LaunchHoldMinutes * 60_000L;
        bool launchInUpdate = session == Session.AfterUpdate && kind == Kind.Launch;
        c.Add(new Check(Rule.OrdinarySession, session == Session.Ordinary || (launchInUpdate && f.SessionAgeMs >= hold), session switch
        {
            Session.AfterUpdate when kind == Kind.Launch =>
                $"the update session: {f.SessionAgeMs / 60_000} of {p.LaunchHoldMinutes} min before the launch ask",
            Session.AfterUpdate => "the first session after an update belongs to What's new",
            Session.FirstRun => "first run or signing in",
            _ => "ordinary session",
        }));
        c.Add(new Check(Rule.OncePerRun, !f.ShownThisRun, f.ShownThisRun ? "already shown this run" : "not shown this run"));

        // Now
        long since0 = m.SinceTrackChangeMs ?? -1;
        c.Add(new Check(Rule.AfterTrackChange, since0 >= 0, since0 < 0 ? "no track change armed" : $"{Secs(since0)} s into the track"));
        long idle = since0 < 0 ? 0 : m.IdleMs < 0 ? since0 : Math.Min(m.IdleMs, since0);
        c.Add(new Check(Rule.Idle, idle >= p.IdleSeconds * 1000L, $"idle {Secs(idle)} of {p.IdleSeconds} s"));
        c.Add(new Check(Rule.Foreground, m.Window == WindowFact.Front, m.Window switch
        {
            WindowFact.Front => "Wavee is in front", WindowFact.Tray => "hidden in the tray",
            WindowFact.Minimized => "minimized", _ => "behind another window",
        }));
        c.Add(new Check(Rule.NoTyping, !m.SearchFocused, m.SearchFocused ? "the search box has focus" : "nothing being typed"));
        c.Add(new Check(Rule.NoOverlay, !m.OverlayOpen, m.OverlayOpen ? "a dialog, flyout or menu is open" : "no overlay"));
        var surface = SurfaceOf(m);
        c.Add(new Check(Rule.BrowseSurface, surface == Surface.Browse, surface switch
        {
            Surface.Browse => "a browse page (" + m.RouteKey + ")", Surface.Stage => "the full-screen stage is up",
            Surface.Video => "video is up", Surface.FullScreen => "the window is full screen",
            _ => "not a browse page (" + m.RouteKey + ")",
        }));

        return new Decision(ask, kind, l.Asks + 1, milestone, c.ToImmutable(), Earliest(l, p));
    }

    /// <summary>The milestone the next REGULAR ask waits for (regular ask 1 → Milestones[0], 2 → Milestones[1]); the launch
    /// ask has none. Null with milestones off.</summary>
    public static double? MilestoneFor(Ledger l, Policy p)
        => p.Milestones.IsDefaultOrEmpty ? null : p.Milestones[Math.Clamp(RegularAsksShown(l), 0, p.Milestones.Length - 1)];

    /// <summary>When the dated When rules (install age, cooldown days, calm week) all pass. Listening can't be dated.</summary>
    public static DateTimeOffset? Earliest(Ledger l, Policy p)
    {
        DateTimeOffset? e = null;
        if (l.ClockStart is { } s) e = Later(e, s.AddDays(p.MinInstallDays));
        if (l.LastAsk is { } a) e = Later(e, a.AddDays(p.CooldownDays));
        if (l.LastTrouble is { } t) e = Later(e, t.AddDays(p.CalmDays));
        return e;

        static DateTimeOffset? Later(DateTimeOffset? x, DateTimeOffset y) => x is { } v && v > y ? v : y;
    }

    // ══ 6. APPLY: the one transition table ═════════════════════════════════════════════════════════════════════════

    public enum Origin : byte { Card, Door }
    public enum Event : byte { Shown, ShownLaunch, Rated, LinkOpened, Canceled, OpenFailed, NotNow, Ignored, NeverAgain, SwitchedOff, SwitchedOn }

    public static Event ShownEvent(Kind k) => k == Kind.Launch ? Event.ShownLaunch : Event.Shown;

    /// <summary>A card answer closes the ask that <see cref="Event.Shown"/> opened. A door is the user's own idea: only a
    /// completed rating or an opened page changes anything there, and a cancel or a failure from a door changes nothing.</summary>
    public static Ledger Apply(Ledger l, Event e, Origin o, DateTimeOffset now) => (e, o) switch
    {
        (Event.Shown, Origin.Card) => l with { Asks = l.Asks + 1, LastAsk = now, HoursAtLastAsk = l.ListenHours, Last = Outcome.None },
        (Event.ShownLaunch, Origin.Card) => l with { Asks = l.Asks + 1, LastAsk = now, HoursAtLastAsk = l.ListenHours, Last = Outcome.None, Launch = LaunchState.Shown },
        (Event.Rated, _) => l with { Done = true, Last = Outcome.Rated },
        (Event.LinkOpened, _) => l with { Done = true, Last = Outcome.LinkOpened },
        (Event.Canceled, Origin.Card) => l with { Last = Outcome.Canceled },
        // A regular ask that could open nothing is refunded (LastAsk keeps the cooldown). The launch ask is not: its moment
        // has passed (Launch == Shown and it is the only ask shown so far).
        (Event.OpenFailed, Origin.Card) => l.Launch == LaunchState.Shown && l.Asks == 1
            ? l with { Last = Outcome.OpenFailed }
            : l with { Asks = Math.Max(0, l.Asks - 1), Last = Outcome.OpenFailed },
        (Event.NotNow, Origin.Card) => l with { Last = Outcome.NotNow },
        (Event.Ignored, Origin.Card) => l with { Last = Outcome.Ignored },
        (Event.NeverAgain, Origin.Card) => l with { Enabled = false, Last = Outcome.NeverAgain },
        (Event.SwitchedOff, _) => l with { Enabled = false },
        (Event.SwitchedOn, _) => l with { Enabled = true },
        _ => l,
    };

    /// <summary>The only event that earns a thank-you toast: Wavee learns nothing else (never the stars).</summary>
    public static bool SaysThanks(Event e) => e == Event.Rated;

    // ══ 7. THE STORE'S ANSWER ══════════════════════════════════════════════════════════════════════════════════════

    public enum StoreAnswer : byte { Succeeded, CanceledByUser, NetworkError, Error, Unavailable }

    /// <summary>Every failure tries the review link before giving up; <paramref name="linkOpened"/> is whether it launched.</summary>
    public static Event AfterStore(StoreAnswer a, bool linkOpened) => a switch
    {
        StoreAnswer.Succeeded => Event.Rated,
        StoreAnswer.CanceledByUser => Event.Canceled,
        _ => linkOpened ? Event.LinkOpened : Event.OpenFailed,
    };

    public static bool WantsLink(StoreAnswer a) => a is not (StoreAnswer.Succeeded or StoreAnswer.CanceledByUser);
    public static string ReviewUri(string storeId) => "ms-windows-store://review/?ProductId=" + storeId;
    public const string RepoApiUrl = "https://api.github.com/repos/christosk92/WaveeMusic";
    public static Event AfterLaunch(bool opened) => opened ? Event.LinkOpened : Event.OpenFailed;

    // ══ 8. THE COUNTERS ════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The first read on this install. Every install starts its clock and its counters now. One that predates this
    /// release gets the launch ask, open for LaunchWindowDays (owner, 2026-11-20: existing users are asked on this update;
    /// this replaces the earlier 0-30 day rollout jitter). A new install never gets it.</summary>
    public static Ledger Seed(Ledger l, DateTimeOffset now, bool predatesFeature, Policy p)
        => l.ClockStart is not null ? l
         : l with
         {
             ClockStart = now,
             CountedSince = now,
             Launch = predatesFeature ? LaunchState.Pending : LaunchState.None,
             LaunchBy = predatesFeature ? now.AddDays(p.LaunchWindowDays) : null,
         };

    /// <summary>A played stretch. A local day counts as a listening day once ListenDayMinutes of it played, once.</summary>
    public static Ledger Accrue(Ledger l, double seconds, DateOnly day, Policy p)
    {
        if (!(seconds > 0)) return l;
        seconds = Math.Min(seconds, p.MaxSliceSeconds);
        double before = l.ListenDay == day ? l.ListenDaySeconds : 0, after = before + seconds, need = p.ListenDayMinutes * 60d;
        bool crossed = before < need && after >= need;
        return l with
        {
            ListenSeconds = l.ListenSeconds + seconds, ListenDay = day, ListenDaySeconds = after,
            ListenDays = l.ListenDays + (crossed ? 1 : 0),
        };
    }

    /// <summary>A crash at the last launch, a playback failure or a sign-in fault: the calm week restarts. A delay, never
    /// an exclusion (FTC review rule: nobody is screened out by how they feel).</summary>
    public static Ledger Troubled(Ledger l, DateTimeOffset at) => l.LastTrouble is { } t && t >= at ? l : l with { LastTrouble = at };

    // ══ 9. THE SHOWN CARD'S CLOCK ══════════════════════════════════════════════════════════════════════════════════

    /// <summary>A shown card. Hover or keyboard focus holds its clock; an untouched card is due to tuck away.</summary>
    public readonly record struct Card(Ask Ask, int AskNumber, double? Milestone, long ShownMs, long HeldMs, long? HoldSinceMs)
    {
        public Card Hold(bool on, long nowMs)
        {
            if (on) return HoldSinceMs is null ? this with { HoldSinceMs = nowMs } : this;
            return HoldSinceMs is { } h ? this with { HeldMs = HeldMs + (nowMs - h), HoldSinceMs = null } : this;
        }

        public long UntouchedMs(long nowMs) => nowMs - ShownMs - HeldMs - (HoldSinceMs is { } h ? nowMs - h : 0);
        public bool TuckDue(long nowMs, Policy p) => HoldSinceMs is null && UntouchedMs(nowMs) >= p.TuckSeconds * 1000L;
    }

    public static Card CardFor(Decision d, long nowMs) => new(d.Ask, d.AskNumber, d.Milestone, nowMs, 0, null);

    // ══ 10. THE ONE SETTINGS STRING ════════════════════════════════════════════════════════════════════════════════

    const string V1 = "1";

    public static string Encode(Ledger l) => string.Join('|',
        V1, Ms(l.ClockStart), l.Asks.ToString(CultureInfo.InvariantCulture), Ms(l.LastAsk), Num(l.HoursAtLastAsk),
        ((int)l.Last).ToString(CultureInfo.InvariantCulture), l.Done ? "1" : "0", l.Enabled ? "1" : "0", Num(l.ListenSeconds),
        l.ListenDays.ToString(CultureInfo.InvariantCulture), l.ListenDay?.DayNumber.ToString(CultureInfo.InvariantCulture) ?? "",
        Num(l.ListenDaySeconds), Ms(l.LastTrouble), Ms(l.CountedSince),
        ((int)l.Launch).ToString(CultureInfo.InvariantCulture), Ms(l.LaunchBy));

    /// <summary>False on anything but a whole v1 record; the host then logs the raw value and starts fresh.</summary>
    public static bool TryDecode(string? raw, out Ledger ledger)
    {
        ledger = Ledger.Fresh;
        var f = (raw ?? "").Split('|');
        if (f.Length != 16 || f[0] != V1) return false;
        if (!TryMs(f[1], out var clock) || !TryInt(f[2], out int asks) || !TryMs(f[3], out var lastAsk)
            || !TryNum(f[4], out double atAsk) || !TryInt(f[5], out int last) || !Enum.IsDefined((Outcome)last)
            || f[6] is not ("0" or "1") || f[7] is not ("0" or "1") || !TryNum(f[8], out double secs)
            || !TryInt(f[9], out int days) || !TryDay(f[10], out var day) || !TryNum(f[11], out double daySecs)
            || !TryMs(f[12], out var trouble) || !TryMs(f[13], out var since)
            || !TryInt(f[14], out int launch) || !Enum.IsDefined((LaunchState)launch) || !TryMs(f[15], out var launchBy)) return false;
        ledger = new Ledger
        {
            ClockStart = clock, Asks = asks, LastAsk = lastAsk, HoursAtLastAsk = atAsk, Last = (Outcome)last,
            Done = f[6] == "1", Enabled = f[7] == "1", ListenSeconds = secs, ListenDays = days, ListenDay = day,
            ListenDaySeconds = daySecs, LastTrouble = trouble, CountedSince = since,
            Launch = (LaunchState)launch, LaunchBy = launchBy,
        };
        return true;
    }

    // ══ 11. THE GITHUB STAR COUNT (GitHub installs only; owner, 2026-11-20) ════════════════════════════════════════

    /// <summary>The last count GitHub gave, and when. Its own settings string (`count|ms`): it is a cache, not memory.</summary>
    public readonly record struct Stars(int Count, DateTimeOffset FetchedAt);

    /// <summary>Fetch only for an install that can see a star (never a Store install), at most once a day.</summary>
    public static bool StarsDue(InstallChannel c, Stars? cached, DateTimeOffset now, Policy p)
        => DoorAsk(c) == Ask.Star && (cached is not { } s || (now - s.FetchedAt).TotalHours >= p.StarsRefreshHours);

    /// <summary>The count to show, or null: never on a Store install, never from a cache older than StarsStaleDays.</summary>
    public static int? StarsShown(InstallChannel c, Stars? cached, DateTimeOffset now, Policy p)
        => DoorAsk(c) == Ask.Star && cached is { } s && (now - s.FetchedAt).TotalDays <= p.StarsStaleDays ? s.Count : null;

    /// <summary>`stargazers_count` from the GET /repos/{owner}/{repo} body; false on anything else.</summary>
    public static bool TryParseStars(string json, out int count)
    {
        count = 0;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("stargazers_count", out var v) && v.TryGetInt32(out count) && count >= 0;
        }
        catch (System.Text.Json.JsonException) { return false; }
    }

    public static string EncodeStars(Stars s) => s.Count.ToString(CultureInfo.InvariantCulture) + "|" + Ms(s.FetchedAt);

    public static Stars? DecodeStars(string? raw)
    {
        var f = (raw ?? "").Split('|');
        if (f.Length == 2 && TryInt(f[0], out int n) && TryMs(f[1], out var at) && at is { } t) return new Stars(n, t);
        return null;
    }

    /// <summary>The log line and the inspector's `ledger` text, one form for both.</summary>
    public static string Describe(Ledger l)
        => $"asks={l.Asks} last={l.Last} done={l.Done} enabled={l.Enabled} hours={Hours(l.ListenHours)} days={l.ListenDays}"
         + $" clock={l.ClockStart:yyyy-MM-dd} since={l.CountedSince:yyyy-MM-dd} lastAsk={l.LastAsk:yyyy-MM-dd} trouble={l.LastTrouble:yyyy-MM-dd}"
         + $" launch={l.Launch} launchBy={l.LaunchBy:yyyy-MM-dd}";

    public static string Explain(Decision d)
        => d.Blocker is { } b ? $"quiet: {b.Rule} ({b.Detail})" : $"show {d.Ask} {d.Kind} ask {d.AskNumber}";

    // ── formatting and parsing helpers (invariant) ──
    static string Days(double d) => Math.Floor(d).ToString(CultureInfo.InvariantCulture);
    static string Hours(double h) => h.ToString("0.#", CultureInfo.InvariantCulture);
    static string Secs(long ms) => (ms / 1000).ToString(CultureInfo.InvariantCulture);
    static string Num(double v) => v.ToString("R", CultureInfo.InvariantCulture);
    static string Ms(DateTimeOffset? t) => t?.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) ?? "";
    static bool TryInt(string s, out int v) => int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out v) && v >= 0;
    static bool TryNum(string s, out double v) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && v >= 0;
    static bool TryMs(string s, out DateTimeOffset? t)
    {
        t = null;
        if (s.Length == 0) return true;
        if (!long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out long ms)) return false;
        t = DateTimeOffset.FromUnixTimeMilliseconds(ms);
        return true;
    }
    static bool TryDay(string s, out DateOnly? d)
    {
        d = null;
        if (s.Length == 0) return true;
        if (!int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out int n)) return false;
        d = DateOnly.FromDayNumber(n);
        return true;
    }
}
