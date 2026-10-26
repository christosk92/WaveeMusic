// ── Themes/Classic/Structure/Rules/Tiers.Concert.cs — the concert surfaces' ladders and the event page's tier records (themes plan 3.6; T10b3, TC6) ───
//
// `ConcertLayout` from `Entities\Concert.Rules.cs` (0.2.9 `ConcertLayout.cs`), moved out of core (plan 3.6) and, since TC6, written with the engine's
// `TierLadder`, so the holds behave like the window tiers' (a fresh resolve uses the edge itself):
//
//   schedule   the artist's schedule (the hero's media pane and the boards' columns): wide from 760, held down to 720 once wide.
//   event      the event page's tickets rail: from 920, held down to 860 once wide.
//
// The views answer the ladders from the page's own measured width (inside `Responsive.Of`, seeded from the same-frame region estimate) and compare nothing
// themselves (WVTH013). THE EVENT PAGE'S RECORDS per window tier (boards C-Concert, C-Concert-Compact, -Small, -Huge; the hub and the schedule have no boards
// of their own and keep the tuned design built from Classic's parts, owner Q18):
//
//   tier       split hero                                    tickets                                 targets
//   Compact    stacked, a 220 photo pane                     inline after the facts                  40 (Buy, the lineup rows, the hub's
//                                                                                                    filter pills)
//   Small      split 56 / 44 at 240, pad 20                  inline                                  32
//   Standard   the tuned arms (a 320 split from 920, else    the 320 rail from 920                   32
//              the 180 stacked pane)
//   Huge       split at 400, pad 40                          a 360 rail from 920                     32
//              the lineup a 640 column beside the dates grid (four date cards a row; C-Concert-Huge's "Tour dates" geometry, filled with the
//              related concerts: the app has no tour-dates feed, so the grid carries them under their own header and the related shelf folds into it)
//
// Public, because `ConcertLayoutTests`, `ConcertSchedulePageTests` and `ConcertDetailModelTests` pin the ladders.

using FluentGpu.Foundation;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

/// <summary>Classic's concert ladders, the event page's tier records and the grid numbers.</summary>
public static class ClassicConcertTiers
{
    /// <summary>The schedule goes wide here and holds down to <see cref="ScheduleLeaveWide"/> once wide.</summary>
    public const float ScheduleEnterWide = 760f, ScheduleLeaveWide = 720f;

    /// <summary>The event page's tickets rail docks here and holds down to <see cref="DetailLeaveWide"/> once docked.</summary>
    public const float DetailEnterWide = 920f, DetailLeaveWide = 860f;

    /// <summary>The schedule's two arms (0 narrow, 1 wide).</summary>
    public static TierLadder Schedule { get; } = TierLadder.Of(new TierEdge(ScheduleEnterWide, NarrowHold: ScheduleEnterWide - ScheduleLeaveWide, WidenHold: 0f));

    /// <summary>The event page's two arms (0 narrow, 1 the tickets rail).</summary>
    public static TierLadder Detail { get; } = TierLadder.Of(new TierEdge(DetailEnterWide, NarrowHold: DetailEnterWide - DetailLeaveWide, WidenHold: 0f));

    /// <summary>The schedule's arm at <paramref name="width"/>, held against <paramref name="wasWide"/> once <paramref name="initialized"/>.</summary>
    public static bool ScheduleWide(float width, bool wasWide, bool initialized = true)
        => Schedule.Resolve(width, initialized ? (wasWide ? 1 : 0) : -1) == 1;

    /// <summary>The event page's arm at <paramref name="width"/>, held like <see cref="ScheduleWide"/>.</summary>
    public static bool DetailWide(float width, bool wasWide, bool initialized = true)
        => Detail.Resolve(width, initialized ? (wasWide ? 1 : 0) : -1) == 1;

    /// <summary>The tuned split hero: the 320 row when wide, the 180 stacked pane when narrow.</summary>
    public static EditorialHeroMetrics EditorialHero(bool wide) => wide
        ? new EditorialHeroMetrics(Height: 320f, MediaHeight: 320f, MediaFraction: 0.44f, Padding: 28f)
        : new EditorialHeroMetrics(Height: 0f, MediaHeight: 180f, MediaFraction: 1f, Padding: 20f);

    /// <summary>The event page's split hero in <paramref name="layout"/>'s tier at the page's arm (<paramref name="wide"/>).</summary>
    public static EditorialHeroMetrics EventHero(ClassicConcertLayout layout, bool wide) => layout.Hero switch
    {
        ClassicConcertHero.Split => new EditorialHeroMetrics(Height: layout.HeroHeight, MediaHeight: layout.HeroHeight, MediaFraction: 0.44f, Padding: layout.HeroPad),
        ClassicConcertHero.Stacked => new EditorialHeroMetrics(Height: 0f, MediaHeight: layout.HeroHeight, MediaFraction: 1f, Padding: layout.HeroPad),
        _ => EditorialHero(wide),
    };

    /// <summary>Compact (C-Concert-Compact; the hub's filter pills at the 40 target).</summary>
    public static ClassicConcertLayout Compact { get; } = new()
    {
        Tier = WidthTier.Compact, Hero = ClassicConcertHero.Stacked, HeroHeight = 220f, HeroPad = 16f, RailWidth = 0f, Target = ClassicArtistTiers.CompactTarget,
        HubPill = ClassicArtistTiers.CompactTarget,
    };

    /// <summary>Small (C-Concert-Small): the split hero at 240 while the tickets stay inline.</summary>
    public static ClassicConcertLayout Small { get; } = new()
    {
        Tier = WidthTier.Small, Hero = ClassicConcertHero.Split, HeroHeight = 240f, HeroPad = 20f, RailWidth = 0f, Target = ClassicArtistTiers.Target,
    };

    /// <summary>Standard: the tuned arms.</summary>
    public static ClassicConcertLayout Standard { get; } = new()
    {
        Tier = WidthTier.Standard, Hero = ClassicConcertHero.Tuned, HeroHeight = 320f, HeroPad = 28f, RailWidth = 320f, Target = ClassicArtistTiers.Target,
    };

    /// <summary>Huge (C-Concert-Huge): the split hero at 400 with 40 of air, a 360 tickets rail, the lineup a 640 column beside the four-column dates grid.</summary>
    public static ClassicConcertLayout Huge { get; } = new()
    {
        Tier = WidthTier.Huge, Hero = ClassicConcertHero.Split, HeroHeight = 400f, HeroPad = 40f, RailWidth = 360f, Target = ClassicArtistTiers.Target,
        LineupWidth = 640f, DateColumns = 4,
    };

    /// <summary>The record of window tier <paramref name="tier"/>.</summary>
    public static ClassicConcertLayout For(WidthTier tier) => tier switch
    {
        WidthTier.Compact => Compact,
        WidthTier.Small => Small,
        WidthTier.Standard => Standard,
        _ => Huge,
    };

    /// <summary>The record of <paramref name="facts"/>; Standard's where no frame resolved the tiers.</summary>
    public static ClassicConcertLayout Of(in TierFacts facts) => facts.Policy is null ? Standard : For(facts.Window);

    /// <summary>The event tile's chrome over its cell width (ch 17 §9 item 5, derived): pad-top 8 + art (cellW − 16) +
    /// gap 8 + text (16 + 3 + 20 + 3 + 16) + pad-bottom 12 = cellW + 70.</summary>
    public const float EventChrome = 70f;

    /// <summary>The all-events grid's minimum column width and its two gaps (ch 17 §3). Row extra = chrome + row gap = 86.</summary>
    public const float GridMinColumn = 240f, GridColumnGap = 12f, GridRowGap = 16f;

    /// <summary>The grid's row extra over the cell width.</summary>
    public const float GridRowExtra = EventChrome + GridRowGap;
}

/// <summary>The split hero's measures: the wide row's height and the narrow pane's media height, the photo's share and the copy padding.</summary>
public readonly record struct EditorialHeroMetrics(float Height, float MediaHeight, float MediaFraction, float Padding)
{
    /// <summary>The hero is the split row (it has a height of its own), not the stacked pane.</summary>
    public bool Split => Height > 0f;
}

/// <summary>How the event page's hero is built in a tier.</summary>
public enum ClassicConcertHero : byte
{
    /// <summary>The tuned arms: split from the tickets rail's edge, stacked under it.</summary>
    Tuned,
    /// <summary>Always the split row.</summary>
    Split,
    /// <summary>Always the stacked pane.</summary>
    Stacked,
}

/// <summary>What the event page lays out in one window tier.</summary>
public sealed record ClassicConcertLayout
{
    /// <summary>The window tier.</summary>
    public required WidthTier Tier { get; init; }

    /// <summary>The hero's arm rule.</summary>
    public required ClassicConcertHero Hero { get; init; }

    /// <summary>The split row's height or the stacked pane's media height.</summary>
    public required float HeroHeight { get; init; }

    /// <summary>The hero copy's padding.</summary>
    public required float HeroPad { get; init; }

    /// <summary>The tickets rail's width when the page is wide; 0 keeps the tickets inline at every width.</summary>
    public required float RailWidth { get; init; }

    /// <summary>The page's hit targets (the Buy button, the lineup rows): 40 at Compact, the control's 32 elsewhere.</summary>
    public required float Target { get; init; }

    /// <summary>The concert hub's filter pills and tokens (where, when, the genres): the 40 target at Compact, the tuned 32 elsewhere.</summary>
    public float HubPill { get; init; } = ClassicConcertRules.PillHeight;

    /// <summary>The lineup's column beside the dates grid (C-Concert-Huge: 640); 0 keeps the lineup a full-width card with the related shelf under it.</summary>
    public float LineupWidth { get; init; }

    /// <summary>The dates grid's columns (C-Concert-Huge: four date cards a row); read only where <see cref="LineupWidth"/> sets the grid beside the lineup.</summary>
    public int DateColumns { get; init; }

    /// <summary>The concert hub's records are the event page's (the hub has no board of its own): its filter pills' height in the tiers <paramref name="facts"/>.</summary>
    public static float HubPillOf(in TierFacts facts) => ClassicConcertTiers.Of(in facts).HubPill;
}
