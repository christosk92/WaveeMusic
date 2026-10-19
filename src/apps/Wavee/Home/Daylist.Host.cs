// ── Home/Daylist.Host.cs — the daylist card's and its clock's CORE HOSTS (T10a1; home rebuild, fifth → seventh pass) ───────
//
// Role: CORE host (the card's picture is Classic's `Home.Daylist` view and the clock's faces its `Home.DaylistClock` view:
//       `Themes\Classic\Structure\Views\Home\ClassicHomeDaylist.cs`, `ClassicHomeDaylistClock.cs`)
// Spec: docs/plans/wavee/home-rebuild-implementation.md "Fifth pass — stock controls" (zone → control table, Daylist row)
//        + "Seventh pass" (the daypart timeline) + docs/plans/wavee/daylist-hero-fade-implementation.md (variant A: the
//        header art full-bleed inside the card, under the artist hero's veil).
//
// WHAT THE HOSTS KEEP. `DaylistCard` reads what must SUBSCRIBE (the greeted name, whether the ▶ names Pause), builds the clock's keyed
// embed and mounts `Home.Daylist`. `DaylistClock` owns the clock's three faces' STATE: the window's phase (a 1-Hz watch that only writes
// on the flip, so a tick never re-renders it), the rollover ladder's signal, the clock MODE (Settings > Appearance > Daylist clock,
// #185: Both, Update time only, Countdown only) and the one-shot at the window's end; it keys the face by (window, face) so a face change
// remounts only the clock's leaf. `DaylistCountingClock` owns the 1-Hz tick that writes the caller-owned signals (the countdown text, the
// elapsed fraction, one fill per segment); every one of them reaches the screen through a BIND (the first line's bound spans, the ring,
// the bars' indicator widths): `Render` reads none of them, so neither this leaf nor anything above it re-renders on the tick. Props
// freeze at mount; the card keys the clock on its window and daypart, so a new window remounts it.
//
// The view's half is the picture: the card's layers (art, veil, copy), the action row, the tag line, the clock block and its timeline.
// The card's pure rules stay core: `DaylistForm` / `DaypartTimeline` / `DaylistCountdownLine` / `DaylistArt` (Items.Rules.cs) and
// `DaypartRules` / `DaylistCountdown` / `DaylistNext` / `DaylistClockFace`.

using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;
using Slots = FluentGpu.Theming.Slots;

namespace Wavee.HomeUi;

/// <summary>Props for <see cref="DaylistCard"/> — the zone by value (a re-plan with equal content gates the re-render at
/// the embed boundary), the overlay service by reference.</summary>
public sealed record DaylistProps(Zone Zone, IOverlayService? Overlay)
{
    public bool Equals(DaylistProps? o) => o is not null && Zone.Equals(o.Zone) && ReferenceEquals(Overlay, o.Overlay);
    public override int GetHashCode() => Zone.GetHashCode();
}

/// <summary>The daylist card's host: the subscribing reads, the clock's embed, and the `Home.Daylist` slot.</summary>
[CoreHost]
public sealed class DaylistCard : Component
{
    public override Element Render()
    {
        var p = UseProps<DaylistProps>();
        var zone = p.Zone;
        string eyebrow = Eyebrow();                          // subscribing read (users table + scope) — hooks first

        var host = Controls.IsNullOverlay(p.Overlay) ? null : p.Overlay;
        bool has = zone.Items.Count > 0 && zone.Items[0].Uri.Length > 0;
        if (!has)
            return Slots.View(ViewKeys.HomeDaylist, new HomeDaylistProps { Zone = zone, Overlay = host, Eyebrow = eyebrow });

        var card = zone.Items[0];
        string uri = card.Uri;
        // The ▶ names what a press does: Pause while this daylist is the context playing (a subscribing, coarse-first read).
        bool pauses = PlayRelation.ShowsPause(uri);

        // The clock's props freeze at mount, so a new window (or a new current daypart) remounts it through its key.
        var current = DaypartTimeline.Current(card.Title, DateTime.Now.Hour);
        long expiresAtMs = card.ExpiresAtMs, createdAtMs = card.CreatedAtMs;
        var clock = Embed.Comp(() => new DaylistClock { ExpiresAtMs = expiresAtMs, CreatedAtMs = createdAtMs, Current = current })
            with { Key = "daylist-clock:" + uri + ":" + expiresAtMs.ToString(CultureInfo.InvariantCulture) + ":" + ((int)current).ToString(CultureInfo.InvariantCulture) };

        return Slots.View(ViewKeys.HomeDaylist, new HomeDaylistProps
        {
            Zone = zone, Overlay = host, Eyebrow = eyebrow, Pauses = pauses, Clock = clock,
        });
    }

    /// <summary>"Good morning, Chris · your daylist" — a local hour-based greeting paired with the signed-in account's
    /// name (<see cref="Zone"/> carries no greeting field, so the server's own string never reaches this card). The
    /// page has no greeting headline: this eyebrow is the greeting.</summary>
    static string Eyebrow()
    {
        string part = GreetingPart();
        string? who = GreetedName();
        string yours = Loc.Get(Strings.Home.YourDaylist);
        return who is null ? part + " · " + yours : Strings.Home.HeroEyebrow(part, who, yours);
    }

    /// <summary>The local-clock part of the greeting: "Good morning" / "Good afternoon" / "Good evening".</summary>
    static string GreetingPart() => DateTime.Now.Hour switch
    {
        >= 4 and < 12 => Loc.Get(Strings.Home.GoodMorning),
        >= 12 and < 17 => Loc.Get(Strings.Home.GoodAfternoon),
        _ => Loc.Get(Strings.Home.GoodEvening),
    };

    /// <summary>The page greeting, "Good evening, Chris" (the bare part while no account name is known). A SUBSCRIBING read like
    /// the card's eyebrow (contract 1.53: <see cref="Wavee.Theming.Contracts.HomePageProps.Greeting"/>), so a structure that draws a
    /// greeting headline binds it and a late login re-describes it.</summary>
    public static string Greeting()
    {
        string part = GreetingPart();
        string? who = GreetedName();
        return who is null ? part : Strings.Home.Greeting(part, who);
    }

    /// <summary>The signed-in account's greetable name, or null. A SUBSCRIBING read (the users table + the scope), so
    /// a late login re-describes the card.</summary>
    static string? GreetedName()
    {
        if (Diagnostics.StoreShot.Presenting.Value) return null;   // presentation mode: no account name in a listing image
        _ = Entities.ScopeEpoch.Value;
        _ = Entities.Current.Users.Changed.Value;
        var me = User.Me;
        if (!me.IsValid || !me.Knows(UserFields.Identity)) return null;
        string name = Entities.Strings.Resolve(me.NameId);
        return name.Length > 0 ? name : null;
    }
}

/// <summary>The card's clock, in one of three faces (<see cref="DaylistClockFace"/>): the countdown while the window is
/// open (<see cref="DaylistCountingClock"/>); once it has ended and the next edition is on its way, a stock
/// indeterminate ring beside "Updating your daylist…" / "{Next daypart} is on its way" over the timeline with the
/// finished segment full and the next one an indeterminate bar; and, when the rollover ladder gave up (or never ran —
/// offline), a static ring beside "Your next daylist is running late" and a "Check again" link that restarts the ladder
/// (<see cref="Home.Feeds.CheckDaylistAgain"/>). This host reads the window's phase (a 1-Hz watch that only writes on the
/// flip, so a tick never re-renders it) and the ladder's signal, and keys the face by (window, face) so a face change
/// remounts only the clock's leaf. Props freeze at mount; the card keys this host on its window and daypart.
/// <para>It also reads the clock MODE (<see cref="Prefs.Appearance.DaylistClock"/>, #185) and keys the Counting face on
/// it, so a pick in Settings swaps the face live. Update time only mounts the static update-time face, which has nothing
/// that changes per second: the 1-Hz watch stays off and a ONE-SHOT at the window's end
/// (<see cref="DaylistClockShows.ExpiryDelayMs"/>) flips the face instead; it re-checks the wall clock when it fires and
/// re-arms if it landed early.</para></summary>
public sealed class DaylistClock : Component
{
    public required long ExpiresAtMs { get; init; }
    public required long CreatedAtMs { get; init; }
    public required Daypart Current { get; init; }

    readonly Signal<int> _flip = new(0);
    readonly Action _watch, _onExpiry;
    TimerHandle _expiry;

    public DaylistClock()
    {
        _watch = Watch;
        _onExpiry = OnExpiry;
    }

    /// <summary>The 1-Hz watch while counting: the first tick past the window re-renders this host once.</summary>
    void Watch()
    {
        if (DaylistCountdown.PhaseOf(ExpiresAtMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) != DaylistCountdown.Phase.Counting)
            _flip.Value++;
    }

    /// <summary>The one-shot at the window's end: flip once, or re-arm for the rest if the timer landed early.</summary>
    void OnExpiry()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (DaylistCountdown.PhaseOf(ExpiresAtMs, now) == DaylistCountdown.Phase.Counting)
            _expiry.RestartIn(DaylistClockShows.ExpiryDelayMs(ExpiresAtMs, now));
        else _flip.Value++;
    }

    public override Element Render()
    {
        _ = _flip.Value;                                           // the window ended: swap the face once
        var ladder = Home.Feeds.RolloverState.Value;               // the ladder moved: Updating ↔ Late
        var mode = HomeHost.DaylistClock();                        // Settings picked another clock: swap the Counting face
        long expiresAtMs = ExpiresAtMs, createdAtMs = CreatedAtMs;
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var phase = DaylistCountdown.PhaseOf(expiresAtMs, nowMs);
        UseInterval(_watch, 1000f, enabled: DaylistClockShows.SecondTick(mode, phase));
        _expiry = UseTimeout(_onExpiry, DaylistClockShows.ExpiryDelayMs(expiresAtMs, nowMs), DepKey.From(expiresAtMs, (long)mode));
        if (expiresAtMs <= 0) return new BoxEl();

        var face = DaylistClockFace.Of(phase, ladder);
        var current = Current;
        bool showUpdateTime = DaylistClockShows.UpdateTime(mode);
        string key = "face:" + expiresAtMs.ToString(CultureInfo.InvariantCulture) + ":" + ((int)face).ToString(CultureInfo.InvariantCulture)
                   + (face == DaylistClockFace.Face.Counting ? ":" + ((int)mode).ToString(CultureInfo.InvariantCulture) : "");
        Element leaf = face switch
        {
            DaylistClockFace.Face.Updating => Updating(current),
            DaylistClockFace.Face.Late => Late(current),
            _ when !DaylistClockShows.Countdown(mode) => UpdateTimeFace(expiresAtMs, createdAtMs, current, nowMs),
            _ => Embed.Comp(() => new DaylistCountingClock
            {
                ExpiresAtMs = expiresAtMs, CreatedAtMs = createdAtMs, Current = current, ShowUpdateTime = showUpdateTime,
            }),
        };
        // The face is the keyed CHILD of a stretch column (a root's own Key is inert in a component's single-child slot).
        return new BoxEl { Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch, Children = [leaf with { Key = key }] };
    }

    /// <summary>The update-time-only Counting face (#185): the SAME block as the countdown — so the card keeps its height
    /// when the setting changes — with nothing on it that moves. "Next update at HH:mm" stands in for the ticking line, the
    /// second line names the next daypart without repeating the time, and the ring and the five bars are drawn once at the
    /// window's elapsed fraction as of this render (no signal is ever written to them).</summary>
    static Element UpdateTimeFace(long expiresAtMs, long createdAtMs, Daypart current, long nowMs)
    {
        float elapsed = (float)DaypartRules.Elapsed(createdAtMs, expiresAtMs, nowMs);
        var segs = new FloatSignal[DaypartTimeline.Segments];
        for (int i = 0; i < segs.Length; i++) segs[i] = new FloatSignal(DaypartTimeline.Fill(i, (int)current, elapsed));
        var tz = TimeZoneInfo.Local;
        var culture = CultureInfo.CurrentCulture;
        return Slots.View(ViewKeys.HomeDaylistClock, new HomeDaylistClockProps
        {
            Face = HomeDaylistClockFace.UpdateTime, Current = current,
            Line = DaylistNext.UpdateAt(expiresAtMs, tz, culture),
            Caption = DaylistNext.UpNext(expiresAtMs, current, tz, culture),
            ElapsedValue = elapsed, Segments = segs,
        });
    }

    /// <summary>The next edition is on its way: the card's own "Updating your daylist…" line, then "{Next daypart} is on its way" (the
    /// segment AFTER the card's current one, the countdown caption's rule); the view draws the indeterminate ring and the timeline whose
    /// next segment is an indeterminate bar.</summary>
    static Element Updating(Daypart current)
    {
        string next = Loc.Get(DaypartTimeline.LabelKey((int)DaypartRules.Next(current)));
        return Slots.View(ViewKeys.HomeDaylistClock, new HomeDaylistClockProps
        {
            Face = HomeDaylistClockFace.Updating, Current = current,
            Line = Loc.Get(Strings.Home.DaylistUpdating), Caption = Strings.Home.Daylist.OnItsWay(next),
        });
    }

    /// <summary>The ladder ran out: the view draws a full, static ring in tertiary ink, the "running late" line and the "Check again"
    /// link (small, left-aligned in the lines column) that restarts the ladder.</summary>
    static Element Late(Daypart current)
        => Slots.View(ViewKeys.HomeDaylistClock, new HomeDaylistClockProps
        {
            Face = HomeDaylistClockFace.Late, Current = current,
            Line = Loc.Get(Strings.Home.Daylist.Late), CheckAgain = Home.Feeds.CheckDaylistAgain,
        });
}

/// <summary>The countdown face's host: a 1-Hz <c>UseInterval</c> writes caller-owned signals — the countdown text, the elapsed
/// fraction, one fill per segment — and every one of them reaches the screen through a BIND in the `Home.DaylistClock` view (the first
/// line's bound spans, the ring, the bars' indicator widths): <see cref="Render"/> reads none of them, so neither this leaf nor
/// anything above it re-renders on the tick. The caption is static per mount. Props freeze at mount;
/// <see cref="DaylistClock"/> keys this leaf on its window, so a new window remounts it (and re-reads the caption).</summary>
public sealed class DaylistCountingClock : Component
{
    public required long ExpiresAtMs { get; init; }
    public required long CreatedAtMs { get; init; }
    public required Daypart Current { get; init; }
    /// <summary>FALSE for the countdown-only clock (#185): the caption names the next daypart without its time.</summary>
    public bool ShowUpdateTime { get; init; } = true;

    public override Element Render()
    {
        var buf = UseRef(new char[16]).Value;
        var countdown = UseRef(new Signal<string>("")).Value;
        var elapsed = UseRef(new FloatSignal(0f)).Value;
        var segs = UseRef(NewSegments()).Value;

        long expiresAtMs = ExpiresAtMs, createdAtMs = CreatedAtMs;
        int current = (int)Current;
        void Tick()
        {
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            int len = DaypartRules.FormatCountdown(DaylistCountdown.RemainingMs(expiresAtMs, nowMs), buf);
            countdown.Value = new string(buf, 0, len);
            float e = (float)DaypartRules.Elapsed(createdAtMs, expiresAtMs, nowMs);
            elapsed.Value = e;
            for (int i = 0; i < segs.Length; i++) segs[i].Value = DaypartTimeline.Fill(i, current, e);
        }

        UseEffect(() => Tick());                                   // seed immediately (mount)
        UseInterval(Tick, 1000f, enabled: ExpiresAtMs > 0);
        if (ExpiresAtMs <= 0) return new BoxEl();

        // The second line names the timeline segment AFTER the card's current one; static per mount. The countdown-only
        // clock drops its time ("… is up next"), keeping the line so the card's height never changes with the setting.
        string caption = ShowUpdateTime
            ? DaylistNext.Caption(expiresAtMs, Current, TimeZoneInfo.Local, CultureInfo.CurrentCulture)
            : DaylistNext.UpNext(expiresAtMs, Current, TimeZoneInfo.Local, CultureInfo.CurrentCulture);

        return Slots.View(ViewKeys.HomeDaylistClock, new HomeDaylistClockProps
        {
            Face = HomeDaylistClockFace.Counting, Current = Current, Caption = caption,
            Countdown = countdown, Elapsed = elapsed, Segments = segs,
        });
    }

    static FloatSignal[] NewSegments()
    {
        var segs = new FloatSignal[DaypartTimeline.Segments];
        for (int i = 0; i < segs.Length; i++) segs[i] = new FloatSignal(0f);
        return segs;
    }
}
