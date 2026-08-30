using System;

namespace Wavee;

/// <summary>Pure whole-shell breakpoints. The bands are hysteretic so a boundary crossing during pointer resize
/// commits one structural change instead of oscillating on adjacent frames.</summary>
public static class ShellResponsiveLayout
{
    public const float NarrowEnterW = 720f;
    public const float NarrowLeaveW = 760f;
    public const float CompactRailW = 56f;
    public const float DrawerMinW = 240f;
    public const float DrawerViewportInset = 32f;

    public const float ToolbarNarrowEnterW = 520f;
    public const float ToolbarNarrowLeaveW = 560f;

    // ── the MERGED chrome row (one 48-DIP TitleBar: tabs · window-centred search · identity) ──────────────────────────
    // The row is allocated by SPACE ACCOUNTING (MergedChromeLayout.Resolve), not by a hand-authored threshold table:
    // the FIXED BUDGET below is subtracted from the window width and what is left is handed out in ONE priority order —
    // every tab at its 110-DIP floor first, then the search, then the tabs widen with whatever is still spare. The only
    // width THRESHOLDS left are the three identity toggles (name · friends · forward), which are cheap fixed-width bits;
    // their COST feeds the budget so the accounting stays honest across their bands.
    //
    // Hysteresis is unchanged in mechanism: MergedChromeLayout.Resolve re-resolves a PROMOTION at (width - hysteresis)
    // and lets a DEMOTION happen immediately — the DetailTrackCommandBarLayout idiom. That single reserve makes every
    // structural decision sticky without a per-stage current/leave pair.
    // 40 DIP is deliberately the SAME band width the two-row toolbar used (ToolbarNarrowLeaveW - ToolbarNarrowEnterW).
    public const float ChromePromotionHysteresisW = 40f;

    /// <summary>The profile NAME beside the avatar — the first thing to go, so the gutters stop being the only give.</summary>
    public const float ChromeNameEnterW = 1360f;
    /// <summary>Friends keeps the two-row toolbar's own 1000 threshold — but as the band where it is IN THE ROW rather
    /// than the band where it exists at all: below this it becomes a profile-menu row (<c>MergedChromeLayout.
    /// FriendsInMenu</c>), so the affordance never disappears.
    /// <para>The bell's 800 and the theme toggle's 720 are GONE, not moved: notifications now ride the profile chip's
    /// unread badge and the theme toggle has a permanent caption-leading button, so neither has a width at which it
    /// drops. The ladder is shorter by two stages on purpose.</para></summary>
    public const float ChromeFriendsEnterW = 1000f;
    /// <summary>Forward folds into the "⋯" overflow at the SAME 520/560 band the old ShellToolbar used for its primary
    /// nav — the raw threshold is <see cref="ToolbarNarrowEnterW"/> and the 40-DIP promotion reserve reproduces 560.</summary>
    public const float ChromeForwardEnterW = ToolbarNarrowEnterW;

    // ── the FIXED BUDGET: every DIP of the row that is neither a tab nor the search ───────────────────────────────────
    // These are the row's REAL laid-out widths, read off the code that draws them — not fresh guesses. Each one names
    // its source so a retune there is caught here. MergedChromeLayout.FixedBudget is the single consumer; it is what
    // makes "how much room do the tabs actually have?" an arithmetic question instead of a hand-tuned table.

    /// <summary>TitleBar's lead column before the tabs island: the bar's own 2-DIP root padding + the built-in pane
    /// toggle (40 wide, and Wavee's <c>ChromeParts</c> margin override keeps the 44-DIP advance the stock 2-a-side
    /// margin had) + TitleBar's 14-DIP <c>LeftHeaderPad</c>. Wavee sets no icon, title or subtitle, so nothing else
    /// is in there. (TitleBar.cs: NavButtonSize/LeftHeaderPad and the root Padding.)</summary>
    public const float ChromeBarLeadW = 60f;
    /// <summary>One island affordance's advance: <c>ShellToolbar.BarNavStyle.Size</c> 40 + <c>BarNavMargin</c> 2+2.
    /// Back, Forward, the Friends button and the "⋯" overflow are all exactly this.</summary>
    public const float ChromeNavButtonW = 44f;
    /// <summary>The tab strip's PERMANENTLY reserved "+" slot (TabStrip.AddPlate, 32 wide, no margin in text mode). It
    /// is mounted at every width even while invisible — the reason the old hand-tuned keep steps over-evicted.</summary>
    public const float ChromeAddSlotW = 32f;
    /// <summary>The "⌄" tab-overflow plate (<c>TabOverflowButton</c>: 7 + a 10pt chevron + 3 gap + the count + 7).
    /// Budgeted ONLY when the ladder is actually going to fold a tab away.</summary>
    public const float ChromeTabOverflowW = 36f;
    /// <summary>The profile chip with the avatar alone (ProfileMenu: 4 + a 24-DIP PersonPicture + 4). The unread badge
    /// is a zero-footprint ZStack overlay, so it never enters the budget.</summary>
    public const float ChromeProfileChipW = 32f;
    /// <summary>What <c>ShowName</c> ADDS to that chip: the 8-DIP gap + the display-name caption + the extra 6 DIP of
    /// right padding the named form carries. A nominal for a nominal name — the drag gutters absorb the error.</summary>
    public const float ChromeProfileNameW = 90f;
    /// <summary>Wavee's minimum grabbable seam before its caption-adjacent search icon. The stock TitleBar retains a
    /// 48-DIP strip; this merged row explicitly lets that elastic strip yield to the app chrome down to Spacing.XS.</summary>
    public const float ChromeMinDragStripW = 4f;
    /// <summary>The caption cluster: 3 × <c>CaptionButton.Width</c> (46).</summary>
    public const float ChromeCaptionClusterW = 138f;
    /// <summary>The permanent theme toggle immediately before the native caption buttons.</summary>
    public const float ChromeThemeToggleW = 32f;
    /// <summary>Reserved on EACH flank of the centre island so the search never butts against the tabs strip or the
    /// identity cluster. The bar's two grow bands split the real leftover between them; this is only the floor that
    /// keeps the two clusters from touching when the row is full.</summary>
    public const float ChromeGutterMinW = 8f;
    /// <summary>Search and tab widths snap DOWN to this. The ladder is now continuous in width, and a signal that moved
    /// on every device pixel would re-render the bar (and re-push its non-client regions) per pixel of a resize drag —
    /// exactly what the band-gated publish in WaveeShell exists to avoid. Snapping DOWN also keeps the allocation
    /// inside the budget by construction.</summary>
    public const float ChromeWidthQuantumW = 10f;

    // Search: a real field between ChromeSearchMinW and ChromeSearchMaxW, else the click-expanding magnifier. These are
    // the only two numbers the search ladder has now — everything between them is whatever space is left over.
    public const float ChromeSearchMaxW = 420f;
    public const float ChromeSearchMinW = 280f;
    public const float ChromeSearchWidthRatio = 0.28f;
    /// <summary>The MINIMUM GUARANTEED form of the search: a 32-DIP magnifier that CLICK-expands (LibraryV3Search's
    /// pattern). Tabs are measured against THIS, not against the field — the search yields all the way to an icon
    /// before a single tab is evicted.</summary>
    public const float ChromeSearchIconW = 32f;
    // Tabs: the FLOOR is what the allocator counts with (a text tab narrower than this is unreadable); the CAP is what
    // a tab may grow to once the search is comfortable and there is still surplus.
    public const float ChromeTabMaxW = 200f;
    public const float ChromeTabMinW = 110f;
    public const float ChromePinnedTabW = 40f;
    public const float ChromeTabViewportMinW = 32f;
    public const float ChromeTabComfortRatio = 0.78f;
    public const float ChromeTabComfortMinW = 240f;
    public const float ChromeTabComfortMaxW = 720f;

    // ── nav-pane (sidebar) width ─────────────────────────────────────────────────────────────────────────────────────
    // The single clamp bounds for the expanded pane. Every writer (the seam drag, the probe seam, the responsive default)
    // must clamp through these — a second literal pair is how the drag and the probe drifted apart.
    public const float NavPaneMinW = 240f, NavPaneMaxW = 460f;

    // ── right rail (lyrics / queue / now-playing) ────────────────────────────────────────────────────────────────────
    // One clamp for every writer (the left-seam splitter, the settings seed, CanFitRail). A second literal pair is how
    // the sidebar drag and the probe drifted apart; do not reintroduce that here.
    public const float RailMinW = 200f, RailMaxW = 500f, RailDefaultW = 340f;
    public static float ClampRailWidth(float w) => Math.Clamp(w, RailMinW, RailMaxW);
    /// <summary>16:9 height of a full-bleed rail-width video cap (no inset). The splitter's floor — drag only grows
    /// from here.</summary>
    public static float DockedVideoNaturalH(float railW) => railW * 9f / 16f;
    /// <summary>How tall the docked cap may grow. Leaves the lyrics/queue body as the remaining Grow=1 column.</summary>
    public const float DockedVideoMaxH = 560f;
    public static float ClampDockedVideoHeight(float h, float railW)
    {
        float min = DockedVideoNaturalH(railW);
        float max = Math.Max(min, DockedVideoMaxH);
        return h <= 0f ? min : Math.Clamp(h, min, max);
    }

    /// <summary>The cap height that matches the CONTENT'S OWN aspect at the current rail width — the card's default
    /// size, so a video fills it edge to edge instead of sitting inside <c>Tok.MediaLetterbox</c> bars.
    /// <para>The card used to be sized purely by the rail's persisted/dragged height, which made letterboxing the
    /// DEFAULT for every stream that was not exactly as tall as whatever the user (or a previous session) had left
    /// there. A player that has not reported its size yet answers 16:9 — the same floor the splitter uses — so the
    /// tile never flashes at a wrong shape on the way in. The result is clamped through
    /// <see cref="ClampDockedVideoHeight"/>: one clamp for every writer.</para></summary>
    /// <param name="railW">The rail's current width — the card is full-bleed, so this IS the video's width.</param>
    /// <param name="naturalW">The media's natural pixel width; ≤ 0 = not reported yet.</param>
    /// <param name="naturalH">The media's natural pixel height; ≤ 0 = not reported yet.</param>
    public static float FitDockedVideoHeight(float railW, int naturalW, int naturalH)
    {
        float ratio = naturalW > 0 && naturalH > 0 ? (float)naturalH / naturalW : 9f / 16f;
        return ClampDockedVideoHeight(railW * ratio, railW);
    }
    /// <summary>Viewport-fit test for sidebar + rail + a minimum usable content region.</summary>
    public static bool CanFitRail(float viewportW, float sidebarW, float railW = RailDefaultW, float minContentW = 480f)
        => sidebarW + railW + minContentW <= viewportW;

    // Stock NavigationView authors OpenPaneLength per window class rather than one fixed number: the 240-DIP floor for
    // ordinary windows and 320 once the window is wide enough that a 240 pane reads as a cramped gutter beside a very wide
    // content card. These three tiers are that ladder; they are the DEFAULT only — a user who drags the seam pins their own
    // width (SidebarPreferences.WidthUserSet, per design) and the ladder stops applying for THAT design.
    // Each sidebar design has its own triple (see NavPaneTiers); these three consts are Classic's.
    public const float NavPaneMidEnterW = 1400f;    // ≥ this → the MID tier
    public const float NavPaneWideEnterW = 1800f;   // ≥ this → the WIDE tier
    public const float NavPaneNarrowW = 240f, NavPaneMidW = 280f, NavPaneWideW = 320f;   // Classic's ladder
    public const float NavPaneHysteresisDip = 24f;

    /// <summary>Classic's tier triple — the values every no-triple overload below forwards with, so an existing call site
    /// that knows nothing about sidebar designs keeps its exact behavior.</summary>
    public static (float Narrow, float Mid, float Wide) ClassicTiers => (NavPaneNarrowW, NavPaneMidW, NavPaneWideW);

    /// <summary>The nav-pane width tiers for a sidebar DESIGN (locked decision 14: Classic 240/280/320 · Library V3
    /// 300/340/380 · Curated 280/320/360). One indirection to <c>SidebarDesignInfo.Tiers</c>, which is the single owner of
    /// the values; this is the name the shell and the mode surfaces call. The breakpoints (1400/1800), the 24-DIP shrink
    /// hysteresis, the 240/460 clamp and the 720/760 narrow band are IDENTICAL for all three designs — only the three tier
    /// values differ.</summary>
    public static (float Narrow, float Mid, float Wide) NavPaneTiers(SidebarDesign design)
        => SidebarDesignInfo.Tiers(design);

    public static float NominalNavPaneDefaultFor(float viewportWidth)
        => NominalNavPaneDefaultFor(viewportWidth, ClassicTiers);

    public static float NominalNavPaneDefaultFor(float viewportWidth, in (float Narrow, float Mid, float Wide) tiers) =>
        viewportWidth >= NavPaneWideEnterW ? tiers.Wide
        : viewportWidth >= NavPaneMidEnterW ? tiers.Mid
        : tiers.Narrow;

    /// <summary>Pre-measure seed. A zero/unknown viewport (the shell constructor, before the first bounds callback) takes
    /// the narrow tier; the viewport effect commits the real tier before the first layout.</summary>
    public static float InitialNavPaneDefaultForViewport(float viewportWidth)
        => InitialNavPaneDefaultForViewport(viewportWidth, ClassicTiers);

    /// <inheritdoc cref="InitialNavPaneDefaultForViewport(float)"/>
    public static float InitialNavPaneDefaultForViewport(float viewportWidth, in (float Narrow, float Mid, float Wide) tiers)
        => viewportWidth <= 0f ? tiers.Narrow : NominalNavPaneDefaultFor(viewportWidth, tiers);

    /// <summary>Widen immediately; shrink only after <see cref="NavPaneHysteresisDip"/> past the threshold — the
    /// <c>DetailLayoutBreakpoints.TierFor</c> idiom, with the dip ADDED because here a LARGER number is the wider tier
    /// (there it is a tier index, where larger is narrower). So 1400 widens to the mid tier at once, and the mid tier holds
    /// down to 1376.</summary>
    public static float NavPaneDefaultFor(float viewportWidth, float current, bool initialized)
        => NavPaneDefaultFor(viewportWidth, current, initialized, ClassicTiers);

    /// <inheritdoc cref="NavPaneDefaultFor(float, float, bool)"/>
    public static float NavPaneDefaultFor(float viewportWidth, float current, bool initialized,
                                          in (float Narrow, float Mid, float Wide) tiers)
    {
        if (viewportWidth <= 0f) return current;
        if (!initialized) return NominalNavPaneDefaultFor(viewportWidth, tiers);
        float nominal = NominalNavPaneDefaultFor(viewportWidth, tiers);
        if (nominal >= current) return nominal;
        float dipped = NominalNavPaneDefaultFor(viewportWidth + NavPaneHysteresisDip, tiers);
        return dipped < current ? dipped : current;
    }

    public static bool NarrowFor(float width, bool current, bool initialized)
    {
        if (width <= 0f) return current;
        if (!initialized) return width <= NarrowEnterW;
        return current ? width < NarrowLeaveW : width <= NarrowEnterW;
    }

    public static bool ToolbarNarrowFor(float width, bool current, bool initialized)
    {
        if (width <= 0f) return current;
        if (!initialized) return width <= ToolbarNarrowEnterW;
        return current ? width < ToolbarNarrowLeaveW : width <= ToolbarNarrowEnterW;
    }

    public static float DrawerWidth(float viewportWidth, float preferredWidth)
    {
        float cap = MathF.Max(CompactRailW, viewportWidth - DrawerViewportInset);
        return MathF.Min(MathF.Max(DrawerMinW, preferredWidth), cap);
    }

    public static float DrawerRestingOpacity(bool open) => open ? 1f : 0f;
    public static float DrawerRestingTranslateX(bool open, float width) => open ? 0f : -width;
}
