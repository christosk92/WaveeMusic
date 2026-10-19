// ── Wavee.Tests/Theming/WinUiNavTests.cs — WinUI's nav pane, pure (P6 W2b) ─────────────────────────────────────────────────────────────
//
// What needs no mounted tree: the planner's WinUI differences (Home and Browse under no header, sections that never collapse, dividers only in
// the rail, no Library layout), the row ladder's numbers (the board `Shell-Nav`), the touch metrics (40 at Compact, the same 40 pitch), and the
// policy seam (`NavPanePolicy.WideMode`: Compact holds the rail at Standard and Huge; the default keeps today's behaviour). The mounted facts
// are `Wavee.ShellTests\WinUiTierLayoutTests.Nav.cs`.

using Wavee.Themes.WinUi;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Xunit;

namespace Wavee.Tests.Theming;

public sealed class WinUiNavTests
{
    static SidebarLayoutDoc Doc(Func<SidebarLayoutState, SidebarLayoutState>? edit = null) => PlanFixture.Doc(SidebarLayoutId.Classic, edit);

    static SidebarRowPlan Plan(SidebarLayoutDoc doc, SidebarProjectionInput input, SidebarPaneMode mode = SidebarPaneMode.Expanded)
        => WinUiNavPlanner.Build(doc, in input, new SidebarPlanOptions(Mode: mode));

    static SidebarProjectionInput OneInTree() => new(PlaylistTree: [PlanFixture.Playlist("pl:a")]);

    // ── the planner ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Home_holds_Home_and_Browse_without_a_header_and_no_section_has_a_divider()
    {
        var plan = Plan(Doc(), OneInTree());
        Assert.Equal(new[]
        {
            SidebarRowKind.IconRow, SidebarRowKind.IconRow,                           // Home, Browse
            SidebarRowKind.SectionHeader,                                             // Library
            SidebarRowKind.IconRow, SidebarRowKind.IconRow, SidebarRowKind.IconRow, SidebarRowKind.IconRow, SidebarRowKind.IconRow,
            SidebarRowKind.SectionHeader,                                             // Playlists
            SidebarRowKind.EntityRow, SidebarRowKind.TreeEnd,
        }, PlanFixture.Kinds(plan));
        Assert.Equal(new[] { SidebarCatalogue.HomeRoute, WinUiNavPlanner.BrowseRoute }, plan.Rows.Take(2).Select(r => r.Key).ToArray());
        Assert.DoesNotContain(plan.Rows, r => r.Kind == SidebarRowKind.Divider);
    }

    [Fact]
    public void The_rail_has_a_divider_between_sections_and_no_headers()
    {
        var plan = Plan(Doc(), OneInTree(), SidebarPaneMode.Compact);
        Assert.DoesNotContain(plan.Rows, r => r.Kind == SidebarRowKind.SectionHeader);
        Assert.Equal(2, plan.Rows.Count(r => r.Kind == SidebarRowKind.Divider));   // Home | Library | Playlists
        Assert.Equal(SidebarRowKind.IconRow, plan.Rows[0].Kind);
        Assert.Equal(SidebarRowKind.IconRow, plan.Rows[1].Kind);
    }

    [Fact]
    public void A_section_collapsed_under_Classic_still_plans_its_rows()
    {
        var doc = Doc(PlanFixture.Ops(new SetSectionCollapsed(SidebarLayoutId.Classic, "collections", true)));
        var plan = Plan(doc, OneInTree());
        Assert.Equal(5, PlanFixture.Keys(plan, SidebarRowKind.IconRow).Count(k => k is "liked" or "albums" or "artists" or "podcasts" or "audiobooks"));
    }

    [Fact]
    public void Settings_is_the_footer_not_a_plan_row()
    {
        var plan = Plan(Doc(), OneInTree());
        Assert.DoesNotContain(plan.Rows, r => r.Key == "settings");
    }

    [Fact]
    public void Browse_and_Home_are_owned_by_the_Home_section()
    {
        var doc = Doc();
        var input = OneInTree();
        Assert.Equal("home", WinUiNavPlanner.OwningSection(doc, in input, WinUiNavPlanner.BrowseRoute, _ => false));
        Assert.Equal("home", WinUiNavPlanner.OwningSection(doc, in input, SidebarCatalogue.HomeRoute, _ => false));
    }

    // ── the row ladder (board Shell-Nav) ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_ladder_is_the_boards_numbers()
    {
        Assert.Equal(36f, WinUiNavGeometry.RowHeight);
        Assert.Equal(40f, WinUiNavGeometry.PitchOf(SidebarRowShape.EntityTwoLine));          // every shape is one line: 36 + 2 + 2
        Assert.Equal(WinUiNavGeometry.PitchOf(SidebarRowShape.Glyph), WinUiNavGeometry.PitchOf(SidebarRowShape.EntityText));
        Assert.Equal(16f, WinUiNavGeometry.ArtOf(SidebarRowShape.EntityTwoLine));
        // the label sits at slot x 44 (pane 48): 12 lead + 16 glyph + 16 gap
        Assert.Equal(44f, WinUiNavGeometry.LabelX(SidebarRowShape.Glyph, glyph: true));
        Assert.Equal(28f, WinUiNavGeometry.IndentStep);
        Assert.Equal(0f, WinUiNavGeometry.PillX(2));                                        // the pill stays at the row's own left edge
        Assert.Equal(12f, WinUiNavGeometry.PillTop(WinUiNavGeometry.RowHeight));
        Assert.Equal(40f, WinUiNavGeometry.HeaderHeight);
        Assert.Equal(40f, WinUiNavGeometry.TileWidth);
    }

    [Fact]
    public void The_touch_ladder_clears_40_and_keeps_the_pitch()
    {
        var standard = WinUiNavMetrics.Standard;
        var touch = WinUiNavMetrics.Touch;
        Assert.True(touch.RowHeight >= 40f && touch.HeaderButton >= 40f);
        Assert.Equal(standard.RowHeight + 2f * standard.MarginY, touch.RowHeight + 2f * touch.MarginY);
        Assert.Equal(standard.PillTop, touch.PillTop);
    }

    [Fact]
    public void The_extents_are_one_pitch_per_row_kind()
    {
        var doc = Doc();
        var plan = Plan(doc, OneInTree());
        var section = doc.Find(SidebarSectionKind.Playlists)!;
        Assert.Equal(40f, WinUiNavExtents.HeightOf(plan.Rows, 0, doc.Find(SidebarSectionKind.Home)));
        Assert.Equal(WinUiNavGeometry.HeaderHeight, WinUiNavExtents.HeightOf(plan.Rows, 2, doc.Find(SidebarSectionKind.Collections)));
        int entity = plan.Rows.ToList().FindIndex(r => r.Kind == SidebarRowKind.EntityRow);
        Assert.Equal(40f, WinUiNavExtents.HeightOf(plan.Rows, entity, section));
    }

    // ── the policy seam ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void WideMode_defaults_to_nothing_forced_and_forces_the_band_when_set()
    {
        Assert.Equal(NavPaneMode.Expanded, NavPanePolicy.Default.WideMode);
        Assert.Equal(NavPaneMode.Expanded, NavPanePolicy.Default.ForcedModeOf(SidebarWindowBand.Wide));
        var held = NavPanePolicy.Default with { WideMode = NavPaneMode.Compact };
        Assert.Equal(NavPaneMode.Compact, held.ForcedModeOf(SidebarWindowBand.Wide));
        Assert.Equal(NavPaneMode.Compact, held.ForcedModeOf(SidebarWindowBand.Narrow));
        // the pure mode rules follow: the rail whatever the user collapsed, an overlay to open, no seam, no write to the user's collapse
        Assert.Equal(SidebarPaneMode.Compact, SidebarPaneModeRules.Resolve(SidebarWindowBand.Wide, userCollapsed: false, editing: false, held));
        Assert.True(SidebarPaneModeRules.HasOverlay(SidebarWindowBand.Wide, held));
        Assert.False(SidebarPaneModeRules.SeamVisible(SidebarWindowBand.Wide, held));
        Assert.False(SidebarPaneModeRules.WritesUserCollapsed(SidebarWindowBand.Wide, editing: false, held));
        Assert.False(SidebarPaneModeRules.HasOverlay(SidebarWindowBand.Wide, NavPanePolicy.Default));
        Assert.Contains((held with { WideMode = (NavPaneMode)99 }).Validate(), p => p.Contains("WideMode"));
    }

    [Fact]
    public void The_compact_option_holds_the_rail_at_every_tier_and_the_others_keep_the_users_choice_at_Standard()
    {
        var structure = new WinUiStructure();
        static OptionValues Values(WinUiNavigationPane pane) => OptionValues.Empty.With(WinUiStructure.NavigationPane.Id, pane);

        var compact = structure.Policies(Values(WinUiNavigationPane.Compact), StyleMetrics.Unset).NavPane;
        Assert.Equal(NavPaneMode.Overlay, compact.ModeFor(WidthTier.Compact));
        Assert.Equal(NavPaneMode.Compact, compact.ModeFor(WidthTier.Small));
        Assert.Equal(NavPaneMode.Compact, compact.ModeFor(WidthTier.Standard));
        Assert.Equal(NavPaneMode.Compact, compact.ModeFor(WidthTier.Huge));

        foreach (var pane in new[] { WinUiNavigationPane.Automatic, WinUiNavigationPane.Expanded })
        {
            var p = structure.Policies(Values(pane), StyleMetrics.Unset).NavPane;
            Assert.Equal(NavPaneMode.Expanded, p.ModeFor(WidthTier.Standard));
            Assert.Equal(NavPaneMode.Expanded, p.ModeFor(WidthTier.Huge));
            Assert.Empty(p.Validate());
        }
    }

    [Fact]
    public void The_pane_is_a_fixed_280_whatever_the_window()
    {
        var p = new WinUiStructure().Policies(OptionValues.Empty, StyleMetrics.Unset).NavPane;
        Assert.Equal(WinUiTiers.PaneW, p.MinWidth);
        Assert.Equal(WinUiTiers.PaneW, p.MaxWidth);
        Assert.Equal(WinUiTiers.PaneW, SidebarPaneModeRules.PresentedWidth(SidebarPaneMode.Expanded, 400f, 700f, p));   // an Expanded pane at 700 docks at 280
        Assert.Equal(WinUiTiers.RailW, SidebarPaneModeRules.PresentedWidth(SidebarPaneMode.Compact, 280f, 700f, p));
    }

    // ── the registry ────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void WinUi_draws_the_twelve_nav_keys_itself()
    {
        var registry = new WinUiStructure();
        var builder = new FluentGpu.Theming.ViewRegistryBuilder();
        registry.Register(builder);
        var built = builder.Build();
        foreach (var key in new[]
        {
            ViewKeys.ShellNavPane.Id, ViewKeys.ShellNavEmpty.Id, ViewKeys.ShellNavSectionFlyout.Id, ViewKeys.ShellNavFolderFlyout.Id, ViewKeys.ShellNavEdit.Id,
            ViewKeys.ShellNavLibraryHead.Id, ViewKeys.ShellNarrowDrawer.Id, ViewKeys.ShellMaterialLayer.Id, ViewKeys.ShellCoverTone.Id,
            ViewKeys.ShellCoverBlend.Id, ViewKeys.ShellCoverVeil.Id, ViewKeys.ShellCoverTint.Id,
        })
        {
            string? id = built.TemplateIdOf(key);
            Assert.True(id is not null && (id.StartsWith("winui.", StringComparison.Ordinal) || id.StartsWith("common.", StringComparison.Ordinal)), $"{key}: {id}");
        }
    }
}
