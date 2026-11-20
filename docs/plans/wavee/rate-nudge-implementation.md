# Rate & star nudge: implementation

Concept, research and the clickable mockups: https://claude.ai/artifact/NUT3nvxVrpVMJdWhCncjAb (private; the owner shares it).
Base: `release/0.3.13`. Paired engine change in `fluent-gpu` (§7). Change file: `changes/unreleased/rate-nudge.md`.

What ships: the **quiet doors** for every install (Settings → About, the account menu, the after-update plate's footer)
and **one corner card** that asks a Store install for a Store rating and a GitHub install for a star, at a quiet track
change. An install that existed before this release gets a **launch ask** on the update that ships it; every install then
gets up to two **regular asks** at listening milestones (25 h, then 100 h). So at most three asks for an existing install,
two for a new one. Source builds get the doors only.

**Decided by the owner, 2026-11-20:**

| # | Question | Answer |
|---|---|---|
| 1 | Thresholds | As proposed: 14 days, 7 listening days, 10 h, 120 days (the cap became 3 with decision 8) |
| 2 | Copy voice | First person: "I build Wavee on my own…" |
| 3 | Live star count on the GitHub card | Show it (one unauthenticated GitHub call a day, cached: `RefreshStarsIfDue`, §4) |
| 4 | Milestones (concept B) | Ship them: `Policy.Default` carries `Milestones = [25, 100]` |
| 5 | Mention GitHub to Store installs | No. A Store install never sees the repo, a star or a star count |
| 6 | Classic | Classic follows WinUI's style, so its doors are the same as WinUI's: the About door is its own clickable card (§6), and the after-update plate gets the same footer line |
| 7 | `expect=` on the `nudge` inspector verb | Keep it: a wrong result is `ok:false`, so `inspect.py --gate` fails the run, as `lint` and `crossing` do. The scenario lines live in this document, never in a test file |
| 8 | Max asks | **3.** Existing installs are asked on this update (the launch ask); new installs are not. Then the two regular milestone asks for everyone. This replaces the rollout stagger (the 0-30 day jitter): existing users are meant to be asked now |

How the launch ask reads the other rules (my reading of decision 8, each a `Policy` number):

- It skips install age, listening days, listening hours, the milestone and the cooldown: these people already use Wavee,
  and the counters only start today. The calm week and every quiet-moment rule still apply.
- In the update session itself it waits until What's new is closed and the app has been open `LaunchHoldMinutes` (60);
  otherwise it comes at the next launch. Regular asks still never use the update session.
- It lapses, uncounted, if no quiet moment came within `LaunchWindowDays` (30). The regular asks then follow as usual.
- A Not now or an ignored launch ask starts the usual 120-day cooldown before the first regular ask.

Nothing is open.

---

## 1. Architecture: one class, every rule in one file

`RateNudge` is one `public static partial class`, the house pattern (`Feedback`, `ReleaseNotes`). Every rule lives in
**`RateNudge.cs`**, which is pure. The other partials only read facts in and carry results out.

```
 the live app                                   RateNudge.cs   CORE · engine-free · every rule
 ───────────────────────────────                ─────────────────────────────────────────────────────────────
 Playback.CurrentId / IsPlaying ──┐             Policy    every number (14 days, 7 days, 10 h, 120 days, 1 launch + 2 regular asks …)
 FluentApp.MsSinceInteraction ────┤             Ledger    what this install remembers (ONE settings string)
 window: visible / iconic / active┤             Facts     raw readings of one instant (Moment + session + ledger)
 Shell.Current / Stage / Video ───┤  Host       Decide()  Facts → Decision { Show, Ask, Kind, AskNumber, Checks[], Earliest }
 Overlay.AnyOpen / search focus ──┼─ Gather ──► Apply()   Ledger × Event → Ledger   (the one transition table)
 Update.Host.UpdatedFrom / wizard ┤             Seed / Accrue / Troubled            (the counters)
 Crash / Playback.Error / Spotify ┘             Card      the shown card's clock (hold on hover, tuck when untouched)
                                                AfterStore(StoreAnswer) → Event · Encode/Decode · SurfaceOf · SessionOf
        ▲                       │
        │ Seams (Now, MonoMs,   ▼ Decision / Event
        │ IdleMs, Window,     RateNudge.Host.cs   SHELL · Gather, Commit, Act, Tick · decides nothing
        │ StoreDialog, Launch)  │
        │                       ▼
 Diagnostics.Inspect.Nudge.cs  RateNudge.UI.cs     [CoreHost] layer: effects + 1 s interval → Slots.View(card)
 the `nudge` verb: fixtures,   Themes/Common/RateNudgeCard.cs   the picture (all three themes register it)
 fakes, expect= gates          FluentGpu.WindowsApi/Store/StoreRating.cs   the Store dialog (engine, §7)
```

**The rule that keeps it in one file:** outside `RateNudge.cs`, no code branches on a `Ledger`, `Policy`, `Moment` or
`Facts` field. The host branches only on a `Decision`, an `Event`, or a seam's result. A reviewer can check this with one
grep: `Ledger\.|\.Asks|\.Enabled|\.Done|LastAsk|ListenHours|Policy\.` outside `RateNudge.cs` must only appear in
`Commit`/`Describe` calls and in the inspector's fixture writer.

Why this shape:

- **Functional core, thin shell.** `Decide` and `Apply` are total functions of their inputs. Time, the window, the Store
  and the browser reach the core only as values, so the same inputs always give the same answer, in the app, in the
  log and in the inspector.
- **One transition table.** Every way the ledger can change is one arm of `Apply`'s switch: the card's three buttons, the
  Store's four answers, the doors, the switch. There is no second place that writes `Asks` or `Done`.
- **Every answer explains itself.** A `Decision` carries every check that applies to its kind of ask (a launch ask skips the listening rules; Milestone and Cooldown appear only when they apply) in a fixed order with their numbers, so "why
  didn't it ask?" is one log line or one inspector reply, never a debugging session.
- **The inspector drives the real entries.** `nudge fire=true` calls the same `OnTrackBoundary` the playback effect
  calls; buttons are clicked with `click`; doors run through `setting`; the stage, video, dialogs and search box are the
  real ones (`mount stage=`, `mount video=`, `dialog`, `click`). The only fakes are the four things a headless run cannot
  have: the calendar, the window being in front, the Store dialog and the browser. They are swapped as one `Seams` record.
- **Persistence is one atomic string** with a version tag. A corrupt value is logged and starts fresh, never a crash.

| File | Role | Budget | Holds |
|---|---|---|---|
| `src/apps/Wavee/Screens/RateNudge.cs` | CORE | 640 | §2: every rule, the ledger encoding, the route list, the session and surface classification, the star-count rules |
| `src/apps/Wavee/Screens/RateNudge.Host.cs` | SHELL | 260 | §4: seams, boot, gather, the track-boundary entry, accrual, `Act`, `Commit`, logging |
| `src/apps/Wavee/Screens/RateNudge.UI.cs` | UI | 160 | §5: the `[CoreHost]` layer, props, resolved copy, the door verbs |
| `src/apps/Wavee/Theming/Contracts/RateNudgeProps.cs` + `ViewKeys.Shell.cs` line | contract | 50 | the props record and `ViewKeys.ShellRateNudge` |
| `src/apps/Wavee/Themes/Common/RateNudgeCard.cs` + `Common/Rules/RateNudgeRules.cs` | picture | 170 | the card, its numbers |
| `src/apps/Wavee/Screens/Diagnostics.Inspect.Nudge.cs` | SHELL | 300 | §8: the `nudge` verb, fixtures, fakes, `expect=` |
| `fluent-gpu/src/FluentGpu.WindowsApi/Store/StoreRating.cs` | engine | 220 | §7: `RequestRateAndReviewAppAsync` without CsWinRT |

---

## 2. `RateNudge.cs`: the rules (full code)

```csharp
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
// ENGINE-FREE BY CONSTRUCTION: System + BCL + the app's InstallChannel enum. No FluentGpu type, no Loc lookup, no clock
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

    public enum Answer : byte { None, Rated, LinkOpened, Canceled, OpenFailed, NotNow, Ignored, NeverAgain }

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
        public Answer Last { get; init; }
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

    /// <summary>The pages the card may appear over: places you browse, never a place you type, configure or read notes.</summary>
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
        c.Add(new(Rule.Channel, ask != Ask.None, f.Channel switch
        {
            InstallChannel.Store => "Store install: asks for a Store rating",
            InstallChannel.Sideload => "GitHub install: asks for a star",
            _ => "source build: doors only",
        }));
        c.Add(new(Rule.Enabled, l.Enabled, l.Enabled ? "on" : "switched off (Don't ask again, or Settings)"));
        c.Add(new(Rule.NotDone, !l.Done, l.Done ? "done: " + l.Last : "not rated or starred"));
        int cap = CapFor(l, p), regular = RegularAsksShown(l);
        c.Add(kind == Kind.Launch
            ? new(Rule.UnderCap, l.Asks < cap, $"the launch ask: ask {l.Asks + 1} of {cap}")
            : new(Rule.UnderCap, regular < p.RegularAsks && l.Asks < cap, $"asked {l.Asks} of {cap} ({regular} of {p.RegularAsks} regular)"));

        // When: the launch ask only checks its window (these people already use Wavee; the counters start today)
        double? milestone = null;
        if (kind == Kind.Launch)
            c.Add(new(Rule.LaunchWindow, true, $"the launch ask is open until {l.LaunchBy:yyyy-MM-dd}"));
        else
        {
            double age = l.ClockStart is { } s ? (f.Now - s).TotalDays : 0;
            c.Add(new(Rule.InstallAge, age >= p.MinInstallDays, $"day {Days(age)} of {p.MinInstallDays}"));
            c.Add(new(Rule.ListenDays, l.ListenDays >= p.MinListenDays, $"{l.ListenDays} of {p.MinListenDays} listening days"));
            c.Add(new(Rule.ListenHours, l.ListenHours >= p.MinListenHours, $"{Hours(l.ListenHours)} of {Hours(p.MinListenHours)} h"));
            milestone = MilestoneFor(l, p);
            if (milestone is { } ms)
                c.Add(new(Rule.Milestone, l.ListenHours >= ms, $"{Hours(l.ListenHours)} of the {Hours(ms)} h milestone"));
            if (l.LastAsk is { } last)
            {
                double days = (f.Now - last).TotalDays, since = l.ListenHours - l.HoursAtLastAsk;
                c.Add(new(Rule.Cooldown, days >= p.CooldownDays && since >= p.CooldownHours,
                    $"{Days(days)} of {p.CooldownDays} days and {Hours(since)} of {Hours(p.CooldownHours)} h since ask {l.Asks}"));
            }
        }
        double calm = l.LastTrouble is { } t ? (f.Now - t).TotalDays : double.PositiveInfinity;
        c.Add(new(Rule.CalmWeek, calm >= p.CalmDays,
            double.IsPositiveInfinity(calm) ? "no trouble on record" : $"trouble {Days(calm)} days ago, waits {p.CalmDays}"));

        // Run
        var session = SessionOf(f.UpdatedThisLaunch, f.WizardOpen, f.SignInRequired);
        long hold = p.LaunchHoldMinutes * 60_000L;
        bool launchInUpdate = session == Session.AfterUpdate && kind == Kind.Launch;
        c.Add(new(Rule.OrdinarySession, session == Session.Ordinary || (launchInUpdate && f.SessionAgeMs >= hold), session switch
        {
            Session.AfterUpdate when kind == Kind.Launch =>
                $"the update session: {f.SessionAgeMs / 60_000} of {p.LaunchHoldMinutes} min before the launch ask",
            Session.AfterUpdate => "the first session after an update belongs to What's new",
            Session.FirstRun => "first run or signing in",
            _ => "ordinary session",
        }));
        c.Add(new(Rule.OncePerRun, !f.ShownThisRun, f.ShownThisRun ? "already shown this run" : "not shown this run"));

        // Now
        long since0 = m.SinceTrackChangeMs ?? -1;
        c.Add(new(Rule.AfterTrackChange, since0 >= 0, since0 < 0 ? "no track change armed" : $"{Secs(since0)} s into the track"));
        long idle = since0 < 0 ? 0 : m.IdleMs < 0 ? since0 : Math.Min(m.IdleMs, since0);
        c.Add(new(Rule.Idle, idle >= p.IdleSeconds * 1000L, $"idle {Secs(idle)} of {p.IdleSeconds} s"));
        c.Add(new(Rule.Foreground, m.Window == WindowFact.Front, m.Window switch
        {
            WindowFact.Front => "Wavee is in front", WindowFact.Tray => "hidden in the tray",
            WindowFact.Minimized => "minimized", _ => "behind another window",
        }));
        c.Add(new(Rule.NoTyping, !m.SearchFocused, m.SearchFocused ? "the search box has focus" : "nothing being typed"));
        c.Add(new(Rule.NoOverlay, !m.OverlayOpen, m.OverlayOpen ? "a dialog, flyout or menu is open" : "no overlay"));
        var surface = SurfaceOf(m);
        c.Add(new(Rule.BrowseSurface, surface == Surface.Browse, surface switch
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
        (Event.Shown, Origin.Card) => l with { Asks = l.Asks + 1, LastAsk = now, HoursAtLastAsk = l.ListenHours, Last = Answer.None },
        (Event.ShownLaunch, Origin.Card) => l with { Asks = l.Asks + 1, LastAsk = now, HoursAtLastAsk = l.ListenHours, Last = Answer.None, Launch = LaunchState.Shown },
        (Event.Rated, _) => l with { Done = true, Last = Answer.Rated },
        (Event.LinkOpened, _) => l with { Done = true, Last = Answer.LinkOpened },
        (Event.Canceled, Origin.Card) => l with { Last = Answer.Canceled },
        // A regular ask that could open nothing is refunded (LastAsk keeps the cooldown). The launch ask is not: its moment
        // has passed (Launch == Shown and it is the only ask shown so far).
        (Event.OpenFailed, Origin.Card) => l.Launch == LaunchState.Shown && l.Asks == 1
            ? l with { Last = Answer.OpenFailed }
            : l with { Asks = Math.Max(0, l.Asks - 1), Last = Answer.OpenFailed },
        (Event.NotNow, Origin.Card) => l with { Last = Answer.NotNow },
        (Event.Ignored, Origin.Card) => l with { Last = Answer.Ignored },
        (Event.NeverAgain, Origin.Card) => l with { Enabled = false, Last = Answer.NeverAgain },
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
        public Card Hold(bool on, long nowMs) => on
            ? HoldSinceMs is null ? this with { HoldSinceMs = nowMs } : this
            : HoldSinceMs is { } h ? this with { HeldMs = HeldMs + (nowMs - h), HoldSinceMs = null } : this;

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
            || !TryNum(f[4], out double atAsk) || !TryInt(f[5], out int last) || !Enum.IsDefined((Answer)last)
            || f[6] is not ("0" or "1") || f[7] is not ("0" or "1") || !TryNum(f[8], out double secs)
            || !TryInt(f[9], out int days) || !TryDay(f[10], out var day) || !TryNum(f[11], out double daySecs)
            || !TryMs(f[12], out var trouble) || !TryMs(f[13], out var since)
            || !TryInt(f[14], out int launch) || !Enum.IsDefined((LaunchState)launch) || !TryMs(f[15], out var launchBy)) return false;
        ledger = new Ledger
        {
            ClockStart = clock, Asks = asks, LastAsk = lastAsk, HoursAtLastAsk = atAsk, Last = (Answer)last,
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
        return f.Length == 2 && TryInt(f[0], out int n) && TryMs(f[1], out var at) && at is { } t ? new Stars(n, t) : null;
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
```

`InstallChannel` is the three-way split `Feedback.ReportIdentity.From` already makes (`Feedback.cs:147-149`). It moves
next to the version stamp so both features share one predicate:

```csharp
// Platform/Platform.Settings.cs, beside WaveeVersionInfo
public enum InstallChannel : byte { Store, Sideload, Source }

// in WaveeVersionInfo
/// <summary>Store build; a packaged non-dev build (the GitHub .msix / .appinstaller); anything else is a source build.</summary>
public InstallChannel ChannelFor(bool isPackaged) => IsStore ? InstallChannel.Store : isPackaged && !IsDev ? InstallChannel.Sideload : InstallChannel.Source;
```

`Feedback.ReportIdentity.From` keeps its signature and maps the enum to its three strings (`InstallSources[(int)channel]`).

---

## 3. Persistence: one key

```csharp
// Platform/Platform.Settings.cs, Keys
/// <summary>The rate/star nudge's ledger (RateNudge.Encode). One string: a write is atomic. Empty = never read yet.</summary>
public static readonly SettingKey<string> RateNudgeLedger = new("app.rateNudge.ledger", "");
/// <summary>The cached GitHub star count (RateNudge.EncodeStars, `count|ms`). A cache: losing it costs one request.</summary>
public static readonly SettingKey<string> RateNudgeStars = new("app.rateNudge.stars", "");
```

Settings hold bool/int/long/float/double/string only, so the ledger is one string. Under `--fake` (and so under
`--inspect`) the store is in memory: an inspector run never touches the real profile.

---

## 4. `RateNudge.Host.cs`: gather, commit, act (decides nothing)

```csharp
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

    // ══ 2. STATE ═══════════════════════════════════════════════════════════════════════════════════════════════════

    static Ledger s_ledger = Ledger.Fresh;
    static bool s_updatedThisLaunch, s_shownThisRun;
    static long? s_trackChangedMs;        // armed by a track boundary, cleared when the card shows
    static long s_playMarkMs = -1;        // the start of the stretch now playing (-1: not playing)
    static long s_bootMs;                 // MonoMs at boot: the session's age (the launch ask's hold in the update session)
    static string s_lastBlocker = "";
    static Action<Action>? s_post;        // the UI thread, from the layer's UsePost

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

    // ══ 3. BOOT (App startup, after Update.Host.Init: UpdatedFrom and PreviousRunVersion are known) ════════════════

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
            SearchFocused: Shell.SearchFocused,                       // new read-only accessor over s_omnibar.SearchFocused
            OverlayOpen: overlayOpen,
            RouteKey: Shell.RouteKeyOf(Shell.Current.Peek()),         // the s_routes key column (confirm: fg api Shell.Row)
            StageOpen: Shell.Ui.ImmersiveLyrics.Peek(),
            VideoUp: Video.State.MainHostFullscreen.Peek() || Video.State.DetachedFullscreen.Peek(),
            WindowFullScreen: InputHooks.Current.Default.IsWindowFullscreen?.Invoke() ?? false);
        return new Facts(Channel, s_ledger, s_updatedThisLaunch, Setup.WizardOpen.Peek(),
            Shell.Auth.Peek() == AuthState.SignInRequired, now - s_bootMs, s_shownThisRun, moment, s_seams.Now());
    }

    static WindowFact LiveWindow()
        => !FluentApp.WindowVisible ? WindowFact.Tray
         : IsIconic(FluentApp.WindowHandle) ? WindowFact.Minimized
         : InputHooks.Current.Default.IsWindowActive?.Invoke() == true ? WindowFact.Front : WindowFact.Behind;

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
        => await FluentGpu.WindowsApi.Store.StoreRating.RequestAsync(owner, ct) switch
        {
            StoreRatingStatus.Succeeded => StoreAnswer.Succeeded,
            StoreRatingStatus.CanceledByUser => StoreAnswer.CanceledByUser,
            StoreRatingStatus.NetworkError => StoreAnswer.NetworkError,
            StoreRatingStatus.Error => StoreAnswer.Error,
            _ => StoreAnswer.Unavailable,
        };

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

    /// <summary>Through the app's one external-open door. `ms-windows-store:` must pass it: some doors only let http(s)
    /// through (Feedback.UI, Shell.Overlays' play-link), this one must not (confirm App.cs:223 → hooks.OpenUri).</summary>
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
```

Notes on the readings (each is an existing member unless marked new):

| Fact | Source |
|---|---|
| track boundary | effect on `Playback.CurrentId` (`Playback.Host.cs:127`), passed through `IsTrackBoundary(prev, next)` |
| playing edges | effect on `Playback.IsPlaying` (`:138`) |
| idle | `FluentApp.MsSinceInteraction`: **new relay** of `AppHost.MsSinceInteraction` (`AppHost.cs:4607`), which counts press, key and wheel on the cold-maintenance clock. The inspector moves that clock with `window advance=` |
| window | `FluentApp.WindowVisible` (false when hidden to the tray), `IsIconic(FluentApp.WindowHandle)`, `InputHooks.IsWindowActive` |
| route | `Shell.Current` → its route key (**new** `Shell.RouteKeyOf`, the `s_routes` key column) |
| stage, video, full screen | `Shell.Ui.ImmersiveLyrics`, `Video.State.MainHostFullscreen` / `DetachedFullscreen`, `InputHooks.IsWindowFullscreen` |
| overlays | `IOverlayService.AnyOpen`, read by the layer through `UseContext(Overlay.Service)` (confirm it does not count the toast lane; if it does, the card waiting for a toast is still the right behaviour) |
| search focus | **new** `Shell.SearchFocused` over `OmnibarModel.SearchFocused` (`OmnibarModel.cs:384`; the instance is private in `Shell.Masthead.UI.cs:109`) |
| session | `Update.Host.UpdatedFrom` (`Update.Host.cs:459`), `Setup.WizardOpen`, `Shell.Auth == SignInRequired` |
| trouble | `Crash.Host.ThisLaunch` at boot; effects on `Playback.Error` and `Spotify.Fault` |
| first seen | none exists: the ledger's `ClockStart`; an older install is `Update.Host.PreviousRunVersion != ""` with no ledger yet |
| star count | `GET https://api.github.com/repos/christosk92/WaveeMusic` through `ReleaseNotes.GitHubHttp` (the shared GitHub pool: User-Agent, accept header, `Wire` offline gate), at most once a day, GitHub and source installs only |

---

## 5. The card

### 5.1 Contract

```csharp
// Theming/Contracts/ViewKeys.Shell.cs (the family the overlays use; confirm with fg api ViewKeys)
public static readonly ViewKey<RateNudgeCardProps> ShellRateNudge = new("Shell.RateNudge");

// Theming/Contracts/RateNudgeProps.cs
public sealed record RateNudgeView(
    string Title, string Body, string? Meta,           // plain card
    string? Big, string? BigCaption,                   // milestone card ("100 hours", "of music played in Wavee since July 2")
    string Primary, string Later, string Never,
    RateNudgeGlyph Glyph);                             // Store bag or star on the primary button

public enum RateNudgeGlyph : byte { Store, Star }

public sealed record RateNudgeCardProps
{
    public required IReadSignal<RateNudgeView?> View { get; init; }   // null: no card; the template binds Visible to it
    public required Action Primary { get; init; }
    public required Action Later { get; init; }
    public required Action Never { get; init; }
    public required Action<bool> Hold { get; init; }                  // pointer over or focus within
}
```

### 5.2 `RateNudge.UI.cs`: the layer

```csharp
// ── Screens/RateNudge.UI.cs ──
// Role: UI · the overlay lane's rate-nudge layer: wires the host's entries to live signals, resolves copy, hands the
// picture its props. Mounted ONCE, in Shell.Overlays after ReleaseNotes.AfterUpdateChrome (the mount order there is
// load-bearing: the session is known by then).

public static partial class RateNudge
{
    public static Element Chrome() => Embed.Comp(static () => new RateNudgeLayer());

    [CoreHost]
    sealed class RateNudgeLayer : Component
    {
        string? _lastTrack;

        public override Element Render()
        {
            var overlay = UseContext(Overlay.Service);
            var post = UsePost();
            UseEffect(() => s_post = post, DepKey.Empty);

            UseEffect(() =>
            {
                string? next = Playback.CurrentId.Value.ToString();      // confirm: fg api EntityId (its uri text)
                if (IsTrackBoundary(_lastTrack, next)) OnTrackBoundary();
                _lastTrack = next;
            });
            UseEffect(() => OnPlaying(Playback.IsPlaying.Value));
            UseEffect(() => { if (Playback.Error.Value.IsFault) NoteTrouble("playback"); });    // confirm: fg api Fault
            UseEffect(() => { if (Spotify.Fault.Value != SessionFault.None) NoteTrouble("sign-in"); });
            UseInterval(() => Tick(overlay.AnyOpen), 1000f);

            var view = UseComputed(() => Shown.Value is { } c ? ViewFor(c) : null);
            return Slots.View(ViewKeys.ShellRateNudge, new RateNudgeCardProps
            {
                View = view,
                Primary = static () => _ = PrimaryAsync(Origin.Card),
                Later = static () => Answer(Event.NotNow, Origin.Card),
                Never = static () => Answer(Event.NeverAgain, Origin.Card),
                Hold = static on => Hold(on),
            });
        }
    }

    /// <summary>Copy is picked by (Ask, milestone); the words live in en-US.json under rateNudge.*, first person (owner,
    /// 2026-11-20). The star line only on a star card, and only when StarCount has a count (StarsShown decides).</summary>
    static RateNudgeView ViewFor(Card c) => c.Ask == Ask.Rate
        ? new(Loc.Get(Strings.RateNudge.Rate.Title), Loc.Get(c.Milestone is null ? Strings.RateNudge.Rate.Body : Strings.RateNudge.Rate.MilestoneBody), null,
              Big(c), BigCaption(c), Loc.Get(Strings.RateNudge.Rate.Primary), Loc.Get(Strings.RateNudge.Later), Loc.Get(Strings.RateNudge.Never), RateNudgeGlyph.Store)
        : new(Loc.Get(Strings.RateNudge.Star.Title), Loc.Get(c.Milestone is null ? Strings.RateNudge.Star.Body : Strings.RateNudge.Star.MilestoneBody),
              StarCount is { } n ? Strings.RateNudge.Star.Meta(n) : null,
              Big(c), BigCaption(c), Loc.Get(Strings.RateNudge.Star.Primary), Loc.Get(Strings.RateNudge.Later), Loc.Get(Strings.RateNudge.Never), RateNudgeGlyph.Star);

    // "25 hours" and "of music played in Wavee since July 2" (the ledger's CountedSince, in the user's date format).
    static string? Big(Card c) => c.Milestone is { } h ? Strings.RateNudge.Big((int)h) : null;
    static string? BigCaption(Card c) => c.Milestone is null || Current.CountedSince is not { } since ? null
        : Strings.RateNudge.BigCaption(since.ToLocalTime().ToString("M", CultureInfo.CurrentCulture));

    // The doors' verbs (Settings dials, the account menu, the plate footer): one home, the same flow as the card.
    public static bool DoorPrimary() { _ = PrimaryAsync(Origin.Door); return false; }

    /// <summary>The switch's caption: "At most three times, ever" on an install with a launch ask, "twice" on a new one.</summary>
    public static string CapHint() => Strings.RateNudge.Setting.Hint(CapFor(Current, Rules));
}
```

The interval runs at 1 Hz and allocates nothing when idle (`Tick` returns at the first `null` check). If the frame-cost
gate objects to a permanent interval, enable it only while `s_trackChangedMs` or `Shown` is set (`UseInterval(…, enabled:)`
fed by a small signal the host flips); the behaviour is identical.

### 5.3 The picture (`Themes/Common/RateNudgeCard.cs`)

```
OverlaysLayer (full window, hit-test pass-through, column)
└─ RateNudge.Chrome()                                          zero-size host until a card is shown
   └─ Slots.View(ViewKeys.ShellRateNudge) → RateNudgeCard.Template
      BoxEl  ZStack, Grow=1, HitTestPassThrough                full-bleed lane: nothing in the page moves
      └─ BoxEl Key="rate-nudge"  Visible = Prop.Of(() => p.View.Value is not null)
            AlignSelf=End, JustifySelf=End, Width=RateNudgeRules.Width
            Margin = (0, 0, gutter, PlayerBar.Height + RateNudgeRules.AboveBar)   PlayerBar.Height from the theme's facts,
            Enter = MotionRecipes FadeUp (12 DIP, 220 ms), Exit = the same reversed      as OverlayFit.Toasts does
            Role=Region, Focusable=false (never takes focus); pointer enter/leave and focus-within → p.Hold(true/false)
            ├─ BoxEl row  Gap=M
            │   ├─ ImageEl app icon 38                                   Key="rate-nudge:icon"
            │   └─ column: [milestone] TextEl Big (title) + BigCaption   Key="rate-nudge:big"
            │              TextEl Title (body strong)                    Key="rate-nudge:title"
            │              TextEl Body (caption, wraps)                  Key="rate-nudge:body"
            │              [star + count] ★ TextEl Meta (caption)        Key="rate-nudge:meta"
            └─ BoxEl row  Gap=S, AlignItems=Center
                ├─ Button accent  glyph + Primary                        Key="rate-nudge:primary"
                ├─ Button         Later                                  Key="rate-nudge:later"
                └─ HyperlinkButton Never (pushed right, Grow spacer)     Key="rate-nudge:never"
```

- Numbers come from `Themes/Common/Rules/RateNudgeRules.cs` (`Width = 336`, `AboveBar = 14`, `Icon = 38`) and tokens
  for type, colours, radii and spacing (WVTH003). Classic, WinUI and Zune each get one registration line:
  `CommonBlocks.Register(views, …)` gains the key (Classic touches nothing else).
- Motion is the reduced-motion-aware recipe (fade only when `Motion.ReducedMotion`); exit is symmetric and interruptible
  (owner rule on motion). The card is drawn over the page, so the page never moves (owner rule: no layout jumps).
- It never takes focus and never announces more than once (`Role=Region`, polite).

---

## 6. The doors

**Settings → About** (`Screens/SettingsSpec.Help.cs`): a group of its own, "Support Wavee", between `about` and
`feedback`. It is not in the `links` group, because Classic's `LinksCard` draws every action row of that group as a plain
link inside one shared card, and the door must be the same card in Classic and WinUI (owner, 2026-11-20).

```csharp
Group("support", Strings.Settings.Groups.Help.Support, null,
// the door: WinUI draws every help.links.* id as its own link card (WinUiSettings.Rows.cs:317, DoorCard + OpenInNewWindow)
Make("help.links.rate", K.Action, Strings.RateNudge.Door.Rate,
    SettingsWriter.OfAction("about.link:rate", RateNudge.DoorPrimary), captionKey: Strings.RateNudge.Door.RateSub,
    visible: static _ => RateNudge.DoorAsk(RateNudge.Channel) == RateNudge.Ask.Rate),
Make("help.links.star", K.Action, Strings.RateNudge.Door.Star,
    SettingsWriter.OfAction("about.link:star", RateNudge.DoorPrimary),
    caption: static _ => RateNudge.StarCount is { } n ? Strings.RateNudge.Door.StarSubCount(n) : Loc.Get(Strings.RateNudge.Door.StarSub),
    visible: static _ => RateNudge.DoorAsk(RateNudge.Channel) == RateNudge.Ask.Star),

// under the door: one switch per channel, so a Store install never reads "star"
Make("help.rate.ask", K.Toggle, Strings.RateNudge.Setting.Rate,
    SettingsWriter.OfToggle("rate.ask", RateNudge.SetEnabled), caption: static _ => RateNudge.CapHint(),
    value: static _ => RateNudge.Current.Enabled,
    visible: static _ => RateNudge.CardAsk(RateNudge.Channel) == RateNudge.Ask.Rate),
Make("help.star.ask", K.Toggle, Strings.RateNudge.Setting.Star,
    SettingsWriter.OfToggle("star.ask", RateNudge.SetEnabled), caption: static _ => RateNudge.CapHint(),
    value: static _ => RateNudge.Current.Enabled,
    visible: static _ => RateNudge.CardAsk(RateNudge.Channel) == RateNudge.Ask.Star)),
```

**Classic draws the door as WinUI does** (`Themes/Classic/Structure/Views/Screens/ClassicSettings.About.cs`, one `case`
in `TryAboutCard`): its own clickable card, the dial's title and caption (the star count on a GitHub install), and the
open-in-new glyph where WinUI's `LinkCard` puts it. The two switches need nothing: Classic's generic grammar draws a
toggle dial as a switch card, as WinUI does.

```csharp
case "help.links.rate" or "help.links.star":
    // Owner, 2026-11-20: Classic follows WinUI's style, so the support door is WinUI's link card: its own clickable card with
    // the caption and the open-in-new glyph, never a plain link in LinksCard (it sits in its own group, so LinksCard never sees it).
    string id = row.Id;
    card = CardRow(row, LinkGlyph(), isClickEnabled: true, onClick: () => Dial(id).Writer.Run!());
    return true;
```

`LinkGlyph()` is the open-in-new icon at Classic's card-glyph size (confirm the Classic helper with
`fg api ClassicSettings.CardRow` and reuse whatever its chevron cards use; add a helper only if none exists). Zune draws an
unknown dial with its default row: `find help.links.rate` in Zune to confirm it is there.

(`caption:` returns a resolved string here, where the other dials return a key: confirm which one
`SettingsDial.Caption` expects with `fg api SettingsDial.Caption`, and pass `StarSubCount`'s key plus its argument the
house way if it wants a key.)

Classic (`ClassicSettings.About.cs`) and Zune (`ZuneSettings.Composites.cs`) switch on dial ids: check with `find` that an
unknown `help.links.*` id falls to their default row. If Classic needs a `case`, that is a Classic change beyond
registration: owner decision (§11).

**Account menu** (`Platform/Actions.Rules.cs`, `Shell/Shell.Overlays.UI.cs`): `ProfileRow.Support` after
`PrivateSession`; its item reads `RateNudge.DoorAsk(Channel)` for the label and calls `RateNudge.DoorPrimary()`.

**After-update plate** (`Theming/Contracts/ReleaseNotesProps.cs`): `AfterUpdatePlateProps` gains an optional
`RateDoor` (`string Line, string Button, Action Open`), additive, built by `ReleaseNotes.UI.cs` from
`RateNudge.DoorAsk(Channel)` and `RateNudge.DoorPrimary`. Every theme draws it as one row above the footer's buttons: the
line as caption text, then a hyperlink button.

- WinUI: `WinUiReleaseNotes.Plate.cs`.
- Classic: `ClassicReleaseNotes.Plate.cs`, the same row as WinUI's, with Classic's tokens (owner, 2026-11-20). The footer
  row itself (`[dontShowBox, Spacer(), full, gotIt]`) is untouched.
- Zune: `ZuneReleaseNotes.Plate.cs`, the same row in Zune's type.

---

## 7. Engine (paired `fluent-gpu` PR)

1. `FluentApp.MsSinceInteraction` (`FluentGpu.Windows/Hosting/FluentApp.cs`): relay `AppHost.MsSinceInteraction` as
   `FluentApp.IsSettledForPause` is relayed (`:906`).
2. `FluentGpu.WindowsApi/Store/StoreRating.cs`: the Store's rate-and-review dialog without CsWinRT, built like
   `PackageUpdater` (hand-written vtables, `WinRtAsync.Wait` polling on a worker).

```csharp
namespace FluentGpu.WindowsApi.Store;

public enum StoreRatingStatus : byte { Succeeded, CanceledByUser, NetworkError, Error, Unavailable }

/// <summary>Windows.Services.Store.StoreContext.RequestRateAndReviewAppAsync (Windows 10 1809+). STARTED on the UI thread
/// (the dialog's owner); the wait polls IAsyncInfo on a worker (WinRtAsync), so nothing blocks the frame. Never throws.
/// Unavailable: unpackaged, pre-1809, activation failed, or an ELEVATED process (microsoft-ui-xaml#10755: a native access
/// violation after InitializeWithWindow when elevated; checked from the token before any call).</summary>
public static unsafe class StoreRating
{
    public static Task<StoreRatingStatus> RequestAsync(nint owner, CancellationToken ct = default);
    // 1. RoGetActivationFactory("Windows.Services.Store.StoreContext", IID_IStoreContextStatics) → GetDefault
    // 2. QueryInterface IInitializeWithWindow → Initialize(owner)
    // 3. QueryInterface IStoreContext4 → RequestRateAndReviewAppAsync → IAsyncOperation<StoreRateAndReviewResult>
    // 4. worker: WinRtAsync.Wait(op) → GetResults → IStoreRateAndReviewResult.get_Status → StoreRatingStatus
    // IIDs and vtable slots: copy from windows.services.store.h (SDK 10.0.17763+); never type them from memory.
}
```

---

## 8. The `nudge` inspector verb

`Screens/Diagnostics.Inspect.Nudge.cs` (a partial of `InspectSession`), `Verb.Nudge` before `Quit` in
`Diagnostics.Inspect.Protocol.cs`, one dispatch line in the no-mount switch of `Diagnostics.Inspect.cs`.

### 8.1 Grammar

```csharp
new(Verb.Nudge, "nudge", "Read or drive the rate/star nudge: the decision with every check, the ledger, the card. Load a fixture, pin a fact, fake the Store, fire a track boundary; expect= turns a mismatch into ok:false", "nudge fixture=eligible channel=store")
{
    Acts = true,
    Args = Join([
        Arg("fixture", ArgType.Enum, "a named ledger, clock and channel (§8.3)", null,
            "fresh|day3|eligible|short-days|short-hours|asked-60|asked-130|asked-130-quiet|capped|rated|starred|opted-out|troubled|troubled-old|milestone-99|milestone-100|existing|existing-lapsed|existing-later|existing-capped"),
        Arg("channel", ArgType.Enum, "the install source", null, "store|github|source"),
        Arg("session", ArgType.Enum, "this launch", null, "ordinary|after-update"),
        Arg("window", ArgType.Enum, "the window fact: a headless window is never the active one, so it is pinned", null, "front|behind|minimized|tray"),
        Arg("policy", ArgType.Enum, "the thresholds: default (milestones on) or plain (no milestone)", null, "default|plain"),
        Arg("starsCache", ArgType.String, "the cached star count: 86@-2h, 86@-8d, none"),
        Arg("stars", ArgType.String, "the star fetch's next answer: a count, or fail"),
        Arg("refresh", ArgType.Bool, "run RefreshStarsIfDue now (it runs at boot in the app)"),
        Arg("clock", ArgType.String, "move calendar time: +130d, -2d, +6h, reset"),
        Arg("trouble", ArgType.String, "a fault at: now, -8d, none"),
        IntArg("sessionAge", "minutes since this launch (the launch ask's hold in the update session)", 0, 0, 100000),
        Arg("store", ArgType.Enum, "the Store dialog's next answer", null, "succeeded|canceled|network|error|unavailable"),
        Arg("link", ArgType.Enum, "whether opening a URL or URI works", null, "ok|fail"),
        Arg("fire", ArgType.Bool, "a track boundary now, through RateNudge.OnTrackBoundary (the playback effect's own entry)"),
        Arg("tick", ArgType.Bool, "run the once-a-second check now"),
        Arg("raw", ArgType.String, "write this raw ledger string to settings (with reload=true: the corrupt-value path)"),
        Arg("reload", ArgType.Bool, "re-read the ledger from settings: the persistence round trip"),
        Arg("expect", ArgType.String, "show:rate:1 | show:star:2 | show:rate:1:launch | quiet:<Rule> | closed: ok:false on a mismatch"),
        Arg("expectLedger", ArgType.String, "comma list of ledger fields that must match, e.g. asks=1,last=NotNow,enabled=true"),
        Arg("expectLaunched", ArgType.String, "a prefix the last launched URL must start with, or none"),
        Arg("expectCard", ArgType.Enum, "the card on screen", null, "shown|hidden"),
    ], s_settleArgs),
},
```

Order inside one command: `raw` → `fixture` → `channel/session/window/policy/clock/trouble` → `store/link` → `reload` →
`fire` → `tick` → evaluate → `expect*`. `mount` and `reset` call `RateNudge.ResetForInspect` with the fresh fixture, so a
scenario never inherits the last one.

### 8.2 Seams under the inspector

| Seam | Inspector value |
|---|---|
| `Now` | `Platform.Clock.SeedEpoch` + the `clock=` offset (the fake seed's fixed dates) |
| `MonoMs` | `FrameTime.NowMs` (16 ms per inspector frame: `frames 1300` ≈ 20.8 s) |
| `IdleMs` | the session host's `AppHost.MsSinceInteraction` (a headless mount is not `FluentApp`'s window, whose relay would always say "no input"); cold-maintenance clock: `window advance=21000`; any `click`/`key` resets it |
| `Window` | pinned by `window=` (default `front`) |
| `StoreDialog` | returns `store=` (default `succeeded`); the real Store dialog is never shown headless |
| `Launch` | records the URL into `launched` and returns `link=` (default `ok`); nothing opens |
| `FetchStars` | returns `stars=` (default `fail`); no request leaves the machine (`--inspect` is offline anyway) |

### 8.3 Fixtures

| Fixture | Ledger | Channel |
|---|---|---|
| `fresh` | `ClockStart = now`, nothing played | store |
| `day3` | clock −3 d, 3 listening days, 4 h | store |
| `eligible` | clock −30 d, 18 days, 26 h, never asked | store |
| `short-days` | as eligible, 5 days | store |
| `short-hours` | as eligible, 6 h | store |
| `asked-60` | asks 1, last NotNow, lastAsk −60 d, 66 h at the ask, 106 h now (past the 100 h milestone) | store |
| `asked-130` | asks 1, lastAsk −130 d, 66 h at the ask, 106 h now | store |
| `asked-130-quiet` | asks 1, lastAsk −130 d, 96 h at the ask, 100.5 h now (4.5 h since) | store |
| `capped` | a new install at its cap: asks 2, no launch ask | store |
| `rated` | done, last Rated | store |
| `starred` | done, last LinkOpened | github |
| `opted-out` | enabled false, last NeverAgain | store |
| `troubled` | eligible + trouble −2 d | store |
| `troubled-old` | eligible + trouble −8 d | store |
| `milestone-99` | asks 1, lastAsk −130 d, 59.9 h at the ask, 99.9 h now | store |
| `milestone-100` | as `milestone-99` at 100.1 h | store |
| `existing` | the update that ships this, on an install that predates it: no ledger yet, so Boot's `Seed(predates: true)` runs (launch Pending, open 30 days, counters at 0); `session=after-update` | store |
| `existing-lapsed` | launch Pending, its window closed yesterday; 6 h, 4 listening days | store |
| `existing-later` | launch Shown, answered Not now 130 days ago; 30 h and 20 listening days since the update | store |
| `existing-capped` | launch Shown and both regular asks shown: asks 3 | store |

Every fixture also sets `CountedSince` = clock start, `starsCache=none` and `stars=fail` unless the command says otherwise.
`eligible` sits at 26 h, past the first milestone (25 h), so its card is the 25-hour milestone card.

### 8.4 Reply

```json
{"id":-1,"ok":true,"cmd":"nudge","channel":"Store","now":"2026-12-20T12:00:00+01:00",
 "decision":{"show":false,"ask":"Rate","askNumber":1,"closed":false,"blocker":{"rule":"Idle","stage":"Now","detail":"idle 4 of 20 s"},
   "earliest":"2026-11-04","checks":[{"rule":"Channel","stage":"Who","pass":true,"detail":"Store install: asks for a Store rating"}, …]},
 "ledger":{"text":"asks=0 last=None done=False …","raw":"1|1765…|0|…"},
 "card":{"shown":false},"launched":[],"stars":{"shown":null,"cached":null,"fetches":0},
 "after":{"route":"home","focused":null,"overlays":[],"hit":null}}
```

`decision` is the host's `LastDecision` when it evaluated since the last fixture (the layer's interval runs during
`frames`, so the card may already be up, and the decision that showed it is the one reported); otherwise the verb
evaluates `Decide(Gather(…))` for the reply without acting on it. The verb calls `RateNudge.Flush()` first, so the
ledger it prints includes the stretch now playing.

A failed `expect*` replies `ok:false` with `"error":"expected quiet:InstallAge, got show:rate:1"` and the same body, so
`inspect.py --gate error` exits 2, the same mechanism `lint` and `crossing` already use. The scenario lines live in this
document (§9), never in a test file.

---

## 9. Every scenario

`M` = the quiet moment after a boundary: `nudge fire=true`, `window advance=21000`, `frames 1300`, `nudge tick=true`.
Each row is one fresh `mount w=1440 h=960 theme=winui route=home` unless it says otherwise.

| # | Scenario | Commands | Expect |
|---|---|---|---|
| **Channel** ||||
| A1 | Store install gets the rating card | `nudge fixture=eligible` · M | `expect=show:rate:1 expectCard=shown`; `find rate-nudge:primary` reads "Rate in the Store", `rate-nudge:big` "25 hours"; no `rate-nudge:meta`; render |
| A2 | GitHub install gets the star card | `nudge fixture=eligible channel=github starsCache=86@-2h` · M | `show:star:1`, primary "Star on GitHub", `rate-nudge:meta` "86 people have starred it" |
| A3 | Source build never sees the card | `nudge fixture=eligible channel=source` · M | `expect=closed expectCard=hidden`; doors only (G1) |
| **Who** ||||
| B1 | Rated stops it | `fixture=rated` · M | `quiet:NotDone`, closed |
| B2 | Starred stops it | `fixture=starred` · M | `quiet:NotDone` |
| B3 | Don't ask again stops it | `fixture=opted-out` · M | `quiet:Enabled` |
| B4 | A new install's cap is two | `fixture=capped` · M | `quiet:UnderCap` ("asked 2 of 2") |
| **When** ||||
| C1 | Not before day 14 | `fixture=day3` · M | `quiet:InstallAge`, `earliest` = clock + 14 d |
| C2 | 7 listening days | `fixture=short-days` · M | `quiet:ListenDays` |
| C3 | 10 hours | `fixture=short-hours` · M | `quiet:ListenHours` |
| C4 | Cooldown, days | `fixture=asked-60` · M | `quiet:Cooldown` ("60 of 120 days") |
| C5 | Cooldown, hours | `fixture=asked-130-quiet` · M | `quiet:Cooldown` ("4.5 of 10 h") |
| C6 | Second and last ask | `fixture=asked-130` · M | `show:rate:2`, `rate-nudge:big` "100 hours" |
| C7 | Calm week after trouble | `fixture=troubled` · M | `quiet:CalmWeek` |
| C8 | Calm week over | `fixture=troubled-old` · M | `show:rate:1` |
| C9 | Time passes | `nudge fixture=eligible clock=-20d` · M → `quiet:InstallAge` (day 10); `nudge clock=reset` · M | `show:rate:1` |
| C11 | Listening counts a day once | `nudge fixture=fresh` · `play spotify:track:tr3 0 next=3` · `frames 40000` | ledger `days=1`, `hours≈0.17` (10 min of frames), and a second 10 min the same day leaves `days=1` |
| **The launch ask (existing installs, this update)** ||||
| L1 | Seeded on the update | `nudge fixture=existing` | `expectLedger=launch=Pending,asks=0`; `launchBy` = now + 30 d; counters at 0 |
| L2 | Not in the update session's first hour | `fixture=existing` · M | `quiet:OrdinarySession` ("the update session: 0 of 60 min before the launch ask") |
| L3 | In the update session after the hour | `fixture=existing sessionAge=61` · M | `expect=show:rate:1:launch`; no `rate-nudge:big` (no milestone): no listening needed |
| L4 | At the next launch | `fixture=existing session=ordinary` · M | `show:rate:1:launch` |
| L5 | Never over What's new | `fixture=existing sessionAge=61` · open the plate · M | `quiet:NoOverlay` |
| L6 | The calm week still holds | `fixture=existing sessionAge=61 trouble=-2d` · M | `quiet:CalmWeek` |
| L7 | Lapses uncounted after 30 days | `fixture=existing-lapsed session=ordinary` · M | a regular decision (`kind=Regular`), `quiet:ListenDays` (4 of 7); `asks=0` |
| L8 | A new install never gets it | `fixture=fresh session=after-update sessionAge=61` · M | `kind=Regular`, `quiet:InstallAge`; ledger `launch=None`, cap 2 |
| L9 | Regular asks follow the launch ask | `fixture=existing-later` · M | `show:rate:2` (`kind=Regular`), `rate-nudge:big` "25 hours" (the first milestone) |
| L10 | An existing install's cap is three | `fixture=existing-capped` · M | `quiet:UnderCap` ("asked 3 of 3 (2 of 2 regular)") |
| L11 | Launch ask rated | L3 · `nudge store=succeeded` · `click key=rate-nudge:primary` | `done=true,last=Rated`, `launch=Shown`; then `quiet:NotDone` |
| L12 | Launch ask that opens nothing isn't refunded | L3 · `nudge store=error link=fail` · click primary | `asks=1,launch=Shown,last=OpenFailed`; the next regular ask waits for the cooldown |
| L13 | Launch ask: Not now | L3 · `click key=rate-nudge:later` | `asks=1,launch=Shown,last=NotNow`; `earliest` = + 120 d |
| **Run** ||||
| D1 | First session after an update | `fixture=eligible session=after-update` · M | `quiet:OrdinarySession` |
| D2 | First run | `mount … setup=true` · `nudge fixture=eligible` · M | `quiet:OrdinarySession` ("first run or signing in") |
| D3 | Once per run | A1, then `click key=rate-nudge:later`, then M | `quiet` (the cooldown and the next milestone block first; OncePerRun fails too, a backstop) |
| **Now (the real app state)** ||||
| E1 | No track change yet | `fixture=eligible` · `window advance=21000` · `nudge tick=true` | `quiet:AfterTrackChange` |
| E2 | Not idle | `fixture=eligible` · `nudge fire=true` · `frames 600` · `click key=<any home row>` · `frames 700` · `nudge tick=true` | `quiet:Idle` (the click 11 s ago resets idle, whichever clock the engine stamps it on) |
| E3 | Typing | `fixture=eligible` · `click` the search box (focus stays) · `nudge fire=true` · `window advance=21000` · `frames 1300` · `nudge tick=true` | `quiet:NoTyping` (rule order puts it before NoOverlay, so an open suggestion flyout doesn't hide it) |
| E4 | A dialog is open | `fixture=eligible` · `dialog report` · M | `quiet:NoOverlay` |
| E5 | Full-screen stage | `mount … stage=visualizer` · `key f11` (the mount only picks the mode; F11 opens the stage) · `nudge fixture=eligible` · M | `quiet:BrowseSurface` ("stage") |
| E6 | Video | `mount … video=fullscreen` · `nudge fixture=eligible` · M | `quiet:BrowseSurface` ("video" or "full screen") |
| E7 | Not a browse page | `goto settings` · `nudge fixture=eligible` · M | `quiet:BrowseSurface` ("settings") |
| E8 | In the tray, minimized, behind | `nudge fixture=eligible window=tray` · M (then `minimized`, `behind`) | `quiet:Foreground` |
| E9 | A real track boundary, no `fire` | `nudge fixture=eligible` · `play spotify:track:tr3 <tr3's duration − 2000> next=2` · `frames 400` · `window advance=21000` · `frames 1300` · `nudge tick=true` | `show:rate:1`: proves the `Playback.CurrentId` wiring (the play reply and the bar's `-m:ss` give tr3's length) |
| E10 | The first track is not a boundary | `nudge fixture=eligible` · `play spotify:track:tr3 0` · `window advance=21000` · `frames 1300` · `nudge tick=true` | `quiet:AfterTrackChange` |
| **The card** ||||
| F1 | Ignored: tucks away | A1 · `frames 1300` | `expectCard=hidden expectLedger=asks=1,last=Ignored` |
| F2 | Hover holds it | A1 · `hover key=rate-nudge` · `frames 2000` · `nudge` | `expectCard=shown`; then `hover` elsewhere · `frames 1300` → hidden |
| F3 | Not now | A1 · `click key=rate-nudge:later` | `asks=1,last=NotNow,enabled=true`; `earliest` = + 120 d |
| F4 | Don't ask again | A1 · `click key=rate-nudge:never` | `last=NeverAgain,enabled=false`; `setting id=help.rate.ask` reads off |
| F5 | Store: rated | `nudge store=succeeded` · A1 · `click key=rate-nudge:primary` | `done=true,last=Rated`; toast "Thanks for rating Wavee"; `expectLaunched=none` |
| F6 | Store: cancelled | `store=canceled` · A1 · click primary | `asks=1,last=Canceled,done=false` |
| F7 | Store: network, link opens | `store=network link=ok` · A1 · click primary | `expectLaunched=ms-windows-store://review/?ProductId=9NJPVWTQPT9H`, `done=true,last=LinkOpened` |
| F8 | Store: error, nothing opens | `store=error link=fail` · A1 · click primary | `asks=0,last=OpenFailed` (refunded), lastAsk kept |
| F9 | Store unavailable (elevated, old Windows) | `store=unavailable link=ok` · A1 · click primary | as F7 |
| F10 | GitHub star | A2 · click primary | `expectLaunched=https://github.com/christosk92/WaveeMusic`, `done=true,last=LinkOpened` |
| F11 | Milestone not reached | `nudge fixture=milestone-99` · M | `quiet:Milestone` ("99.9 of the 100 h milestone") |
| F12 | Milestone card | `nudge fixture=milestone-100` · M | `show:rate:2`; `rate-nudge:big` "100 hours", the caption "since" the fixture's clock start; render |
| F13 | No jump in, no jump out | A1 with `frames 1300 trace=60 prefix=rate-nudge` around the arrival, and `trace=60` on the `click key=rate-nudge:later` | `--gate error` clean: the card animates, no home key moves (`movedAtOnce` empty) |
| F14 | Reduced motion | the Motion dial on (`setting category=appearance` lists its id) · A1 with the trace | fade only, no translate |
| F15 | Lint | A1 · `lint min=error` · `lint root=rate-nudge min=warn` | clean; contrast and clipping read in every theme and kind |
| F16 | Without milestones | `nudge fixture=eligible policy=plain` · M | `show:rate:1`; `find rate-nudge:big` count 0; the plain body |
| **Star count** ||||
| S1 | Shown from a fresh cache | A2 | meta "86 people have starred it"; About's `help.links.star` caption carries "86 stars" |
| S2 | Daily refresh | `nudge fixture=eligible channel=github starsCache=86@-30h stars=120 refresh=true` · `frames 10` | meta and caption read 120; log `stars: 120`; settings key `app.rateNudge.stars` = `120\|…` |
| S3 | Not refreshed twice a day | `starsCache=86@-2h stars=120 refresh=true` | no fetch (the fake counts calls: 0); still 86 |
| S4 | Fetch fails, stale cache | `starsCache=86@-8d stars=fail refresh=true` · `frames 10` | no meta line, plain caption; log `fetch failed, kept 86` |
| S5 | A Store install never sees GitHub | `nudge fixture=eligible channel=store starsCache=86@-2h stars=120 refresh=true` · M · `goto settings arg=help` | no fetch; no meta; `find` text "GitHub" and "star" count 0 in the card and the About rows |
| **Doors** ||||
| G1 | About rows per channel | `goto settings arg=help` with `channel=store`, `github`, `source` | store: `help.links.rate` + `help.rate.ask`; github: `help.links.star` + `help.star.ask`; source: `help.links.star` only |
| G1b | Classic matches WinUI | G1 in `theme=winui` and `theme=classic`, both kinds, rendered side by side | the door is its own clickable card with the caption and the open-in-new glyph in both; it is not a link inside Classic's links card (`find` it under the "support" group, not under `about:links`) |
| G2 | Door rating counts | `nudge fixture=eligible store=succeeded` · `setting id=help.links.rate` · `frames 10` (the answer is posted to the UI thread) · M | `done=true,last=Rated`; then `quiet:NotDone` |
| G3 | Door cancel changes nothing | `nudge fixture=eligible store=canceled` · `setting id=help.links.rate` · `frames 10` | ledger unchanged (`asks=0,last=None`) |
| G4 | The switch | `setting id=help.rate.ask value=off` · M → `quiet:Enabled`; `value=on` · M → `show:rate:1` (the GitHub install: `help.star.ask`) | |
| G5 | Account menu | `click` the profile chip · `find` the support row · `click` it | as G2 / G3 |
| G6 | After-update plate footer | open the plate (developer `help.developer.simulateUpdate`, confirm it opens the plate headless) in `winui`, `classic` and `zune` | the line and its button in all three, Classic's identical to WinUI's; the button runs `DoorPrimary` |
| **Persistence** ||||
| H1 | Round trip | F3 · `nudge reload=true` | `expectLedger` equals the pre-reload ledger |
| H2 | Corrupt value | `nudge raw="garbage" reload=true` | ledger fresh with a clock; log line `unreadable ledger` |
| H3 | Mount resets | F3 · `mount …` · `nudge` | fresh fixture, `card.shown=false` |

### 9.1 Look

A1, A2 and F12 rendered and looked at in `classic`, `winui` and `zune`, `kind=dark` and `kind=light`, at 400×860 (Compact),
1440×960 (Standard) and 2560×1440 (Huge): the card clears the player bar in each theme, reads at its size, and never covers
the bar's controls. One example run:

```bash
python -I tools/inspect.py --exe C:/WAVEE/scratch/rate-nudge/run/Wavee.exe --findings --gate error \
  -c "mount w=1440 h=960 theme=zune kind=dark route=home" \
  -c "nudge fixture=eligible" -c "nudge fire=true" -c "window advance=21000" -c "frames 1300" \
  -c "nudge tick=true expect=show:rate:1 expectCard=shown" \
  -c "lint min=error" -c "render path=C:/WAVEE/scratch/rate-nudge/a1.zune.dark.1440.png" \
  -c "click key=rate-nudge:later" -c "nudge expectLedger=asks=1,last=NotNow expectCard=hidden"
```

### 9.2 Done

A clean Debug build of Wavee and of fluent-gpu; every row of §9 run with `--gate error` exiting 0; the §9.1 renders
looked at; `lint min=error` clean on the card in every theme; F13's traces with no part moving at once.

---

## 10. Change list

| Repo | File | Change |
|---|---|---|
| app | `Screens/RateNudge.cs` | new: §2 |
| app | `Screens/RateNudge.Host.cs` | new: §4 |
| app | `Screens/RateNudge.UI.cs` | new: §5.2 |
| app | `Screens/Diagnostics.Inspect.Nudge.cs` | new: §8 |
| app | `Screens/Diagnostics.Inspect.Protocol.cs`, `Diagnostics.Inspect.cs` | `Verb.Nudge`, its spec, one dispatch line, the mount reset |
| app | `Theming/Contracts/RateNudgeProps.cs`, `ViewKeys.Shell.cs` | new contract and key (additive) |
| app | `Themes/Common/RateNudgeCard.cs`, `Themes/Common/Rules/RateNudgeRules.cs`, `CommonBlocks.cs` | the picture, its numbers, its registration |
| app | Classic / WinUI / Zune structure | one registration line each |
| app | `Platform/Platform.Settings.cs` | `InstallChannel`, `ChannelFor`, `Keys.RateNudgeLedger`, `Keys.RateNudgeStars` |
| app | `Screens/Feedback.cs` | `ReportIdentity.From` reads `InstallChannel` (same three strings) |
| app | `Shell/Shell.Overlays.UI.cs` | `RateNudge.Chrome()` after `ReleaseNotes.AfterUpdateChrome()`; the account-menu item |
| app | `Shell/Shell.Masthead.UI.cs`, `Shell/Shell.cs` | `Shell.SearchFocused`, `Shell.RouteKeyOf` (read-only accessors) |
| app | `Platform/Actions.Rules.cs` | `ProfileRow.Support` |
| app | `Screens/SettingsSpec.Help.cs` | three dials (§6) |
| app | `Theming/Contracts/ReleaseNotesProps.cs` + the WinUI, Classic and Zune plates | `AfterUpdatePlateProps.RateDoor` (additive) and its footer line |
| app | `Themes/Classic/Structure/Views/Screens/ClassicSettings.About.cs` | the support door's `case`: WinUI's link card (owner, 2026-11-20) |
| app | `App.cs` | `RateNudge.Boot(Platform.Settings)` after `Update.Host` starts; `RateNudge.Flush()` on exit |
| app | `assets/loc/en-US.json` | `rateNudge.*` |
| app | `changes/unreleased/rate-nudge.md` | the change file |
| engine | `FluentGpu.Windows/Hosting/FluentApp.cs` | `MsSinceInteraction` relay |
| engine | `FluentGpu.WindowsApi/Store/StoreRating.cs` | new: §7 |

Strings (`en-US.json`, `rateNudge`), first person (owner, 2026-11-20). A Store string never names GitHub or a star.

| Key | English |
|---|---|
| `rate.title` | Rate Wavee in the Microsoft Store |
| `rate.body` | I build Wavee on my own. If it has earned a place on your PC, a rating in the Store helps other people find it. |
| `rate.milestoneBody` | I build Wavee on my own. If it has been good company, a rating in the Store helps other people find it. |
| `rate.primary` | Rate in the Store |
| `star.title` | Star Wavee on GitHub |
| `star.body` | I build Wavee on my own, in the open. If it has earned a place on your PC, a star helps other people find it. |
| `star.milestoneBody` | I build Wavee on my own, in the open. If it has been good company, a star helps other people find it. |
| `star.primary` | Star on GitHub |
| `star.meta` | `{count, plural, one {# person has starred it} other {# people have starred it}}` |
| `later` / `never` | Not now / Don't ask again |
| `big` / `bigCaption` | `{hours} hours` / `of music played in Wavee since {date}` |
| `thanks` | Thanks for rating Wavee |
| `door.rate` / `door.rateSub` | Rate Wavee in the Microsoft Store / Opens the Store's rating dialog |
| `door.star` / `door.starSub` | Star Wavee on GitHub / Opens the repository in your browser |
| `door.starSubCount` | `Opens the repository in your browser · {count, plural, one {# star} other {# stars}}` |
| `setting.rate` / `setting.star` | Occasionally ask me to rate Wavee / Occasionally ask me to star Wavee |
| `setting.hint` | `At most {count, plural, =2 {twice} =3 {three times} other {# times}}, ever` |
| `settings.groups.help.support` (the group heading, in the settings strings) | Support Wavee |
| `plate.rate` | If this release made Wavee better for you, a rating in the Microsoft Store helps other people find it. |
| `plate.star` | If this release made Wavee better for you, a star on GitHub helps other people find it. |

---

## 11. Open questions

None. The seven decisions of 2026-11-20 are at the top of this document.
