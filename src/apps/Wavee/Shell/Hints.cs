// ── Shell/Hints.cs — the in-app hints: ONE table, the budget, retire-on-use, the slot a surface mounts ──────────────────────────────
//
// Role: CORE (settings rework phase 3, handoff/settings-2026-11-18/DESIGN.md 6.1-6.3; owner decisions 13 and 14).
//
// A HINT tells a person, at the moment a setting would help, that it exists, and offers ONE way to use it. Never an upsell, never a
// notification, never a dialog, never a sound; it sits on its own surface (`HintSurfaces`) and the theme draws it through ONE view key,
// `ViewKeys.Hint`, from the props built here. Each row of `HintDef` names one dial (or one feature pointer), the surface, the trigger
// fact (one sentence: `Hints.Facts.cs` feeds it from the place that already knows it), the words, the action (inline write | link |
// pointer | badge | state line) and the budget.
//
// THE BUDGET (6.2, `HintRules`): at most one hint on screen in the whole app; at most one NEW hint per session (a second waits for a
// later session); at most N shows on N different days; "×" snoozes 30 days and the third dismissal retires (some retire on the first);
// "Tips about features" off (`notifications.tips`) shows none; retired for good when its dial is written from anywhere
// (`SettingsWrites` → `OnDialWritten`) or, once it has shown, when the state it suggests is reached; never while the setup wizard
// runs, another dialog or flyout is open, the window is hidden or minimised, in the first two minutes after launch, or in the first
// seconds of a track. State lines are state, not hints: shown every time, never counted, untouched by the switch.
//
// PRIORITY (owner decision 14): state lines first, then the hint of the surface on screen (a slot only ever asks for its own surface),
// then `HintDef.Priority` (1 first), ordered by what missing it costs. The Settings home's Suggestions take the top three in the same
// order (`Suggestions`): there the on-screen, per-session and never-when gates do not apply (the person opened Settings to look), the
// rest of the budget does, and a show counts against the same show budget.
//
// DRAWN (phase 6). A row whose placement every theme it applies to draws is `Drawn`: its slot mounts it from its trigger. A row whose
// surface is not on this line (the sleep timer) stays undrawn: only the inspector's `hint show id=<hint>` (`Force`) would mount it, and no
// slot asks for its surface. A menu's dot (`HintAction.Badge`, the daylist tile's options row) has no slot: the menu asks when it builds its
// rows (`BadgeOnOpen`), and the dot goes with the menu. A row the surface draws IN PLACE, without the slot (the devices flyout's
// last row, "Sound settings", states the equalizer at rest), is `InPlace`: no slot picks it.
//
// ON SCREEN. A hint that appeared keeps its place until it is answered, set aside or its surface goes: the never-when gates decide
// whether a hint may APPEAR, not whether one already up must leave (a TeachingTip is itself an open overlay; a track starting under a
// hint must not pull it away). A click elsewhere (a TeachingTip's light dismiss) and a followed link set it aside for the session
// (`SetAside`, no snooze: its show was counted); an inline action turns it into its confirmation, which goes after
// `HintRules.ConfirmationMs` or with its own close.
//
// COST. Nothing here runs per frame: a slot re-evaluates when `Epoch` moves (a fact crossing its threshold, a show, a dismissal, a
// retirement, a force) or when the time gate it waits on runs out; `Pick` walks the fifteen rows over plain fields and allocates
// nothing; props (strings) are built only for a hint that shows.

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee;

/// <summary>One hint (design 6.3): see the file header.</summary>
public sealed record HintDef
{
    /// <summary>Stable id (<c>ai-lyrics</c>): the blob's key, the inspector's name, a template's node key.</summary>
    public required string Id { get; init; }

    /// <summary>The dial it is about (a registry id), or a feature pointer (<c>pointer.*</c>).</summary>
    public required string Target { get; init; }

    /// <summary>Where it sits (<see cref="HintSurfaces"/>).</summary>
    public required string Surface { get; init; }

    public required HintAction Action { get; init; }

    /// <summary>1 first, by what missing it costs; 0 for a state line (always first, never counted).</summary>
    public required int Priority { get; init; }

    /// <summary>The line (a loc key); <see cref="Words"/> words it instead when it carries a value.</summary>
    public required string TextKey { get; init; }

    /// <summary>The button's word (a loc key), or null.</summary>
    public string? ActionKey { get; init; }

    /// <summary>A heading for a template that titles its hint (a TeachingTip), a loc key; null = the dial's own title.</summary>
    public string? TitleKey { get; init; }

    /// <summary>The line with its value ("Your playback cache is almost full (30.4 of 32 GB)."), or null for <see cref="TextKey"/>.</summary>
    public Func<string>? Words { get; init; }

    /// <summary>What the confirmation's "Change" opens after an inline write (a dial id or a category slug), or null for <see cref="Target"/>.</summary>
    public string? ChangeTarget { get; init; }

    /// <summary>For <see cref="HintAction.Point"/>: the node key of the control it points at.</summary>
    public string? Anchor { get; init; }

    /// <summary>At most this many shows, on as many different days; 0 = unlimited (until resolved).</summary>
    public int MaxShows { get; init; }

    /// <summary>"×" snoozes it this long.</summary>
    public int SnoozeDays { get; init; } = HintRules.SnoozeDays;

    /// <summary>"×" retires it at once (one-time offers).</summary>
    public bool DismissRetires { get; init; }

    /// <summary>The dials whose write retires it; null = its <see cref="Target"/> when that is a dial.</summary>
    public IReadOnlyList<string>? RetiredBy { get; init; }

    /// <summary>The trigger fact, in one sentence (design 6.3). Reads facts; never allocates.</summary>
    public required Func<bool> Trigger { get; init; }

    /// <summary>The state the hint suggests is reached (the dial is on): it does not qualify, and once it has shown it retires.</summary>
    public Func<bool> Satisfied { get; init; } = static () => false;

    /// <summary>The hint can exist here at all (its dial is shown: fast navigation is not Zune's; the sleep timer must exist).</summary>
    public Func<bool> Applies { get; init; } = static () => true;

    /// <summary>Every theme it applies to draws its placement (phase 6): its slot mounts it from its trigger. Otherwise only
    /// <c>hint show</c> would.</summary>
    public bool Drawn { get; init; }

    /// <summary>The surface draws it itself, in place, without the slot (the devices flyout's "Sound settings" row): no slot picks it.</summary>
    public bool InPlace { get; init; }

    /// <summary>A state line: shown every time, never counted against the budget.</summary>
    public bool IsState => Action == HintAction.State;
}

/// <summary>The hint table and the service around it (see the file header).</summary>
public static partial class Hints
{
    /// <summary>Moves whenever what a slot shows may have changed. Slots read it; nothing per frame writes it.</summary>
    public static readonly Signal<int> Epoch = new(0);

    /// <summary>Re-evaluate every slot.</summary>
    public static void Bump() => Epoch.Value = Epoch.Peek() + 1;

    static IReadOnlyList<HintDef>? s_all;

    /// <summary>The fifteen placements of design 6.3 (rows 15 and 17 are deliberately no hint: the lyrics pane's ⋯ is the blur's door,
    /// and private session is never advertised), in table order.</summary>
    public static IReadOnlyList<HintDef> All => s_all ??= Build();

    /// <summary>The index of the hint <paramref name="id"/> in <see cref="All"/>, or -1.</summary>
    public static int IndexOf(ReadOnlySpan<char> id)
    {
        var all = All;
        for (int i = 0; i < all.Count; i++)
            if (id.SequenceEqual(all[i].Id)) return i;
        return -1;
    }

    /// <summary>The hint <paramref name="id"/>, or null.</summary>
    public static HintDef? Find(string id) => IndexOf(id) is >= 0 and var i ? All[i] : null;

    static readonly string[] s_daylistDials = ["appearance.daylistClock", "notifications.topic.daylistRefresh"];

    static IReadOnlyList<HintDef> Build()
    {
        HintDef[] all =
        [
            // 1 — the lyrics pane's footer, every theme and the full-screen lyrics.
            new()
            {
                Id = "ai-lyrics", Target = "lyrics.ai", Surface = HintSurfaces.LyricsFooter, Action = HintAction.Link, Priority = 4,
                TextKey = Strings.Hints.AiLyrics.Text, ActionKey = Strings.Hints.AiLyrics.Action, MaxShows = 3, Drawn = true,
                Trigger = HintFacts.AiLyricsWanted, Satisfied = static () => Platform.Settings.Get(Platform.Keys.AiLyricsEnabled),
            },
            // 2 — the devices flyout's quality row: a second line under what plays.
            new()
            {
                Id = "lossless", Target = "sound.quality", Surface = HintSurfaces.DevicePicker, Action = HintAction.Link, Priority = 2,
                TextKey = Strings.Hints.Lossless.Text, ActionKey = Strings.Hints.Lossless.Action, MaxShows = 3, Drawn = true,
                Trigger = HintFacts.LosslessOffered, Satisfied = HintFacts.LosslessChosen,
            },
            // 3 — the same row, when the metered cap is what lowers the quality now: state, every time.
            new()
            {
                Id = "metered-cap", Target = "sound.meteredQuality", Surface = HintSurfaces.DevicePicker, Action = HintAction.State,
                Priority = 0, TextKey = Strings.Hints.MeteredCap.Text, ActionKey = Strings.Hints.MeteredCap.Action, Drawn = true,
                Trigger = HintFacts.MeteredLowers,
            },
            // 4 — the queue panel, pointing at its own Crossfade switch (it draws no second control on that screen).
            new()
            {
                Id = "crossfade", Target = "playback.crossfade", Surface = HintSurfaces.QueuePanel, Action = HintAction.Point, Priority = 7,
                TextKey = Strings.Hints.Crossfade.Text, Anchor = "queue:crossfade", MaxShows = 2, Drawn = true,
                Trigger = HintFacts.CuttingTails, Satisfied = static () => Platform.Settings.Get(Platform.Keys.CrossfadeEnabled),
            },
            // 5 — by the player bar's volume slider.
            new()
            {
                Id = "normalization", Target = "sound.normalization", Surface = HintSurfaces.PlayerVolume, Action = HintAction.Inline,
                Priority = 6, TextKey = Strings.Hints.Normalization.Text, ActionKey = Strings.Hints.Normalization.Action,
                ChangeTarget = "sound.normalization.mode", MaxShows = 2, Drawn = true,
                Trigger = HintFacts.UnevenLoudness, Satisfied = static () => Platform.Settings.Get(Platform.Keys.NormalizationEnabled),
            },
            // 6 — the devices flyout's last row: the equalizer's state at rest, a door to its dial. Phase 5's "Sound settings" row IS it (the
            // equalizer and normalization at rest, a link into Sound), drawn in place by every theme's picker, never through the slot.
            new()
            {
                Id = "equalizer-state", Target = "sound.equalizer", Surface = HintSurfaces.DevicePicker, Action = HintAction.State,
                Priority = 0, TextKey = Strings.Hints.EqualizerState.Off, Words = HintFacts.EqualizerWords, Drawn = true, InPlace = true,
                Trigger = static () => true,
            },
            // 7 — the bell flyout's header.
            new()
            {
                Id = "windows-notifications", Target = "notifications.windows", Surface = HintSurfaces.BellHeader, Action = HintAction.Inline,
                Priority = 5, TextKey = Strings.Hints.WindowsNotifications.Text, ActionKey = Strings.Hints.WindowsNotifications.Action,
                ChangeTarget = "notifications", MaxShows = 2, Drawn = true,
                Trigger = HintFacts.WantsBanners, Satisfied = static () => Platform.Settings.Get(Platform.Keys.NotifyWindows),
            },
            // 8 — an InfoBar line at the top of Home, on a later launch.
            new()
            {
                Id = "close-to-tray", Target = "system.tray.closeToTray", Surface = HintSurfaces.HomeBanner, Action = HintAction.Inline,
                Priority = 3, TextKey = Strings.Hints.CloseToTray.Text, ActionKey = Strings.Hints.CloseToTray.Action,
                MaxShows = 1, DismissRetires = true, Drawn = true,
                Trigger = HintFacts.QuitsByAccident, Satisfied = static () => Platform.Settings.Get(Platform.Keys.TrayCloseToTray),
            },
            // 9 — the same InfoBar slot; never in the same session as #8 (one new hint per session holds it).
            new()
            {
                Id = "start-with-windows", Target = "system.startOnLogin", Surface = HintSurfaces.HomeBanner, Action = HintAction.Inline,
                Priority = 8, TextKey = Strings.Hints.StartWithWindows.Text, ActionKey = Strings.Hints.StartWithWindows.Action,
                MaxShows = 1, DismissRetires = true, Drawn = true,
                Trigger = HintFacts.OpensAtSignIn, Satisfied = static () => Platform.Settings.Get(Platform.Keys.StartOnLogin),
            },
            // 10 — the back button (WinUI) / a line under the page head (Classic); only where the dial is shown (not Zune).
            new()
            {
                Id = "fast-navigation", Target = "appearance.fastNavigation", Surface = HintSurfaces.PageHead, Action = HintAction.Inline,
                Priority = 11, TextKey = Strings.Hints.FastNavigation.Text, ActionKey = Strings.Hints.FastNavigation.Action,
                MaxShows = 1, DismissRetires = true, Drawn = true,
                Trigger = HintFacts.WaitsForSwaps, Satisfied = static () => Platform.Settings.Get(Platform.Keys.FastNavigation),
                Applies = static () => !ShellCaps.Now.DefersPageMount,
            },
            // 11 — WinUI: a TeachingTip on the title bar, whose right-click menu offers Backdrop (5.4). A theme option's hint exists only
            // where the active theme declares the option (never "if the theme is WinUI", WVTH004).
            new()
            {
                Id = "backdrop", Target = SettingsSpec.ThemeOptionPrefix + "style.backdrop", Surface = HintSurfaces.TitleBar,
                Action = HintAction.Point, Priority = 12, TextKey = Strings.Hints.Backdrop.Text, Anchor = "titlebar", MaxShows = 1, Drawn = true,
                Trigger = HintFacts.AdjustingTheLook, Satisfied = HintFacts.BackdropChanged, Applies = HintFacts.BackdropOffered,
            },
            // 12 — the daylist tile's "..." menu: a one-time dot on its options item ("Customize widget" / "Options" / "options": WinUI's
            // InfoBadge dot, Classic's dot, Zune's accent word), asked by each theme's tile rows as the menu opens (HomeTileMenus.OptionsAttention).
            new()
            {
                Id = "daylist-options", Target = "appearance.daylistClock", Surface = HintSurfaces.DaylistMenu, Action = HintAction.Badge,
                Priority = 10, TextKey = Strings.Hints.DaylistOptions.Text, MaxShows = 1, RetiredBy = s_daylistDials, Drawn = true,
                Trigger = HintFacts.DaylistRolledOnHome,
            },
            // 13 — by the play button, after many mouse clicks on it.
            new()
            {
                Id = "shortcuts", Target = "help.shortcuts.playPause", Surface = HintSurfaces.PlayButton, Action = HintAction.Link,
                Priority = 13, TextKey = Strings.Hints.Shortcuts.Text, ActionKey = Strings.Hints.Shortcuts.Action, MaxShows = 2, Drawn = true,
                TitleKey = Strings.Settings.Groups.Help.Shortcuts,
                Trigger = HintFacts.ClicksPlayALot,
            },
            // 14 — the player bar while an audiobook plays late. The sleep timer (Podcasts/SleepTimer.cs, its only home) is not on this
            // line yet: the facts are fed, the hint applies once the timer exists (its "no timer is set" then joins the trigger).
            new()
            {
                Id = "sleep-timer", Target = "pointer.sleepTimer", Surface = HintSurfaces.PlayerBar, Action = HintAction.Link,
                Priority = 9, TextKey = Strings.Hints.SleepTimer.Text, ActionKey = Strings.Hints.SleepTimer.Action, MaxShows = 2,
                Trigger = HintFacts.ListeningAtBedtime, Applies = static () => false,
            },
            // 16 — the Settings home's Suggestions only, until resolved (drawn by every theme's Settings home since phase 4b).
            new()
            {
                Id = "cache-full", Target = "storage.cache.budget", Surface = HintSurfaces.SettingsHome, Action = HintAction.Link,
                Priority = 1, Drawn = true, TextKey = Strings.Hints.CacheFull.TextKey, ActionKey = Strings.Hints.CacheFull.Action, Words = HintFacts.CacheWords,
                Trigger = HintFacts.CacheAlmostFull,
            },
        ];
        Validate(all);
        return all;
    }

    /// <summary>Every target a hint names must exist (a typo fails loudly, at the first use of the table), and an inline write needs a
    /// switch. Pointers and theme options (live, per theme) are not registry dials.</summary>
    static void Validate(HintDef[] all)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var def in all)
        {
            if (!seen.Add(def.Id)) throw new InvalidOperationException("Hints: the id '" + def.Id + "' is declared twice.");
            if (def.Id.AsSpan().IndexOfAny("|;,=") >= 0) throw new InvalidOperationException("Hints: '" + def.Id + "' holds a blob separator.");
            if (!IsDial(def.Target)) continue;
            SettingsDial dial = SettingsSpec.Dial(def.Target);
            if (def.Action == HintAction.Inline && dial.Kind != SettingsRowKind.Toggle)
                throw new InvalidOperationException("Hints: '" + def.Id + "' writes inline, but '" + def.Target + "' is no switch.");
            foreach (string other in def.RetiredBy ?? []) _ = SettingsSpec.Dial(other);
        }
    }

    /// <summary>The dial a hint is about (a registry id or a live theme option's), or null for a feature pointer.</summary>
    static string? DialOf(HintDef def) => def.Target.StartsWith("pointer.", StringComparison.Ordinal) ? null : def.Target;

    /// <summary>A registry dial (not a feature pointer, not a live theme option).</summary>
    static bool IsDial(string target)
        => !target.StartsWith("pointer.", StringComparison.Ordinal) && !target.StartsWith(SettingsSpec.ThemeOptionPrefix, StringComparison.Ordinal);

    /// <summary>The zero box a slot renders while no hint is up: out of layout flow and hit-testing.</summary>
    public static readonly BoxEl Nothing = new() { Width = 0f, Height = 0f, Grow = 0f, Shrink = 0f, HitTestVisible = false };

    // ── session state ────────────────────────────────────────────────────────────────────────────────────────────────────

    static string? s_sessionHint;       // the one counted hint this session has shown
    static string? s_forced;            // `hint show`: forced for the next mount of its surface
    static object? s_onScreen;          // the slot that holds the one on-screen hint
    static string? s_onScreenId;
    static string? s_done;              // the hint whose inline action ran on the slot showing it
    static bool[]? s_aside;             // set aside for this session, by index in All

    static bool IsAside(int i) => s_aside is { } aside && aside[i];

    static int Today => DateOnly.FromDateTime(DateTime.Now).DayNumber;

    /// <summary>"Tips about features" is on.</summary>
    public static bool TipsOn => Platform.Settings.Get(Platform.Keys.TipsEnabled);

    /// <summary>Why a hint does or does not show right now (the inspector's <c>hint list</c>, and the one gate order).</summary>
    public enum Verdict : byte
    {
        /// <summary>It would show (its trigger holds and the budget allows it).</summary>
        Qualifies,
        /// <summary>Its trigger does not hold.</summary>
        Waiting,
        NotHere,
        TipsOff,
        Retired,
        Snoozed,
        ShownOut,
        ShownToday,
        OtherThisSession,
        Satisfied,
        NotDrawn,
        Blocked,
        /// <summary>Set aside for this session (a click elsewhere closed it, its link was followed, its confirmation is done).</summary>
        SetAside,
    }

    /// <summary>The budget's verdict for <paramref name="def"/> (see the file header). <paramref name="inApp"/> applies the gates that
    /// hold only in the app (drawn, one per session, never-when); the Settings home passes false. Pure: retires nothing.</summary>
    public static Verdict Judge(HintDef def, bool inApp, bool overlayOpen = false)
    {
        if (!def.Applies()) return Verdict.NotHere;
        if (inApp && !def.Drawn) return Verdict.NotDrawn;
        if (def.IsState) return def.Trigger() ? Verdict.Qualifies : Verdict.Waiting;
        if (!TipsOn) return Verdict.TipsOff;
        int i = IndexOf(def.Id);
        if (HintState.Retired(i)) return Verdict.Retired;
        if (inApp && IsAside(i)) return Verdict.SetAside;
        int today = Today;
        if (HintState.SnoozedUntil(i) > today) return Verdict.Snoozed;
        bool thisSession = string.Equals(s_sessionHint, def.Id, StringComparison.Ordinal);
        if (def.MaxShows > 0 && HintState.Shows(i) >= def.MaxShows && !thisSession) return Verdict.ShownOut;
        if (HintState.LastDay(i) == today && !thisSession && inApp) return Verdict.ShownToday;
        if (inApp && s_sessionHint is not null && !thisSession) return Verdict.OtherThisSession;
        if (def.Satisfied()) return Verdict.Satisfied;
        if (!def.Trigger()) return Verdict.Waiting;
        if (inApp && Blocked(def, overlayOpen, out _)) return Verdict.Blocked;
        return Verdict.Qualifies;
    }

    /// <summary>The never-when list (6.2): the setup wizard, another dialog or flyout (a hint inside a flyout is that flyout's), the
    /// window hidden or minimised, the first minutes after launch, the first seconds of a track. <paramref name="recheckMs"/> says when
    /// a timed gate opens (0 = it opens on an event, or none holds).</summary>
    static bool Blocked(HintDef def, bool overlayOpen, out long recheckMs)
    {
        recheckMs = 0;
        if (Setup.WizardOpen.Peek() || Setup.Covering.Peek() != Setup.Cover.None) return true;
        if (overlayOpen && !InFlyout(def.Surface)) { recheckMs = HintRules.BlockedRecheckMs; return true; }
        if (!Residency.WindowOnScreen()) { recheckMs = HintRules.BlockedRecheckMs; return true; }
        long sinceLaunch = Log.SinceStartMs;
        if (sinceLaunch < HintRules.LaunchGraceMs) { recheckMs = HintRules.LaunchGraceMs - sinceLaunch; return true; }
        long position = Playback.PositionMs.Peek();
        if (Playback.IsPlaying.Peek() && position < HintRules.TrackStartGraceMs) { recheckMs = HintRules.TrackStartGraceMs - position; return true; }
        return false;
    }

    static bool InFlyout(string surface) => surface is HintSurfaces.DevicePicker or HintSurfaces.BellHeader or HintSurfaces.DaylistMenu;

    /// <summary>The hint the slot on <paramref name="surface"/> shows now, or null; <paramref name="recheckMs"/> says when to ask again
    /// (0 = only when <see cref="Epoch"/> moves). A forced hint (<c>hint show</c>) wins over everything; then a hint whose inline action
    /// ran on this slot keeps its confirmation; then state lines; then the qualifying hint of the lowest priority number.</summary>
    public static HintDef? Pick(string surface, object slot, bool overlayOpen, out long recheckMs)
    {
        recheckMs = 0;
        var all = All;
        if (s_forced is { } forced && Find(forced) is { } f && f.Surface == surface && !f.InPlace) return f;
        if (s_done is { } done && ReferenceEquals(s_onScreen, slot) && Find(done) is { } d && d.Surface == surface) return d;
        HintDef? best = null;
        for (int i = 0; i < all.Count; i++)
        {
            HintDef def = all[i];
            if (def.InPlace || !string.Equals(def.Surface, surface, StringComparison.Ordinal)) continue;
            if (best is not null && Rank(best) <= Rank(def)) continue;
            Verdict verdict = Judge(def, inApp: true, overlayOpen);
            // Already up on this slot: the never-when gates decide whether a hint may appear, they never pull one that is up.
            if (verdict == Verdict.Blocked && ReferenceEquals(s_onScreen, slot) && string.Equals(s_onScreenId, def.Id, StringComparison.Ordinal))
                verdict = Verdict.Qualifies;
            // Shown before and its state now reached: retired for good (the blob only: no signal is written in a render, and no slot
            // shows it to need a re-run).
            if (verdict == Verdict.Satisfied) { if (HintState.Shows(i) > 0) HintState.MarkRetired(i); continue; }
            if (verdict == Verdict.Blocked) { Blocked(def, overlayOpen, out long wait); recheckMs = Math.Max(HintRules.MinRecheckMs, wait); continue; }
            if (verdict == Verdict.Waiting) recheckMs = MinPositive(recheckMs, HintFacts.RecheckMs(def));
            if (verdict != Verdict.Qualifies) continue;
            if (!def.IsState && s_onScreen is not null && !ReferenceEquals(s_onScreen, slot)) continue;   // one hint on screen in the whole app
            best = def;
        }
        return best;
    }

    static int Rank(HintDef def) => def.IsState ? 0 : def.Priority;

    static long MinPositive(long a, long b) => a <= 0 ? b : b <= 0 ? a : Math.Min(a, b);

    // ── what a slot reports ──────────────────────────────────────────────────────────────────────────────────────────────

    static readonly object s_menuBadge = new();   // the "slot" a menu's dot is picked for: it never holds the on-screen place

    /// <summary>A one-time dot on a menu item (<see cref="HintAction.Badge"/>) on <paramref name="surface"/>, asked when that menu builds its rows
    /// (open time): true when its hint qualifies (or is forced), and then the show is recorded (the session's one hint, once a day against its
    /// budget). A menu is a flyout of its own: the dot holds no on-screen place and goes with the menu; it yields to a hint already on screen.</summary>
    public static bool BadgeOnOpen(string surface)
    {
        if (Pick(surface, s_menuBadge, overlayOpen: true, out _) is not { Action: HintAction.Badge } def) return false;
        if (!IsForced(def))
        {
            s_sessionHint ??= def.Id;
            HintState.MarkShown(IndexOf(def.Id), Today);
        }
        return true;
    }

    /// <summary>The item that carried the dot on <paramref name="surface"/> was chosen: it opened the place the dot pointed at, so the dot has done
    /// its job for this session (set aside; a write of its dials retires it for good).</summary>
    public static void BadgeFollowed(string surface)
    {
        var all = All;
        for (int i = 0; i < all.Count; i++)
            if (all[i].Action == HintAction.Badge && string.Equals(all[i].Surface, surface, StringComparison.Ordinal)) { SetAside(all[i]); return; }
    }

    /// <summary>The slot mounted <paramref name="def"/>: it holds the one on-screen place, and (unless forced) the show is recorded: the
    /// session's one hint, once per day against its show budget.</summary>
    public static void Shown(HintDef def, object slot)
    {
        if (def.IsState) return;
        s_onScreen = slot;
        s_onScreenId = def.Id;
        if (IsForced(def)) return;
        s_sessionHint ??= def.Id;
        HintState.MarkShown(IndexOf(def.Id), Today);
    }

    /// <summary>The slot unmounted or shows nothing any more: it gives the on-screen place back, and a confirmation ends there.</summary>
    public static void Released(object slot)
    {
        if (!ReferenceEquals(s_onScreen, slot)) return;
        s_onScreen = null;
        s_onScreenId = null;
        s_done = null;
        Bump();
    }

    /// <summary>The hint is forced by <c>hint show</c>.</summary>
    public static bool IsForced(HintDef def) => string.Equals(s_forced, def.Id, StringComparison.Ordinal);

    /// <summary>The inspector's <c>hint show</c>: the next mount of <paramref name="def"/>'s surface shows it, whatever its trigger and
    /// budget say; it records no show. Cleared by the next inspector mount (<see cref="ClearForced"/>).</summary>
    public static void Force(HintDef def)
    {
        s_forced = def.Id;
        Bump();
    }

    public static void ClearForced()
    {
        if (s_forced is null) return;
        s_forced = null;
        Bump();
    }

    /// <summary>The id <c>hint show</c> forced, or null.</summary>
    public static string? Forced => s_forced;

    /// <summary>The id of the one hint on screen, or null.</summary>
    public static string? OnScreen => s_onScreenId;

    /// <summary>The id of the counted hint this session has shown, or null.</summary>
    public static string? SessionHint => s_sessionHint;

    /// <summary>Forget this session's hint state (the inspector's <c>hint reset</c>: as a fresh launch with an empty blob).</summary>
    public static void ResetSession()
    {
        s_sessionHint = s_forced = s_onScreenId = s_done = null;
        s_onScreen = null;
        if (s_aside is { } aside) Array.Clear(aside);
        HintFacts.ResetSession();
        Bump();
    }

    // ── the verbs a hint hands its view ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>"×" / "Not now". On a confirmation (the inline action ran) it only closes it: the hint is retired already.</summary>
    public static void Dismiss(HintDef def)
    {
        if (def.IsState) return;
        if (IsDone(def)) { SetAside(def); return; }
        HintState.MarkDismissed(IndexOf(def.Id), Today, def.SnoozeDays, def.DismissRetires);
        if (IsForced(def)) s_forced = null;
        Bump();
    }

    /// <summary>A click elsewhere closed it (a TeachingTip's light dismiss), its link was followed, or its confirmation is done: gone for
    /// the rest of this session, with no snooze (its show was counted when it appeared).</summary>
    public static void SetAside(HintDef def)
    {
        if (def.IsState) return;
        (s_aside ??= new bool[All.Count])[IndexOf(def.Id)] = true;
        if (IsDone(def)) s_done = null;
        if (IsForced(def)) s_forced = null;
        Bump();
    }

    /// <summary>The confirmation of <paramref name="def"/> is up (its inline action ran on the slot showing it).</summary>
    public static bool IsDone(HintDef def) => string.Equals(s_done, def.Id, StringComparison.Ordinal);

    /// <summary>"Don't show tips": the global switch off, through its dial's writer.</summary>
    public static void NeverTips()
    {
        SettingsSpec.Dial(TipsDialId).WriteToggle(false, SettingsSurfaces.Hint);
        s_forced = null;
        Bump();
    }

    /// <summary>The hint's one action: an inline write through the dial's own writer (the hint then shows its confirmation and retires),
    /// a deep link to the dial, or opening the pointer's place.</summary>
    public static void Act(HintDef def)
    {
        switch (def.Action)
        {
            case HintAction.Inline:
                s_done = def.Id;
                SettingsSpec.Dial(def.Target).WriteToggle(true, SettingsSurfaces.Hint);   // retires every hint about the dial (OnDialWritten)
                Bump();
                break;
            default:
                // A followed link has done its job for this session (the surface that carried it closes as Settings opens). The in-context
                // door: an overlaid rail or the full screen steps aside for Settings, as every other "open in Settings" does.
                if (DialOf(def) is { } dial) InContextDoors.OpenSettings(dial);   // a pointer's place opens from its own surface
                if (!def.IsState) SetAside(def);
                break;
        }
    }

    /// <summary>A dial was written from anywhere (<see cref="SettingsWrites"/>): every hint about it retires; the trigger facts that are a
    /// dial write (App mode while the backdrop is Mica) are noted; the tips switch re-evaluates every slot.</summary>
    internal static void OnDialWritten(string dialId)
    {
        bool changed = HintFacts.DialWritten(dialId) || string.Equals(dialId, TipsDialId, StringComparison.Ordinal);
        var all = All;
        for (int i = 0; i < all.Count; i++)
        {
            HintDef def = all[i];
            if (def.IsState || HintState.Retired(i)) continue;
            bool about = def.RetiredBy is { } dials ? Contains(dials, dialId) : string.Equals(def.Target, dialId, StringComparison.Ordinal);
            if (!about) continue;
            HintState.MarkRetired(i);
            changed = true;
        }
        if (changed) Bump();
    }

    static bool Contains(IReadOnlyList<string> list, string value)
    {
        for (int i = 0; i < list.Count; i++)
            if (string.Equals(list[i], value, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>The global switch's dial.</summary>
    public const string TipsDialId = "notifications.tips";

    // ── the Settings home's Suggestions (owner decision 14) ──────────────────────────────────────────────────────────────

    /// <summary>The top three hints that qualify for the Settings home, in priority order: hints with words (not a state line, not a dot)
    /// whose trigger holds and whose budget allows a show. Pure; the page records what it showed with <see cref="SuggestionShown"/>.</summary>
    public static IReadOnlyList<HintDef> Suggestions()
    {
        var picked = new List<HintDef>(HintRules.SuggestionCap);
        var all = All;
        for (int rank = 1; rank <= all.Count && picked.Count < HintRules.SuggestionCap; rank++)
            for (int i = 0; i < all.Count && picked.Count < HintRules.SuggestionCap; i++)
            {
                HintDef def = all[i];
                if (def.Priority != rank || def.IsState || def.Action == HintAction.Badge) continue;
                if (Judge(def, inApp: false) == Verdict.Qualifies) picked.Add(def);
            }
        return picked;
    }

    /// <summary>The Settings home showed <paramref name="def"/> as a suggestion: it counts against the same show budget (once per day).</summary>
    public static void SuggestionShown(HintDef def) => HintState.MarkShown(IndexOf(def.Id), Today);

    // ── props ────────────────────────────────────────────────────────────────────────────────────────────────────────────

    static Action[]? s_act, s_dismiss, s_change, s_setAside;
    static readonly Action s_neverTips = NeverTips;

    /// <summary>The props of <paramref name="def"/> on <paramref name="surface"/> (built only for a hint that shows). The verbs are one
    /// delegate per hint, built once, so an unchanged hint re-pushes an equal record. <paramref name="target"/> is the control the
    /// surface's slot points at (null where the hint sits in a place of its own); <paramref name="close"/> closes the flyout the hint sits in
    /// (the devices picker's), so a door to Settings never leaves it open over the page (the bell's panel closes through its own closer).</summary>
    public static HintProps PropsFor(HintDef def, string surface, Func<NodeHandle>? target = null, Action? close = null)
    {
        int i = IndexOf(def.Id);
        s_act ??= new Action[All.Count];
        s_dismiss ??= new Action[All.Count];
        s_change ??= new Action[All.Count];
        s_setAside ??= new Action[All.Count];
        Action act = s_act[i] ??= () => Act(def);
        Action dismiss = s_dismiss[i] ??= () => Dismiss(def);
        Action setAside = s_setAside[i] ??= () => SetAside(def);
        // The confirmation's "Change" opens the dial and the confirmation goes with it (its word was said).
        Action change = s_change[i] ??= () => { InContextDoors.OpenSettings(def.ChangeTarget ?? def.Target); SetAside(def); };
        // A door out of a flyout closes the flyout first (an inline write stays: its confirmation shows in place).
        close ??= surface == HintSurfaces.BellHeader ? NotificationVerbs.PanelCloser : null;
        if (close is { } closer)
        {
            if (def.Action != HintAction.Inline) { Action open = act; act = () => { closer(); open(); }; }
            Action changeThere = change;
            change = () => { closer(); changeThere(); };
        }
        bool done = string.Equals(s_done, def.Id, StringComparison.Ordinal);
        bool button = def.ActionKey is not null && def.Action is HintAction.Inline or HintAction.Link or HintAction.State;
        // On the Settings home every suggestion is a door: a pointer's or a dot's row opens its dial.
        bool door = surface == HintSurfaces.SettingsHome || def.IsState;
        return new HintProps
        {
            Surface = surface, Id = def.Id, Action = def.Action, DialId = DialOf(def),
            Text = def.Words is { } words ? words() : Loc.Get(def.TextKey),
            ActionLabel = button ? Loc.Get(def.ActionKey!) : null,
            Act = button || door ? act : null,
            Dismiss = def.IsState ? null : dismiss,
            NeverTips = def.IsState ? null : s_neverTips,
            Done = done,
            DoneText = done ? DoneWords(def) : null,
            ChangeLabel = done ? Loc.Get(Strings.Hints.Done.Change) : null,
            Change = done ? change : null,
            Anchor = def.Anchor,
            Forced = IsForced(def),
            Target = target,
            SetAside = def.IsState ? null : setAside,
            Title = TitleOf(def),
        };
    }

    /// <summary>The confirmation after an inline write (design 6.3): "On", and the value the "Change" link opens when that is a dial with a
    /// value of its own ("On · Normal" for normalization's level).</summary>
    static string DoneWords(HintDef def)
    {
        string on = Loc.Get(Strings.Hints.Done.On);
        if (def.ChangeTarget is not { } id || !SettingsSpec.TryDial(id, out SettingsDial dial)) return on;
        string value = SettingsPageModel.ValueWords(dial, dial.Current);
        return value.Length == 0 ? on : on + " · " + value;
    }

    /// <summary>The heading of <paramref name="def"/>: its own title key, else its dial's title (a live theme option's included), else null.</summary>
    static string? TitleOf(HintDef def)
    {
        if (def.TitleKey is { } key) return Loc.Get(key);
        if (DialOf(def) is not { } id) return null;
        return SettingsSpec.TryResolve(id, out SettingsDial dial, out _) ? Loc.Get(dial.TitleKey) : null;
    }
}
