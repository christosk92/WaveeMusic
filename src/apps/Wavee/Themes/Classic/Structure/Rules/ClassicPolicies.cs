// ── Themes/Classic/Structure/Rules/ClassicPolicies.cs — Wavee Classic's policies (themes plan 5.5, 6.1) ───────────────
//
// What the Classic structure decides for the core: the navigation pane's widths and what each band forces, the panel's bounds
// and the mini player's, the player bar's six pressure levels, the title-bar row's budget and the stage's layout classes, and
// (T3b) the window tiers whose edges are the pane's bands (`Tiers.cs`). These are the
// numbers the shell shipped with before themes existed; Classic behaves exactly as it did because the core state machines now
// take them from here (through `ThemeHost.Policies`) instead of from constants.
//
// A tuned-Classic PR (CT, TC) changes a value HERE and nowhere else. `ThemePolicies.Default` in core is the same set, kept so
// the shell has a policy before a composition is installed; `ClassicPoliciesTests` pins the two equal (the tiers aside: core
// never names a theme, so `Default` carries `TierPolicy.Standard` and the bundle below carries `ClassicTiers.Policy`).
//
// Literal numbers are allowed in this file (WVTH003 exempts `Rules\**`).

using System.Collections.Immutable;
using Wavee.Theming;

namespace Wavee.Themes.Classic;

/// <summary>The policies of Wavee Classic.</summary>
public static class ClassicPolicies
{
    /// <summary>The pane: the Wide band lets the user collapse; Narrow forces the 48-DIP rail with an overlay pane; Tiny is the
    /// drawer (where the bands begin is the window ladder's, 528 and 660 in <c>ClassicTiers</c>). An expanded pane is 180-460
    /// (320 fresh) and leaves 480 of content; a drag 64 below 180 collapses it, 24 back expands it. The seam strip is 16 wide and the collapsed rail 48.</summary>
    public static NavPanePolicy NavPane { get; } = new(
        ContentFloorW: 480f,
        MinWidth: 180f, MaxWidth: 460f, DefaultWidth: 320f,
        CollapseApproach: 64f, CollapseHysteresis: 24f,
        NarrowMode: NavPaneMode.Compact, TinyMode: NavPaneMode.Overlay, Resizable: true, Seam: 16f,
        RailWidth: SidebarGeometry.RailWidth);

    /// <summary>The panel: 200-500 wide, inline while 480 of content remains (otherwise it floats), beside the content row. The
    /// mini player keeps the 0.2.9 numbers: 240 x 135 at the least, 72 clear below (the player bar) and 32 above (the chrome),
    /// 16 from every edge.</summary>
    public static PanelPolicy Panel { get; } = new(
        MinWidth: 200f, MaxWidth: 500f, InlineMin: 480f, FullHeight: false,
        Pip: new Video.PipBounds(MinW: 240f, MinH: 135f, ReserveBottom: 72f, ReserveTop: 32f, Margin: 16f));

    /// <summary>The player bar: the 72-DIP dock, six levels starting at 0 / 440 / 760 / 900 / 1100 / 1240, narrowing held through a
    /// 24-DIP dip. Pressure is absorbed by what a level drops and by the spacing rungs, never by the buttons: the hit targets are
    /// flat at 32 (36 and 18 for the play button, 40 and 20 from Medium up). Each level also states what the PODCAST bar carries
    /// (`PodcastSlots`, `PodcastLeftW`): no shuffle / repeat or inline volume rail (the transport spends that room on its ±10 s steps and
    /// the speed button), previous / next from Wide, the time labels from Comfortable, the heart from Medium, and the identity block a
    /// bare cover at Minimal and 120 wide at Compact.</summary>
    public static PlayerBarPolicy PlayerBar { get; } = new(
        ImmutableArray.Create(
            // Minimal: the 300-DIP floor. Identity-first: art, title and artist, the heart and the device picker.
            new PlayerTier(0f, PlayerBarSlots.Devices | PlayerBarSlots.LikeSlot | PlayerBarSlots.Subtitle | PlayerBarSlots.Title,
                LeftW: 132f, ArtSize: 40f, RowGap: 3f, RowPad: 6f, ClusterGap: 2f, LeftGap: 4f, SeekGap: 4f, RightGap: 0f,
                ButtonBox: 32f, ButtonGlyph: 16f, PrimaryBox: 36f, PrimaryGlyph: 18f,
                PodcastSlots: PlayerBarSlots.Devices | PlayerBarSlots.Subtitle, PodcastLeftW: 40f),
            // Compact: the remaining time joins.
            new PlayerTier(440f, PlayerBarSlots.Devices | PlayerBarSlots.LikeSlot | PlayerBarSlots.Subtitle | PlayerBarSlots.Title | PlayerBarSlots.TimesRemaining,
                LeftW: 172f, ArtSize: 40f, RowGap: 4f, RowPad: 8f, ClusterGap: 3f, LeftGap: 6f, SeekGap: 5f, RightGap: 1f,
                ButtonBox: 32f, ButtonGlyph: 16f, PrimaryBox: 36f, PrimaryGlyph: 18f,
                PodcastSlots: PlayerBarSlots.Devices | PlayerBarSlots.Subtitle | PlayerBarSlots.Title, PodcastLeftW: 120f),
            // Medium: the 48 cover, the roomy play button, previous / next, elapsed time, lyrics and the volume button.
            new PlayerTier(760f, PlayerBarSlots.Devices | PlayerBarSlots.LikeSlot | PlayerBarSlots.Subtitle | PlayerBarSlots.Title | PlayerBarSlots.TimesRemaining
                    | PlayerBarSlots.VolumeButton | PlayerBarSlots.Lyrics | PlayerBarSlots.TimesElapsed | PlayerBarSlots.PrevNext,
                LeftW: 220f, ArtSize: 48f, RowGap: 6f, RowPad: 8f, ClusterGap: 4f, LeftGap: 8f, SeekGap: 6f, RightGap: 2f,
                ButtonBox: 32f, ButtonGlyph: 16f, PrimaryBox: 40f, PrimaryGlyph: 20f,
                PodcastSlots: PlayerBarSlots.Devices | PlayerBarSlots.LikeSlot | PlayerBarSlots.Subtitle | PlayerBarSlots.Title | PlayerBarSlots.VolumeButton
                    | PlayerBarSlots.Lyrics),
            // Comfortable: shuffle / repeat and the remote-device line join.
            new PlayerTier(900f, PlayerBarSlots.Devices | PlayerBarSlots.LikeSlot | PlayerBarSlots.Subtitle | PlayerBarSlots.Title | PlayerBarSlots.TimesRemaining
                    | PlayerBarSlots.VolumeButton | PlayerBarSlots.Lyrics | PlayerBarSlots.TimesElapsed | PlayerBarSlots.PrevNext
                    | PlayerBarSlots.ShuffleRepeat | PlayerBarSlots.RemoteDeviceLine,
                LeftW: 230f, ArtSize: 48f, RowGap: 6f, RowPad: 8f, ClusterGap: 4f, LeftGap: 8f, SeekGap: 6f, RightGap: 2f,
                ButtonBox: 32f, ButtonGlyph: 16f, PrimaryBox: 40f, PrimaryGlyph: 20f,
                PodcastSlots: PlayerBarSlots.Devices | PlayerBarSlots.LikeSlot | PlayerBarSlots.Subtitle | PlayerBarSlots.Title | PlayerBarSlots.TimesRemaining
                    | PlayerBarSlots.VolumeButton | PlayerBarSlots.Lyrics | PlayerBarSlots.TimesElapsed | PlayerBarSlots.RemoteDeviceLine),
            // Wide: the queue and the inline volume rail join; the row breathes (gap 8, pad 12).
            new PlayerTier(1100f, PlayerBarSlots.Devices | PlayerBarSlots.LikeSlot | PlayerBarSlots.Subtitle | PlayerBarSlots.Title | PlayerBarSlots.TimesRemaining
                    | PlayerBarSlots.VolumeButton | PlayerBarSlots.Lyrics | PlayerBarSlots.TimesElapsed | PlayerBarSlots.PrevNext
                    | PlayerBarSlots.ShuffleRepeat | PlayerBarSlots.RemoteDeviceLine | PlayerBarSlots.Queue | PlayerBarSlots.VolumeSlider,
                LeftW: 240f, ArtSize: 48f, RowGap: 8f, RowPad: 12f, ClusterGap: 4f, LeftGap: 8f, SeekGap: 6f, RightGap: 2f,
                ButtonBox: 32f, ButtonGlyph: 16f, PrimaryBox: 40f, PrimaryGlyph: 20f,
                PodcastSlots: PlayerBarSlots.Devices | PlayerBarSlots.LikeSlot | PlayerBarSlots.Subtitle | PlayerBarSlots.Title | PlayerBarSlots.TimesRemaining
                    | PlayerBarSlots.VolumeButton | PlayerBarSlots.Lyrics | PlayerBarSlots.TimesElapsed | PlayerBarSlots.PrevNext
                    | PlayerBarSlots.RemoteDeviceLine | PlayerBarSlots.Queue),
            // Full: the expand button joins; the widest identity block.
            new PlayerTier(1240f, PlayerBarSlots.Devices | PlayerBarSlots.LikeSlot | PlayerBarSlots.Subtitle | PlayerBarSlots.Title | PlayerBarSlots.TimesRemaining
                    | PlayerBarSlots.VolumeButton | PlayerBarSlots.Lyrics | PlayerBarSlots.TimesElapsed | PlayerBarSlots.PrevNext
                    | PlayerBarSlots.ShuffleRepeat | PlayerBarSlots.RemoteDeviceLine | PlayerBarSlots.Queue | PlayerBarSlots.VolumeSlider
                    | PlayerBarSlots.Expand,
                LeftW: 260f, ArtSize: 48f, RowGap: 8f, RowPad: 12f, ClusterGap: 4f, LeftGap: 8f, SeekGap: 6f, RightGap: 2f,
                ButtonBox: 32f, ButtonGlyph: 16f, PrimaryBox: 40f, PrimaryGlyph: 20f,
                PodcastSlots: PlayerBarSlots.Devices | PlayerBarSlots.LikeSlot | PlayerBarSlots.Subtitle | PlayerBarSlots.Title | PlayerBarSlots.TimesRemaining
                    | PlayerBarSlots.VolumeButton | PlayerBarSlots.Lyrics | PlayerBarSlots.TimesElapsed | PlayerBarSlots.PrevNext
                    | PlayerBarSlots.RemoteDeviceLine | PlayerBarSlots.Queue | PlayerBarSlots.Expand)),
        Hysteresis: 24f, Height: 72f);

    /// <summary>The title-bar row: bell, friends and pin from 1200, the profile name from 1360, forward above 520 (all
    /// budget-checked), every fixed affordance at the width the row really lays it out at, a 280-420 search field (28 % of the
    /// window) that yields to the 44 magnifier, and 110-200 tabs.</summary>
    public static ChromePolicy Chrome { get; } = new(new ChromeBudget(
        NameEnterW: 1360f, ActionsEnterW: 1200f, ForwardEnterW: 520f, PromotionHysteresisW: 40f,
        BarLeadW: 60f, NavButtonW: 44f, AddSlotW: 32f,
        ProfileChipW: 32f, ConnectingChipW: 90f, SignInChipW: 84f, ReconnectChipW: 100f, ProfileNameW: 90f,
        MinDragStripW: 48f, CaptionClusterW: 138f, ThemeToggleW: 44f, GutterMinW: 8f,
        WidthQuantumW: 10f, SearchMaxW: 420f, SearchMinW: 280f, SearchWidthRatio: 0.28f, SearchIconW: 44f,
        TabMaxW: 200f, TabMinW: 110f, PinnedTabW: 40f, TabViewportMinW: 32f));

    /// <summary>The stage: Compact under 600 wide or from 460 tall (back at 484), Portrait up to 0.80 (back above 0.85),
    /// Ultrawide from 2.00 (back at 1.95).</summary>
    public static StagePolicy Stage { get; } = new(new AspectThresholds(
        CompactBelowW: 600f, PortraitEnter: 0.80f, PortraitLeave: 0.85f, UltrawideEnter: 2.00f, UltrawideLeave: 1.95f,
        CompactEnterH: 460f, CompactLeaveH: 484f));

    /// <summary>The whole bundle, with Classic's window tiers. One instance, so republishing the facts is not a change. Declared
    /// LAST: static properties initialise in textual order and it reads the five above (`ClassicTiers` builds its policy in its
    /// own initializer and never reads this class).</summary>
    public static ThemePolicies Bundle { get; } = new(NavPane, Panel, PlayerBar, Chrome, Stage) { Tiers = ClassicTiers.Policy };

    // The bundle for each combination of the structure options that reach the core (M7a: the sidebar layout x density; M4: the track artwork hidden; M8: the
    // detail page layout x the rails' uniformity): index = (((railUniform * 2) + pageLayout) * 2 + hideArt) * 4 + layout * 2 + density. The default
    // combination is `Bundle` itself.
    private static readonly ThemePolicies[] s_byOptions = BuildByOptions();

    private static ThemePolicies[] BuildByOptions()
    {
        var all = new ThemePolicies[32];
        foreach (bool railUniform in new[] { false, true })
            foreach (DetailPageLayout pageLayout in new[] { DetailPageLayout.Automatic, DetailPageLayout.Hero })
                foreach (bool hideArt in new[] { false, true })
                    foreach (SidebarLayoutId layout in new[] { SidebarLayoutId.Classic, SidebarLayoutId.Library })
                        foreach (SidebarDensity density in new[] { SidebarDensity.Default, SidebarDensity.Compact })
                        {
                            bool isDefault = !hideArt && layout == SidebarLayoutId.Classic && density == SidebarDensity.Default
                                && pageLayout == DetailPageLayout.Automatic && !railUniform;
                            all[IndexOf(layout, density, hideArt, pageLayout, railUniform)] = isDefault
                                ? Bundle
                                : Bundle with
                                {
                                    NavPane = NavPane with { Layout = layout, Density = density },
                                    Lists = hideArt ? new ListPolicy(TrackArtwork: false) : Bundle.Lists,
                                    Detail = new DetailPolicy(pageLayout, railUniform),
                                };
                        }
        return all;
    }

    private static int IndexOf(SidebarLayoutId layout, SidebarDensity density, bool hideArt, DetailPageLayout pageLayout, bool railUniform)
        => ((((railUniform ? 2 : 0) + (int)pageLayout) * 2 + (hideArt ? 1 : 0)) * 4) + (int)layout * 2 + (int)density;

    /// <summary>The bundle for the options the structure published: <see cref="Bundle"/> with the pane's <see cref="NavPanePolicy.Layout"/> and
    /// <see cref="NavPanePolicy.Density"/> set, the track lists' artwork hidden when <paramref name="hideTrackArt"/>, and the detail pages' layout and rail
    /// uniformity. One instance per combination, so republishing the facts is not a change.</summary>
    public static ThemePolicies For(SidebarLayoutId layout, SidebarDensity density, bool hideTrackArt = false,
        DetailPageLayout pageLayout = DetailPageLayout.Automatic, bool railUniform = false)
        => s_byOptions[IndexOf(layout, density, hideTrackArt, pageLayout, railUniform)];
}
