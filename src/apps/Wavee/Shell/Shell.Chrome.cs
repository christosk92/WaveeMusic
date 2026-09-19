// ── Shell/Shell.Chrome.cs ──────────────────────────────────────────────────────────────────────────────────────────
// The named partial of Shell.cs (§2's "a file that passes its budget by 30 % gets a NAMED partial", declared on the
// first day of the wave per §8 G4): the whole-shell responsive breakpoints, the merged 48-DIP chrome row's PRESSURE
// ALLOCATOR, and the tab workspace with its persisted pinned subset.
//
// Role: CORE
// Owner: I
// Wave: 4
// Budget: 750 lines (inside Shell.cs's 2,100)
// Spec: ch 18 §8 (ShellResponsiveLayout 247 · MergedChromeLayout 192 · TabWorkspace 246 · WorkspaceTabsPersistence 55)

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wavee;

public static partial class Shell
{
    // ══ 1. WHOLE-SHELL BREAKPOINTS ══════════════════════════════════════════════════════════════════════════════════
    //
    // Pure, and HYSTERETIC by construction, so a boundary crossing during a pointer resize commits ONE structural
    // change instead of oscillating on adjacent frames.

    public static class Layout
    {
        public const float NarrowEnterW = 720f;
        public const float NarrowLeaveW = 760f;
        public const float CompactRailW = 56f;
        public const float DrawerMinW = 240f;
        public const float DrawerViewportInset = 32f;

        public const float ToolbarNarrowEnterW = 520f;
        public const float ToolbarNarrowLeaveW = 560f;

        // ── the MERGED chrome row (one 48-DIP title bar: tabs · window-centred search · identity) ────────────────────
        //
        // The row is allocated by SPACE ACCOUNTING (`Chrome.Resolve`), not by a hand-authored threshold table: the
        // FIXED BUDGET below is subtracted from the window width and what is left is handed out in ONE priority order
        // — every tab at its 110-DIP floor first, then the search, then the tabs widen with whatever is still spare.
        // The only width THRESHOLDS left are the three identity toggles (name · actions · forward), which are cheap
        // fixed-width bits; their COST feeds the budget so the accounting stays honest across their bands.
        //
        // Hysteresis: a PROMOTION is re-resolved at (width − reserve) and a DEMOTION happens immediately. That single
        // reserve makes every structural decision sticky without a per-stage current/leave pair. 40 DIP is
        // deliberately the SAME band width the two-row toolbar used (ToolbarNarrowLeaveW − ToolbarNarrowEnterW).
        public const float ChromePromotionHysteresisW = 40f;

        /// <summary>The profile NAME beside the avatar — the first thing to go, so the gutters stop being the only
        /// give.</summary>
        public const float ChromeNameEnterW = 1360f;

        /// <summary>The ONE "actions in row" stage — bell · friends · pin · settings enter the trailing island
        /// TOGETHER at this width. 1200, not the old friends-only 1000: four 44-DIP buttons plus the theme toggle
        /// would starve the search field of a 1000-wide window with two tabs. Below it every one of the four FOLDS
        /// rather than vanishing — bell and friends become profile-menu rows, and pin simply drops from the row (the
        /// tab/page context menu still offers it).</summary>
        public const float ChromeActionsEnterW = 1200f;

        /// <summary>Forward hides below the SAME 520/560 band the old toolbar used for its primary nav — the raw
        /// threshold is <see cref="ToolbarNarrowEnterW"/> and the 40-DIP promotion reserve reproduces 560. There is no
        /// overflow to catch it: Alt+Right and the mouse back button still reach it while it is off-screen.</summary>
        public const float ChromeForwardEnterW = ToolbarNarrowEnterW;

        // ── the FIXED BUDGET: every DIP of the row that is neither a tab nor the search ───────────────────────────────
        // These are the row's REAL laid-out widths, read off the code that draws them. Each names its source so a
        // retune there is caught here; `Chrome.FixedBudget` is the single consumer.

        /// <summary>The title bar's lead column before the tabs island: its own 2-DIP root padding + the built-in pane
        /// toggle (40 wide, and the margin override keeps the 44-DIP advance the stock 2-a-side margin had) + the
        /// 14-DIP left header pad. Wavee sets no icon, title or subtitle, so nothing else is in there.</summary>
        public const float ChromeBarLeadW = 60f;

        /// <summary>One island affordance's advance: a 40-DIP bar-nav button + 2+2 margin. Back, Forward and every
        /// trailing action button (bell, friends, pin, settings) are all exactly this.</summary>
        public const float ChromeNavButtonW = 44f;

        /// <summary>The tab strip's PERMANENTLY reserved "+" slot (32 wide, no margin in text mode). It is mounted at
        /// every width even while invisible — the reason the old hand-tuned keep steps over-evicted.</summary>
        public const float ChromeAddSlotW = 32f;

        /// <summary>The "⌄" tab-overflow plate (7 + a 10 pt chevron + 3 gap + the count + 7). Budgeted ONLY when the
        /// ladder is actually going to fold a tab away.</summary>
        public const float ChromeTabOverflowW = 36f;

        /// <summary>The profile chip with the avatar alone (4 + a 24-DIP picture + 4). The unread badge is a
        /// zero-footprint overlay, so it never enters the budget.</summary>
        public const float ChromeProfileChipW = 32f;

        /// <summary>What "show the name" ADDS to that chip: the 8-DIP gap + the display-name caption + the extra 6 DIP
        /// of right padding the named form carries. A nominal for a nominal name — the drag gutters absorb the
        /// error.</summary>
        public const float ChromeProfileNameW = 90f;

        /// <summary>The minimum grabbable seam before the caption-adjacent search icon. This used to read 4 and claim
        /// the strip yields to the XS spacing — it does not: the title bar PINS it at 48 under merged mode, and the
        /// band is emitted <c>Grow=0, Shrink=0</c> even with the elastic tabs lane. The budget was therefore
        /// under-reserving by 44 DIP, biasing every threshold computed against <see cref="Chrome.FixedBudget"/>
        /// optimistic — the search field appeared to fit 44 DIP sooner than the row could actually give it. (#88)</summary>
        public const float ChromeMinDragStripW = 48f;

        /// <summary>The caption cluster: 3 × 46.</summary>
        public const float ChromeCaptionClusterW = 138f;

        /// <summary>The permanent theme toggle immediately before the native caption buttons — a real bar-nav island
        /// button (40 + 2+2), not the old hand-rolled 32-DIP box.</summary>
        public const float ChromeThemeToggleW = 44f;

        /// <summary>Reserved on EACH flank of the centre island so the search never butts against the tab strip or the
        /// identity cluster. The bar's two grow bands split the real leftover between them; this is only the floor
        /// that keeps the two clusters from touching when the row is full.</summary>
        public const float ChromeGutterMinW = 8f;

        /// <summary>Search and tab widths snap DOWN to this. The ladder is continuous in width, and a signal that
        /// moved on every device pixel would re-render the bar (and re-push its non-client regions) per pixel of a
        /// resize drag. Snapping DOWN also keeps the allocation inside the budget by construction.</summary>
        public const float ChromeWidthQuantumW = 10f;

        // Search: a real field between min and max, else the click-expanding magnifier. These are the only two numbers
        // the search ladder has — everything between them is whatever space is left over.
        public const float ChromeSearchMaxW = 420f;
        public const float ChromeSearchMinW = 280f;
        public const float ChromeSearchWidthRatio = 0.28f;

        /// <summary>The MINIMUM GUARANTEED form of the search: a magnifier that CLICK-expands, styled as a bar-nav
        /// island button (40 + 2+2). Tabs are measured against THIS, not against the field — the search yields all the
        /// way to an icon before a single tab is evicted.</summary>
        public const float ChromeSearchIconW = 44f;

        // Tabs: the FLOOR is what the allocator counts with (a text tab narrower than this is unreadable); the CAP is
        // what a tab may grow to once the search is comfortable and there is still surplus.
        public const float ChromeTabMaxW = 200f;
        public const float ChromeTabMinW = 110f;
        public const float ChromePinnedTabW = 40f;
        public const float ChromeTabViewportMinW = 32f;

        // ── nav-pane (sidebar) width ─────────────────────────────────────────────────────────────────────────────────
        // The single clamp bounds for the expanded pane. EVERY writer (the seam drag, the probe seam, the responsive
        // default) must clamp through these — a second literal pair is how the drag and the probe drifted apart.
        // Issue #84 lowered the floor from 240 to 180 so the sidebar can go genuinely narrow.
        public const float NavPaneMinW = 180f, NavPaneMaxW = 460f;

        // Stock navigation views author the open-pane length per window class rather than one fixed number: a 240-DIP
        // floor for ordinary windows, 320 once the window is wide enough that 240 reads as a cramped gutter. These are
        // the DEFAULT only — a user who drags the seam pins their own width and the ladder stops applying for that
        // design. Each sidebar design has its own triple (owner J's `Sidebar.cs` owns the values); the breakpoints,
        // the 24-DIP shrink hysteresis and the clamp are IDENTICAL for all three.
        public const float NavPaneMidEnterW = 1400f;    // ≥ this → the MID tier
        public const float NavPaneWideEnterW = 1800f;   // ≥ this → the WIDE tier
        public const float NavPaneNarrowW = 240f, NavPaneMidW = 280f, NavPaneWideW = 320f;   // Classic's ladder
        public const float NavPaneHysteresisDip = 24f;

        /// <summary>Classic's tier triple — what every no-triple overload forwards with, so a call site that knows
        /// nothing about sidebar designs keeps its exact behaviour.</summary>
        public static (float Narrow, float Mid, float Wide) ClassicTiers => (NavPaneNarrowW, NavPaneMidW, NavPaneWideW);

        public static float NominalNavPaneDefaultFor(float viewportWidth)
            => NominalNavPaneDefaultFor(viewportWidth, ClassicTiers);

        public static float NominalNavPaneDefaultFor(float viewportWidth, in (float Narrow, float Mid, float Wide) tiers) =>
            viewportWidth >= NavPaneWideEnterW ? tiers.Wide
            : viewportWidth >= NavPaneMidEnterW ? tiers.Mid
            : tiers.Narrow;

        /// <summary>Pre-measure seed. A zero/unknown viewport (before the first bounds callback) takes the narrow
        /// tier; the viewport effect commits the real tier before the first layout.</summary>
        public static float InitialNavPaneDefaultForViewport(float viewportWidth)
            => InitialNavPaneDefaultForViewport(viewportWidth, ClassicTiers);

        /// <inheritdoc cref="InitialNavPaneDefaultForViewport(float)"/>
        public static float InitialNavPaneDefaultForViewport(float viewportWidth,
            in (float Narrow, float Mid, float Wide) tiers)
            => viewportWidth <= 0f ? tiers.Narrow : NominalNavPaneDefaultFor(viewportWidth, tiers);

        /// <summary>Widen immediately; shrink only after <see cref="NavPaneHysteresisDip"/> past the threshold — with
        /// the dip ADDED, because here a LARGER number is the wider tier. So 1400 widens to the mid tier at once, and
        /// the mid tier holds down to 1376.</summary>
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

        public static float ClampNavPaneWidth(float w) => Math.Clamp(w, NavPaneMinW, NavPaneMaxW);

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

    // ══ 2. THE MERGED CHROME ROW'S ALLOCATOR ════════════════════════════════════════════════════════════════════════

    /// <summary>Which shape the merged row's search takes at this width.</summary>
    public enum MergedSearchMode : byte { Field, Icon }

    /// <summary>Pure pressure allocator for the single 48-DIP chrome row. Tabs are never projected out: their measured
    /// natural extent only decides whether the centred search may occupy a full field or must yield to the
    /// caption-adjacent icon. Structural promotions carry the standard 40-DIP reserve; demotions are immediate.
    /// <para><b>Do not replace this with an <c>if (width &lt; X)</c> ladder.</b> Every threshold that remains
    /// (1360 / 1200 / 520) is a COST INPUT to the budget, not a layout branch.</para></summary>
    public readonly record struct Chrome(
        bool ShowName,
        bool ShowActions,
        bool ShowForward,
        bool ShowBack,
        bool ShowNewTab,
        bool ShowTrailing,
        MergedSearchMode SearchMode,
        float SearchWidth,
        // The tabs viewport's RESERVED width (navigation and add buttons excluded — the row adds those on top). Issue #88: the box used
        // to HUG the tab strip's own measured content, so a title swinging inside the strip's min/max (or a tab count
        // change) shoved the centred search box by up to half the swing. This is a QUANTISED, content-independent
        // stand-in for that hug — sized off the measured natural extent, clamped to whatever the row can actually spare,
        // and held across resolves with a WIDEN-IMMEDIATELY / NARROW-AFTER-hysteresis shape: the OPPOSITE polarity
        // from the boolean stages below (which promote late, demote at once), because here it is GROWTH that must
        // never clip a longer title and SHRINKING that must not reclaim space only to hand it straight back.
        float LeadClusterW)
    {
        /// <summary>Bell · Friends · Pin · Settings are in the trailing row together.</summary>
        public bool ActionsInRow => ShowActions;

        /// <summary>Below the threshold: bell and friends become profile-menu rows (Settings always is one; pin simply
        /// drops — the tab/page context menu still offers it).</summary>
        public bool ActionsInMenu => !ShowActions;

        public bool BareAvatar => !ShowName && !ShowActions;

        public static Chrome FromWidth(float width, int tabCount)
            => Resolve(width, EstimatedTabExtent(tabCount), null);

        public static Chrome Resolve(float width, int tabCount, Chrome? previous = null)
            => Resolve(width, EstimatedTabExtent(tabCount), previous);

        public static Chrome Resolve(float width, float naturalTabExtent, Chrome? previous = null)
        {
            width = MathF.Max(0f, width);
            naturalTabExtent = MathF.Max(Layout.ChromeTabViewportMinW, naturalTabExtent);
            var candidate = StageFor(width, naturalTabExtent);
            if (previous is not { } old) return Compose(width, naturalTabExtent, in candidate, null);

            var reserved = StageFor(MathF.Max(0f, width - Layout.ChromePromotionHysteresisW), naturalTabExtent);
            var held = new Stage(
                candidate.Name && (old.ShowName || reserved.Name),
                candidate.Actions && (old.ShowActions || reserved.Actions),
                candidate.Forward && (old.ShowForward || reserved.Forward),
                candidate.Back && (old.ShowBack || reserved.Back),
                candidate.NewTab && (old.ShowNewTab || reserved.NewTab),
                candidate.Trailing && (old.ShowTrailing || reserved.Trailing),
                candidate.Field && (old.SearchMode == MergedSearchMode.Field || reserved.Field));
            return Compose(width, naturalTabExtent, in held, old.LeadClusterW);
        }

        public float FixedBudgetFor()
            => FixedBudget(ShowName, ShowActions, ShowForward, ShowBack, ShowNewTab, ShowTrailing);

        public float FootprintFor(float naturalTabExtent)
            => FixedBudgetFor()
             + (SearchMode == MergedSearchMode.Field ? SearchWidth : Layout.ChromeSearchIconW)
             + RequiredTabExtent(naturalTabExtent);

        public static float EstimatedTabExtent(int tabCount, int pinnedCount = 0)
        {
            int open = Math.Max(1, tabCount);
            int pinned = Math.Clamp(pinnedCount, 0, open);
            return pinned * Layout.ChromePinnedTabW + (open - pinned) * Layout.ChromeTabMinW;
        }

        /// <summary>Measured titles retain their full natural width whenever the window can fit them. Round UP so
        /// the quantisation itself never clips the last glyph or forces a needless horizontal scroll.</summary>
        public static float RequiredTabExtent(float naturalTabExtent)
            => MathF.Max(Layout.ChromeTabViewportMinW, QuantiseUp(naturalTabExtent));

        /// <summary>The island contains navigation and the add button beside the measured scrolling viewport.</summary>
        public float TabIslandWidth => LeadClusterW
            + (ShowBack ? Layout.ChromeNavButtonW : 0f)
            + (ShowForward ? Layout.ChromeNavButtonW : 0f)
            + (ShowNewTab ? Layout.ChromeAddSlotW : 0f);

        public static float PreferredSearchWidth(float width)
            => QuantiseDown(Math.Clamp(
                width * Layout.ChromeSearchWidthRatio,
                Layout.ChromeSearchMinW,
                Layout.ChromeSearchMaxW));

        /// <summary>The row's non-tab, non-search DIPs. <paramref name="actionsInRow"/> reserves FOUR nav buttons at
        /// once (bell, friends, pin, settings) REGARDLESS of whether pin actually applies to the current destination:
        /// a page that gains/loses a pin row must not reflow the whole trailing island, and an element tree that
        /// disagrees with the budget reflows the island on every navigation. There is no "…" reservation — Forward
        /// simply hides below its own threshold instead of moving to an overflow menu.</summary>
        public static float FixedBudget(bool name, bool actionsInRow, bool forward, bool back, bool newTab, bool trailing)
            => Layout.ChromeBarLeadW
             + Layout.ChromeThemeToggleW
             + (back ? Layout.ChromeNavButtonW : 0f)
             + (forward ? Layout.ChromeNavButtonW : 0f)
             + (newTab ? Layout.ChromeAddSlotW : 0f)
             + (trailing ? Layout.ChromeProfileChipW : 0f)
             + (trailing && name ? Layout.ChromeProfileNameW : 0f)
             + (actionsInRow ? 4f * Layout.ChromeNavButtonW : 0f)
             + 2f * Layout.ChromeGutterMinW
             + Layout.ChromeMinDragStripW
             + Layout.ChromeCaptionClusterW;

        readonly record struct Stage(bool Name, bool Actions, bool Forward, bool Back, bool NewTab, bool Trailing, bool Field);

        static Stage StageFor(float width, float naturalTabExtent)
        {
            bool name = width >= Layout.ChromeNameEnterW;
            bool actionsInRow = width >= Layout.ChromeActionsEnterW;
            bool forward = width > Layout.ChromeForwardEnterW;
            bool back = true, newTab = true, trailing = true;

            // The tab viewport is the last elastic lane. Under extreme pressure shed fixed islands before allowing it
            // to disappear; the captions and the compact search trigger never participate in that trade.
            bool FitsEssential()
                => width - FixedBudget(name, actionsInRow, forward, back, newTab, trailing) - Layout.ChromeSearchIconW
                   >= Layout.ChromeTabViewportMinW;
            if (!FitsEssential()) newTab = false;
            if (!FitsEssential()) trailing = false;
            if (!FitsEssential()) back = false;

            float search = PreferredSearchWidth(width);
            float tabsRequired = RequiredTabExtent(naturalTabExtent);
            float tabLaneWithField = width - FixedBudget(name, actionsInRow, forward, back, newTab, trailing) - search;
            bool field = tabLaneWithField >= tabsRequired;
            return new Stage(name, actionsInRow, forward, back, newTab, trailing, field);
        }

        static Chrome Compose(float width, float naturalTabExtent, in Stage stage, float? previousLeadClusterW)
        {
            float searchWidth = stage.Field ? PreferredSearchWidth(width) : Layout.ChromeSearchIconW;
            return new Chrome(stage.Name, stage.Actions, stage.Forward, stage.Back, stage.NewTab, stage.Trailing,
                stage.Field ? MergedSearchMode.Field : MergedSearchMode.Icon, searchWidth,
                LeadClusterFor(width, naturalTabExtent, in stage, searchWidth, previousLeadClusterW));
        }

        /// <summary>The tab lane's reserved width. Bounded by what the row can spare once the resolved fixed budget
        /// and search allotment are taken out — the SAME budget the boolean stages already pay, so this can never
        /// over-reserve and starve the row — and then held with the widen-now / narrow-later shape.</summary>
        static float LeadClusterFor(float width, float naturalTabExtent, in Stage stage, float searchWidth,
            float? previousLeadClusterW)
        {
            float budget = FixedBudget(stage.Name, stage.Actions, stage.Forward, stage.Back, stage.NewTab, stage.Trailing);
            float available = MathF.Max(Layout.ChromeTabViewportMinW, width - budget - searchWidth);
            float desired = MathF.Max(Layout.ChromeTabViewportMinW,
                MathF.Min(RequiredTabExtent(naturalTabExtent), available));
            float candidate = QuantiseDown(desired);
            float held = HeldLeadCluster(candidate, previousLeadClusterW);
            // A STRUCTURAL shrink of `available` (a window resize, or a stage losing a fixed island) always wins over
            // the hold — the hold exists to smooth CONTENT jitter at a fixed width, never to overflow the row.
            return MathF.Max(Layout.ChromeTabViewportMinW, MathF.Min(held, available));
        }

        /// <summary>WIDEN-IMMEDIATELY / NARROW-AFTER-<see cref="Layout.ChromePromotionHysteresisW"/>: a longer tab
        /// title must never clip (so growth is never delayed), but a shorter one must not yank the reservation back
        /// only to need it again next frame — the opposite polarity from the boolean stages, which hide overflow by
        /// NOT showing something yet, while this hides jitter by NOT giving space back yet.</summary>
        static float HeldLeadCluster(float candidate, float? previous)
        {
            if (previous is not { } prev) return candidate;
            if (candidate >= prev) return candidate;
            return prev - candidate >= Layout.ChromePromotionHysteresisW ? candidate : prev;
        }

        /// <summary>The tab strip's MEASURED natural extent, as the allocator consumes it: rounded UP to the
        /// width quantum and floored at one tab's minimum. The strip publishes its content extent on every re-measure;
        /// quantising here is what stops a sub-pixel title reflow from re-resolving the row.</summary>
        public static float TabExtentFromMetrics(float contentExtent)
            => MathF.Max(Layout.ChromeTabMinW,
                QuantiseUp(contentExtent));

        /// <summary>The UPWARD seed applied the instant a tab is added or unpinned, so the row can collapse the search in
        /// the same event turn instead of squeezing the new tab behind a stale measurement. Never lowers the measured
        /// value — the strip's own measurement does that.</summary>
        public static float SeedTabExtent(float measured, int tabCount, int pinnedCount)
            => MathF.Max(measured, EstimatedTabExtent(tabCount, pinnedCount));

        static float QuantiseDown(float value)
            => MathF.Floor(value / Layout.ChromeWidthQuantumW) * Layout.ChromeWidthQuantumW;

        static float QuantiseUp(float value)
            => MathF.Ceiling(value / Layout.ChromeWidthQuantumW) * Layout.ChromeWidthQuantumW;
    }

    // ══ 3. THE TAB WORKSPACE ════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>One open browser-style tab: a stable identity, the route it currently shows, and whether it survives
    /// the session (pinned tabs are the only cross-session subset).</summary>
    public sealed record WorkspaceTab(int Id, Route Route, bool Pinned);

    /// <summary>What the shell must do with its route after a workspace op. <see cref="Restore"/> is a tab SWITCH:
    /// show the tab's page as it was — no history push, no origin write. <see cref="Push"/> is a real navigation (a
    /// new tab opening on a fresh route). <see cref="None"/> leaves the route alone.</summary>
    public enum TabNavIntent : byte { None, Restore, Push }

    public readonly record struct TabNavResult(TabNavIntent Intent, Route? Route, int ActiveIndex);

    /// <summary>The shell's tab list + which tab is active, as a pure model. <see cref="ActiveId"/> is the ONE source
    /// of truth for "which tab is showing": the strip's own <c>SelectedIndex</c> cell is a PROJECTION the shell
    /// re-asserts after every op, so the engine control pre-writing it before it raises its selection event (its
    /// documented controlled-prop idiom) can never make an activation look like a no-op. That pre-write is exactly
    /// what made the old shell early-return on "selection unchanged" and leave the content on the previous tab's page.
    /// <para>Ids are minted once and never reused, so drag/reorder/pin moves keep every tab's identity (the keep-alive
    /// slot key and the strip's item key are both built from it).</para></summary>
    public sealed class TabWorkspace
    {
        readonly List<WorkspaceTab> _tabs = [];
        int _nextId = 1;

        public IReadOnlyList<WorkspaceTab> Tabs => _tabs;
        public int Count => _tabs.Count;
        public int ActiveId { get; private set; }
        public int ActiveIndex => IndexOf(ActiveId);
        public WorkspaceTab Active => _tabs[ActiveIndex];

        /// <summary>The pinned tab that was selected most recently — the one a cold start reopens on. Survives
        /// switching to an UNPINNED tab (that is the whole point: the session-only tab is gone next launch, this one
        /// is not).</summary>
        public int LastSelectedPinnedId { get; private set; } = -1;

        /// <summary>Bumped whenever the PERSISTED subset changes (pinned membership/order, a pinned tab's route, or
        /// <see cref="LastSelectedPinnedId"/>). The shell compares it against the revision it last saved, so every op
        /// that touches pins persists them and no op that does not pays for a settings write.</summary>
        public int PinnedRevision { get; private set; }

        public TabWorkspace() => SeedHome();

        public int IndexOf(int id)
        {
            for (int i = 0; i < _tabs.Count; i++) if (_tabs[i].Id == id) return i;
            return -1;
        }

        /// <summary>Open a new tab on <paramref name="route"/> and make it active. An unpinned tab appends at the end;
        /// a pinned one joins the end of the pinned block (pins are always a prefix). Always a
        /// <see cref="TabNavIntent.Push"/>: the route is a new place, so it belongs on the back stack.</summary>
        public TabNavResult Open(Route route, bool pinned = false)
        {
            var tab = new WorkspaceTab(_nextId++, route, pinned);
            int at = pinned ? PinnedBoundary() : _tabs.Count;
            _tabs.Insert(at, tab);
            ActiveId = tab.Id;
            if (pinned) { LastSelectedPinnedId = tab.Id; PinnedRevision++; }
            return new TabNavResult(TabNavIntent.Push, route, at);
        }

        /// <summary>Make the tab at <paramref name="index"/> active. The index is whatever the caller has in hand (the
        /// strip's click index, a spring-load's id lookup) — never trusted as "already selected": only
        /// <see cref="ActiveId"/> decides that. Out of range → <see cref="TabNavIntent.None"/>.</summary>
        public TabNavResult Activate(int index)
        {
            if ((uint)index >= (uint)_tabs.Count) return None();
            var tab = _tabs[index];
            if (tab.Id == ActiveId) return None();
            ActiveId = tab.Id;
            if (tab.Pinned && LastSelectedPinnedId != tab.Id) { LastSelectedPinnedId = tab.Id; PinnedRevision++; }
            return new TabNavResult(TabNavIntent.Restore, tab.Route, index);
        }

        public TabNavResult ActivateById(int id) => Activate(IndexOf(id));

        /// <summary>The active tab as an index into the PINNED block, or −1 when the active tab is session-only — what the
        /// session document persists (G-258). Never the tab id: ids are minted per process (<c>_nextId</c> restarts at 1),
        /// so a persisted id can name an unrelated tab on the next launch.</summary>
        public int ActivePinnedIndex
        {
            get
            {
                int index = ActiveIndex;
                return index >= 0 && _tabs[index].Pinned ? index : -1;   // pins are a prefix: the index IS the pinned index
            }
        }

        /// <summary>Re-select a PINNED tab by its index in the pinned block WITHOUT a route intent — the cold-start session
        /// restore, which already knows the route it is about to put on the tab (<see cref="RestoreActiveRoute"/>). Does
        /// not touch <see cref="LastSelectedPinnedId"/>: restore must not rewrite pins. False when there is no such pin
        /// (−1 = the session's tab was session-only, or the pins changed since), leaving the pinned default selected.</summary>
        public bool TrySelectPinned(int pinnedIndex)
        {
            if ((uint)pinnedIndex >= (uint)PinnedBoundary()) return false;
            ActiveId = _tabs[pinnedIndex].Id;
            return true;
        }

        /// <summary>Close the tab at <paramref name="index"/>. The LAST tab is never closed. Closing the ACTIVE tab
        /// selects its right neighbour (else the left one) and restores that tab's route; closing a BACKGROUND tab
        /// changes nothing the user is looking at, so it is <see cref="TabNavIntent.None"/>.</summary>
        public TabNavResult Close(int index)
        {
            if (_tabs.Count <= 1 || (uint)index >= (uint)_tabs.Count) return None();
            var closing = _tabs[index];
            bool wasActive = closing.Id == ActiveId;
            _tabs.RemoveAt(index);
            if (closing.Pinned) PinnedRevision++;
            if (!wasActive)
            {
                ReconcileLastPinned();
                return None();
            }
            int next = Math.Min(index, _tabs.Count - 1);
            ActiveId = _tabs[next].Id;
            ReconcileLastPinned();
            return new TabNavResult(TabNavIntent.Restore, _tabs[next].Route, next);
        }

        public TabNavResult CloseById(int id) => Close(IndexOf(id));

        /// <summary>Close every tab <paramref name="remove"/> selects (the "close others / to the right / all
        /// unpinned" menu). An emptied workspace reseeds a Home tab. The active tab, when it survives, stays where it
        /// is; when it went, the tab now at its old index (clamped) takes over.</summary>
        public TabNavResult CloseWhere(Func<WorkspaceTab, bool> remove)
        {
            int oldIndex = ActiveIndex;
            bool activeGone = false;
            for (int i = _tabs.Count - 1; i >= 0; i--)
            {
                var tab = _tabs[i];
                if (!remove(tab)) continue;
                _tabs.RemoveAt(i);
                if (tab.Pinned) PinnedRevision++;
                if (tab.Id == ActiveId) activeGone = true;
            }
            if (_tabs.Count == 0)
            {
                SeedHome();
                ReconcileLastPinned();
                return new TabNavResult(TabNavIntent.Restore, _tabs[0].Route, 0);
            }
            if (!activeGone)
            {
                ReconcileLastPinned();
                return None();
            }
            int next = Math.Clamp(oldIndex, 0, _tabs.Count - 1);
            ActiveId = _tabs[next].Id;
            ReconcileLastPinned();
            return new TabNavResult(TabNavIntent.Restore, _tabs[next].Route, next);
        }

        /// <summary>Pin/unpin a tab: it moves to the pinned boundary (pins are always the leading block) and keeps its
        /// identity, so the active tab stays active even when its index changes. Never a route intent.</summary>
        public TabNavResult SetPinned(int id, bool pinned)
        {
            int index = IndexOf(id);
            if (index < 0 || _tabs[index].Pinned == pinned) return None();
            var tab = _tabs[index] with { Pinned = pinned };
            _tabs.RemoveAt(index);
            _tabs.Insert(PinnedBoundary(), tab);
            if (pinned && id == ActiveId) LastSelectedPinnedId = id;
            else if (!pinned && LastSelectedPinnedId == id) LastSelectedPinnedId = FirstPinnedId();
            PinnedRevision++;
            return None();
        }

        /// <summary>Every navigation lands on the ACTIVE tab (browser semantics: the tab follows the page). Returns
        /// whether that tab is pinned, i.e. whether the persisted subset just changed.</summary>
        public bool SetActiveRoute(Route route)
        {
            int index = ActiveIndex;
            var tab = _tabs[index];
            if (!SameSlot(tab.Route, route)) _tabs[index] = tab with { Route = route };
            if (tab.Pinned) PinnedRevision++;
            return tab.Pinned;
        }

        /// <summary>Cold start, the session half (G-258): the restored route lands on the ACTIVE tab, so the strip's label
        /// names the page on screen instead of the seeded Home — the same "the tab follows the page" rule
        /// <see cref="SetActiveRoute"/> applies to a navigation, but WITHOUT bumping <see cref="PinnedRevision"/>: restore
        /// must not rewrite pins (0.2.9 <c>RestoreSessionNav</c>). Returns the active tab's id.</summary>
        public int RestoreActiveRoute(Route route)
        {
            int index = ActiveIndex;
            var tab = _tabs[index];
            if (!SameSlot(tab.Route, route)) _tabs[index] = tab with { Route = route };
            return tab.Id;
        }

        /// <summary>Cold start: replace the workspace with the persisted pins (routes run through
        /// <see cref="Shell.Parse"/>, so a legacy key in the settings file never reaches the router) and select the
        /// one that was last selected. No pins → a single Home tab. Returns the active tab's route.</summary>
        public Route RestorePinned(in WorkspaceTabsSnapshot snapshot)
        {
            _tabs.Clear();
            for (int i = 0; i < snapshot.Tabs.Length; i++)
            {
                var saved = snapshot.Tabs[i];
                _tabs.Add(new WorkspaceTab(_nextId++, Parse(saved.Route, saved.Arg ?? ""), Pinned: true));
            }
            if (_tabs.Count == 0)
            {
                SeedHome();
                LastSelectedPinnedId = -1;
                return _tabs[0].Route;
            }
            int selected = Math.Clamp(snapshot.LastSelected, 0, _tabs.Count - 1);
            ActiveId = _tabs[selected].Id;
            LastSelectedPinnedId = ActiveId;
            return _tabs[selected].Route;
        }

        /// <summary>The persisted subset: pinned tabs in order + which of them is <see cref="LastSelectedPinnedId"/>
        /// (the first pin when none is). Round-trips through <see cref="WorkspaceTabs"/>.</summary>
        public WorkspaceTabsSnapshot PinnedSnapshot()
        {
            var pins = new List<PersistedTab>();
            int selected = -1;
            for (int i = 0; i < _tabs.Count; i++)
            {
                var tab = _tabs[i];
                if (!tab.Pinned) continue;
                if (tab.Id == LastSelectedPinnedId) selected = pins.Count;
                pins.Add(new PersistedTab(NameOf(tab.Route), ArgOf(tab.Route)));
            }
            if (pins.Count > 0 && selected < 0) selected = 0;
            return new WorkspaceTabsSnapshot(pins.ToArray(), selected);
        }

        /// <summary>How many tabs are pinned (always a prefix).</summary>
        public int PinnedCount => PinnedBoundary();

        /// <summary>Closable is a RULE, not a hover: a pinned tab never shows ⓧ, and neither does the last tab.</summary>
        public static bool IsClosable(WorkspaceTab tab, int tabCount) => !tab.Pinned && tabCount > 1;

        /// <summary>"Close all unpinned tabs" is enabled while ANY tab is unpinned.</summary>
        public bool HasAnyUnpinned()
        {
            for (int i = 0; i < _tabs.Count; i++) if (!_tabs[i].Pinned) return true;
            return false;
        }

        /// <summary>"Close other tabs" is enabled while an unpinned tab other than <paramref name="id"/> exists.</summary>
        public bool HasOtherUnpinned(int id)
        {
            for (int i = 0; i < _tabs.Count; i++) if (_tabs[i].Id != id && !_tabs[i].Pinned) return true;
            return false;
        }

        /// <summary>"Close tabs to the right" is enabled while an unpinned tab sits right of <paramref name="index"/>.</summary>
        public bool HasUnpinnedToRight(int index)
        {
            for (int i = Math.Max(0, index + 1); i < _tabs.Count; i++) if (!_tabs[i].Pinned) return true;
            return false;
        }

        TabNavResult None() => new(TabNavIntent.None, null, ActiveIndex);

        void SeedHome()
        {
            var home = new WorkspaceTab(_nextId++, new Route(RouteKind.Home), Pinned: false);
            _tabs.Add(home);
            ActiveId = home.Id;
        }

        int PinnedBoundary()
        {
            int boundary = 0;
            while (boundary < _tabs.Count && _tabs[boundary].Pinned) boundary++;
            return boundary;
        }

        int FirstPinnedId()
        {
            for (int i = 0; i < _tabs.Count; i++) if (_tabs[i].Pinned) return _tabs[i].Id;
            return -1;
        }

        /// <summary>After a removal or a selection: the active tab, when pinned, IS the last-selected pin; otherwise
        /// the remembered one must still exist, else the first pin (or none) stands in.</summary>
        void ReconcileLastPinned()
        {
            int before = LastSelectedPinnedId;
            var active = Active;
            if (active.Pinned) LastSelectedPinnedId = active.Id;
            else if (IndexOf(LastSelectedPinnedId) < 0) LastSelectedPinnedId = FirstPinnedId();
            if (LastSelectedPinnedId != before) PinnedRevision++;
        }
    }

    /// <summary>WHEN the strip relabels. A navigation commits <c>Motion</c>, <c>Current</c> and the tab's route in ONE
    /// flush, but the page it replaces is still on screen for its exit leg (<see cref="Design.Nav.ExitDurationMs"/>) —
    /// so a tab labelled from its own route names the NEXT page while the previous one is still leaving, one frame
    /// (and then 90 ms) ahead of the content. The strip therefore labels the active tab from <c>Shell.Shown</c>, the
    /// route the content host reports once the exit leg has run, and only falls back to the tab's own route when the
    /// staged route is not about that tab.</summary>
    public static class TabLabelStaging
    {
        /// <summary>The route the tab's label and glyph come from. <paramref name="shown"/> wins when it is a real route
        /// on the SAME tab (a tab id is 1-based; a Tab of 0 is a route that never went through a commit — the boot Home,
        /// a restored pin — and must not claim every other un-normalised tab); otherwise the tab's own route stands.
        /// The pin id and the drop target keep the tab's route regardless — they describe the DESTINATION, not what
        /// is on screen right now.</summary>
        public static Route LabelRoute(in Route tabRoute, in Route shown)
            => !shown.IsNone && shown.Tab != 0 && shown.Tab == tabRoute.Tab ? shown : tabRoute;

        /// <summary>How long the label lags the commit: the exit leg, exactly. The instant cut
        /// (<see cref="Design.PageMotionStyle.None"/>) and the OS reduced-motion preference both run no exit leg, so
        /// there the label lands with the page (the next timer tick).</summary>
        public static float DelayMs(bool instantCut, bool reducedMotion)
            => instantCut || reducedMotion ? 0f : Design.Nav.ExitDurationMs;
    }

    // ══ 4. THE PINNED SUBSET'S CODEC ════════════════════════════════════════════════════════════════════════════════

    /// <summary>One persisted tab: the opaque route key + its display arg, exactly the pair 0.2.9 wrote.</summary>
    public sealed record PersistedTab(string Route, string? Arg);

    /// <summary>The decoded pinned subset.</summary>
    public readonly record struct WorkspaceTabsSnapshot(PersistedTab[] Tabs, int LastSelected);

    /// <summary>AOT-safe codec for the pinned subset of the shell workspace. Ordinary tabs are deliberately
    /// session-only. v1; caps 128 tabs / 1024-char route / 4096-char arg; a corrupt or too-new document decodes to an
    /// EMPTY set rather than throwing.</summary>
    public static class WorkspaceTabs
    {
        public const int CurrentVersion = 1;
        public const int MaxTabs = 128;
        public const int MaxRouteChars = 1024;
        public const int MaxArgChars = 4096;

        public static WorkspaceTabsSnapshot Decode(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return new WorkspaceTabsSnapshot([], -1);
            try
            {
                var doc = JsonSerializer.Deserialize(raw, WorkspaceTabsJson.Default.WorkspaceTabsDocument);
                if (doc is null || doc.Version != CurrentVersion || doc.Tabs is null)
                    return new WorkspaceTabsSnapshot([], -1);
                var valid = new List<PersistedTab>(Math.Min(doc.Tabs.Length, MaxTabs));
                int selected = -1;
                for (int i = 0; i < doc.Tabs.Length && valid.Count < MaxTabs; i++)
                {
                    var tab = doc.Tabs[i];
                    if (tab is null || string.IsNullOrWhiteSpace(tab.Route) || tab.Route.Length > MaxRouteChars
                        || (tab.Arg?.Length ?? 0) > MaxArgChars) continue;
                    if (i == doc.LastSelected) selected = valid.Count;
                    valid.Add(new PersistedTab(tab.Route, tab.Arg));
                }
                if (selected >= valid.Count) selected = valid.Count > 0 ? 0 : -1;
                return new WorkspaceTabsSnapshot(valid.ToArray(), selected);
            }
            catch (Exception)
            {
                return new WorkspaceTabsSnapshot([], -1);
            }
        }

        public static string Encode(IReadOnlyList<PersistedTab> tabs, int lastSelected)
        {
            int count = Math.Min(tabs.Count, MaxTabs);
            var copy = new PersistedTab[count];
            for (int i = 0; i < count; i++) copy[i] = tabs[i];
            int selected = count == 0 ? -1 : Math.Clamp(lastSelected, 0, count - 1);
            return JsonSerializer.Serialize(
                new WorkspaceTabsDocument(CurrentVersion, selected, copy),
                WorkspaceTabsJson.Default.WorkspaceTabsDocument);
        }

        public static string Encode(in WorkspaceTabsSnapshot snapshot) => Encode(snapshot.Tabs, snapshot.LastSelected);
    }

}

/// <summary>The persisted pinned-tab document (v1). Namespace level, not nested: a source-generated
/// <c>JsonSerializerContext</c> cannot live inside a STATIC class.</summary>
internal sealed record WorkspaceTabsDocument(int Version, int LastSelected, Shell.PersistedTab[] Tabs);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(WorkspaceTabsDocument))]
internal sealed partial class WorkspaceTabsJson : JsonSerializerContext;
