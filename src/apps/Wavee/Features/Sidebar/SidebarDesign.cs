using System;

namespace Wavee;

// The sidebar design system's PURE vocabulary: the design enum, the per-design key slugs / mount keys / width tiers, the
// per-design pane-state snapshot-restore rules, and the per-design view-state enums.
//
// ENGINE-FREE BY CONSTRUCTION (System + IAppSettings + ShellResponsiveLayout only). That is load-bearing: this file is
// source-included by src/apps/Wavee.Tests (which has no FluentGpu.Engine reference) so SidebarModeStateTests can drive
// the real snapshot/restore + tier rules instead of a copy of them, exactly like ShellResponsiveLayout / SidebarSort.
// Nothing here may reference Signal<T>, Context<T>, Element or any other engine type — that lives in SidebarPreferences.

/// <summary>The three user-selectable left-sidebar designs. Values are PERSISTED (<c>WaveeSettings.SidebarDesign</c>) —
/// append only, never reorder or reuse. Classic is 0 so an install that has never written the key stays Classic
/// (locked decision 5).</summary>
public enum SidebarDesign : byte { Classic = 0, LibraryV3 = 1, Curated = 2 }

/// <summary>Which of Classic's three fixed sections a toggle refers to (its expansion is per-design state, so it lives
/// in <c>SidebarPreferences</c> rather than in the component — F.2.4).</summary>
public enum ClassicSection : byte { Pinned = 0, Library = 1, Playlists = 2 }

// (SidebarLoadFault — the document's health enum — is declared beside its producer in
//  Features/Sidebar/Persistence/SidebarLayoutStore.cs, which is its single owner.)

// The Library-V3 view state. Persisted as ints (AppDataSettings has no enum arm — the RowDensity/ThemeMode convention).
public enum SidebarV3Filter : byte { All = 0, Playlists = 1, Podcasts = 2, Albums = 3, Artists = 4 }
public enum SidebarV3Qualifier : byte { Any = 0, ByYou = 1, BySpotify = 2, Mixed = 3 }
public enum SidebarV3Sort : byte { Recents = 0, RecentlyAdded = 1, Alphabetical = 2, Creator = 3, Custom = 4 }
public enum SidebarV3View : byte { CompactList = 0, List = 1, CompactGrid = 2, Grid = 3 }

/// <summary>The per-design static table: key slug, mount key, responsive width tiers, and the int coercion the
/// persisted setting round-trips through.</summary>
public static class SidebarDesignInfo
{
    public const int Count = 3;

    /// <summary>Render/settings order — the order the design picker and the layout menu list the designs.</summary>
    public static readonly SidebarDesign[] All =
        [SidebarDesign.Classic, SidebarDesign.LibraryV3, SidebarDesign.Curated];

    /// <summary>The per-design settings-key slug (<c>"classic"</c> | <c>"v3"</c> | <c>"curated"</c>). PERSISTED — never
    /// change these. This is the single source of truth for every <c>sidebar.{slug}.*</c> key in the app.</summary>
    public static string Slug(SidebarDesign d) => d switch
    {
        SidebarDesign.LibraryV3 => "v3",
        SidebarDesign.Curated => "curated",
        _ => "classic",
    };

    /// <summary>The mode component's mount <c>Key</c> (see <c>SidebarHost</c>). Stable across releases: it is what makes
    /// a design switch an unconditional REMOUNT rather than a reuse.</summary>
    public static string MountKey(SidebarDesign d) => d switch
    {
        SidebarDesign.LibraryV3 => "sidebar.v3",
        SidebarDesign.Curated => "sidebar.curated",
        _ => "sidebar.classic",
    };

    /// <summary>The responsive nav-pane width tiers per design (locked decision 14). All three sets sit INSIDE the global
    /// <c>ShellResponsiveLayout.NavPaneMinW/MaxW</c> clamp (240/460) — the grip and the tier ladder still clamp through
    /// that one owner, and no per-design literal pair may be reintroduced anywhere else.</summary>
    public static (float Narrow, float Mid, float Wide) Tiers(SidebarDesign d) => d switch
    {
        SidebarDesign.LibraryV3 => (300f, 340f, 380f),
        SidebarDesign.Curated => (280f, 320f, 360f),
        _ => (ShellResponsiveLayout.NavPaneNarrowW,
              ShellResponsiveLayout.NavPaneMidW,
              ShellResponsiveLayout.NavPaneWideW),   // 240 / 280 / 320 — Classic's existing ladder, unchanged
    };

    /// <summary>Persisted-int → design, tolerating a hand-edited or future value (falls back to Classic, never throws).</summary>
    public static SidebarDesign FromInt(int v) => (uint)v < Count ? (SidebarDesign)v : SidebarDesign.Classic;
}

/// <summary>One design's remembered pane triple. Immutable so a snapshot can be handed around without aliasing the live
/// signals it came from.</summary>
public readonly record struct SidebarPaneSnapshot(float Width, bool Collapsed, bool WidthUserSet);

/// <summary>
/// The PURE per-design pane snapshot/restore rules behind <c>SidebarPreferences.SwitchDesign</c> (F.2.3). Kept here,
/// engine-free, for two reasons: it is the one part of a design switch that is genuinely a decision (which width does the
/// incoming design get?) rather than plumbing, and it is therefore the part worth pinning in a unit test.
///
/// The contract, restated: while a design's <c>WidthUserSet</c> is false its width follows THAT design's tier ladder;
/// the first committed seam drag in that design latches the flag forever, for that design only. Switching designs never
/// latches, and never clears, another design's flag. Collapsing is not a width choice and never touches the flag.
/// </summary>
public static class SidebarPaneState
{
    /// <summary>Write the outgoing design's live pane state into its own key bag (step 1 of a switch).</summary>
    public static void Snapshot(IAppSettings settings, SidebarDesign design, in SidebarPaneSnapshot state)
    {
        settings.Set(SidebarKeys.Width(design), state.Width);
        settings.Set(SidebarKeys.Collapsed(design), state.Collapsed);
        settings.Set(SidebarKeys.WidthUserSet(design), state.WidthUserSet);
    }

    /// <summary>Read the incoming design's remembered pane state (step 2 of a switch). A design whose width was never
    /// pinned by a drag gets its OWN tier default at the live viewport — which is what makes the tier ladder re-seed on
    /// a switch instead of carrying the outgoing design's width across.</summary>
    public static SidebarPaneSnapshot Restore(IAppSettings settings, SidebarDesign design, float viewportWidth)
    {
        bool userSet = settings.Get(SidebarKeys.WidthUserSet(design));
        float width = userSet
            ? Math.Clamp(settings.Get(SidebarKeys.Width(design)),
                         ShellResponsiveLayout.NavPaneMinW, ShellResponsiveLayout.NavPaneMaxW)
            : TierDefault(design, viewportWidth);
        return new SidebarPaneSnapshot(width, settings.Get(SidebarKeys.Collapsed(design)), userSet);
    }

    /// <summary>The width a design gets when it has never been pinned: its own tier ladder evaluated at
    /// <paramref name="viewportWidth"/> (a zero/unknown viewport takes the narrow tier — the pre-measure seed).</summary>
    public static float TierDefault(SidebarDesign design, float viewportWidth)
        => ShellResponsiveLayout.InitialNavPaneDefaultForViewport(viewportWidth, SidebarDesignInfo.Tiers(design));

    /// <summary>"Reset width" (the layout menu / customizer affordance): clear the design's user-set latch and hand back
    /// its tier default, so the responsive ladder owns the width again.</summary>
    public static SidebarPaneSnapshot ResetWidth(IAppSettings settings, SidebarDesign design, float viewportWidth)
    {
        settings.Set(SidebarKeys.WidthUserSet(design), false);
        float width = TierDefault(design, viewportWidth);
        settings.Set(SidebarKeys.Width(design), width);
        return new SidebarPaneSnapshot(width, settings.Get(SidebarKeys.Collapsed(design)), false);
    }

    /// <summary>The drag-commit edge: clamp, persist, and LATCH the width as this design's user choice.</summary>
    public static float CommitWidth(IAppSettings settings, SidebarDesign design, float width)
    {
        float clamped = Math.Clamp(width, ShellResponsiveLayout.NavPaneMinW, ShellResponsiveLayout.NavPaneMaxW);
        settings.Set(SidebarKeys.Width(design), clamped);
        settings.Set(SidebarKeys.WidthUserSet(design), true);
        return clamped;
    }
}
