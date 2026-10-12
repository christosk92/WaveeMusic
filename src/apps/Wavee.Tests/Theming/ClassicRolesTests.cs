// ── Wavee.Tests/Theming/ClassicRolesTests.cs — Classic's roles are today's `Design` values, and `R` reads them (T2a) ────
//
// Classic renders pixel-identical to before T2a because every value a view reads through `R` equals what it read from
// `Design`. This file pins that: the type roles against the bodies they replaced (the engine rung and its deviations, written
// out here as the old `Design.Type` wrote them), the space and motion roles against the literals of `Design.Size`,
// `Design.Dock` and `Design.Motion`, and the table's value equality (what keeps `Tok.Use` from calling an equal swap a change).
//
// NO TOKEN CHANGES HERE. `Tok` is process-wide and this assembly runs its facts in parallel, so nothing installs a definition:
// a process with none reads the roles the app registered as its baseline, which are Classic's. The facade's reaction to a
// definition change is `Wavee.ShellTests\StyleRolesTests.cs`, in the process where tokens may change.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Wavee.Themes.Classic;
using Wavee.Theming.Roles;
using Xunit;

namespace Wavee.Tests.Theming;

public sealed class ClassicRolesTests
{
    const string Display = "Segoe UI Variable Display";

    // ── the type roles, against the bodies they replaced ─────────────────────────────────────────────────────────────────

    public static TheoryData<string> TextRoleNames => new()
    {
        "TrackTitle", "CardTitle", "TrackMeta", "Eyebrow", "MicroMeta", "DenseMeta", "DenseTitle", "SheetTitle", "RailHeader",
        "ModuleHeader", "PageHero", "DetailHero", "SurfaceDisplay", "HeroTitle", "ArtistDisplay", "ArtistTitle",
        "ArtistCompactTitle", "NowPlayingTitle", "PickQuote", "FoldTitle", "PivotLabel", "NpvLyric",
    };

    // Each expression is the body of the `Design.Type` member it replaced: the engine rung, then the member's `with`.
    static TextEl Before(string name, string s) => name switch
    {
        "TrackTitle" => Ui.BodyStrong(s),
        "CardTitle" => Ui.BodyStrong(s),
        "TrackMeta" => Ui.Caption(s).Secondary(),
        "Eyebrow" => Ui.Caption(s) with { Weight = 600, CharSpacing = 30f },
        "MicroMeta" => Ui.Caption(s) with { Size = 11f, LineHeight = 15f },
        "DenseMeta" => Ui.Body(s) with { Size = 13f, LineHeight = 18f },
        "DenseTitle" => Ui.BodyStrong(s) with { Size = 13f, LineHeight = 18f },
        "SheetTitle" => Ui.BodyLarge(s) with { Size = 16f, LineHeight = 22f, Weight = 600 },
        "RailHeader" => Ui.Subtitle(s),
        "ModuleHeader" => Ui.Subtitle(s) with { FontFamily = Display, CharSpacing = -6f },
        "PageHero" => Ui.Title(s),
        "DetailHero" => Ui.Title(s) with { FontFamily = Display, CharSpacing = -20f },
        "SurfaceDisplay" => Ui.TitleLarge(s) with { FontFamily = Display, Weight = 400, CharSpacing = -12f },
        "HeroTitle" => Ui.TitleLarge(s) with { FontFamily = Display, Weight = 600, CharSpacing = -12f },
        "ArtistDisplay" => Ui.Display(s) with { FontFamily = Display, Size = 84f, LineHeight = 96f, Weight = 700, CharSpacing = -28f, MinSize = 68f },
        "ArtistTitle" => Ui.TitleLarge(s) with { FontFamily = Display, Size = 48f, LineHeight = 60f, Weight = 700, CharSpacing = -20f, MinSize = 40f },
        "ArtistCompactTitle" => Ui.Title(s) with { FontFamily = Display, Size = 32f, LineHeight = 40f, Weight = 700, CharSpacing = -12f, MinSize = 28f },
        "NowPlayingTitle" => Ui.Subtitle(s),
        "PickQuote" or "FoldTitle" => Ui.Title(s) with { FontFamily = Display, Weight = 400, CharSpacing = -12f },
        "PivotLabel" => Ui.BodyLarge(s) with { FontFamily = Display, Size = 19f, LineHeight = 25f, Weight = 350, CharSpacing = -6f },
        "NpvLyric" => Ui.Subtitle(s) with { FontFamily = Display, Weight = 350, CharSpacing = -6f },
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
    };

    static TextEl After(string name, string s) => name switch
    {
        "TrackTitle" => R.Type.TrackTitle(s),
        "CardTitle" => R.Type.CardTitle(s),
        "TrackMeta" => R.Type.TrackMeta(s),
        "Eyebrow" => R.Type.Eyebrow(s),
        "MicroMeta" => R.Type.MicroMeta(s),
        "DenseMeta" => R.Type.DenseMeta(s),
        "DenseTitle" => R.Type.DenseTitle(s),
        "SheetTitle" => R.Type.SheetTitle(s),
        "RailHeader" => R.Type.RailHeader(s),
        "ModuleHeader" => R.Type.ModuleHeader(s),
        "PageHero" => R.Type.PageHero(s),
        "DetailHero" => R.Type.DetailHero(s),
        "SurfaceDisplay" => R.Type.SurfaceDisplay(s),
        "HeroTitle" => R.Type.HeroTitle(s),
        "ArtistDisplay" => R.Type.ArtistDisplay(s),
        "ArtistTitle" => R.Type.ArtistTitle(s),
        "ArtistCompactTitle" => R.Type.ArtistCompactTitle(s),
        "NowPlayingTitle" => R.Type.NowPlayingTitle(s),
        "PickQuote" => R.Type.PickQuote(s),
        "FoldTitle" => R.Type.FoldTitle(s),
        "PivotLabel" => R.Type.PivotLabel(s),
        "NpvLyric" => R.Type.NpvLyric(s),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
    };

    // NaN means "unset" (a line height, an auto-fit floor): float.Equals treats two NaNs as equal, `==` does not.
    static void SameFloat(float expected, float actual, string what) => Assert.True(expected.Equals(actual), what + ": " + expected + " vs " + actual);

    static void SameLook(TextEl expected, TextEl actual, string name)
    {
        SameFloat(expected.Size, actual.Size, name + " size");
        SameFloat(expected.LineHeight, actual.LineHeight, name + " line height");
        SameFloat(expected.CharSpacing, actual.CharSpacing, name + " tracking");
        SameFloat(expected.MinSize, actual.MinSize, name + " auto-fit floor");
        Assert.Equal(expected.ResolvedWeight, actual.ResolvedWeight);
        Assert.Equal(expected.Weight, actual.Weight);
        Assert.Equal(expected.FontFamily, actual.FontFamily);
        Assert.Equal(expected.Wrap, actual.Wrap);
        Assert.Equal(expected.Trim, actual.Trim);
        Assert.Equal(expected.MaxLines, actual.MaxLines);
        Assert.Equal(expected.LineStacking, actual.LineStacking);
        Assert.Equal(expected.LineBounds, actual.LineBounds);
        Assert.Equal(expected.Color, actual.Color);   // the rung's own (shared) ink, or the secondary one
        Assert.Equal(expected.Text, actual.Text);
    }

    [Theory]
    [MemberData(nameof(TextRoleNames))]
    public void Every_text_role_builds_the_element_its_design_alias_did(string name)
        => SameLook(Before(name, "x"), After(name, "x"), name);

    [Fact]
    public void The_baseline_paired_roles_keep_the_headings_metrics_face_and_tracking()
    {
        // The old `Paired(title, meta, face, tracking)`: the heading rung's metrics, the face and tracking of the role.
        var module = R.Type.ModuleHeader("Radio", "20 stations");
        var rail = R.Type.RailHeader("Radio", "20 stations");
        var stat = R.Type.StatHero("2B", "monthly");
        var statAlone = R.Type.StatHero("2B", null);
        var subtitle = Ui.Subtitle("");
        var title = Ui.Title("");

        Assert.Equal(Display, module.FontFamily);
        Assert.Equal(-6f, module.CharSpacing);
        Assert.Equal(subtitle.Size, module.Size);
        Assert.Equal(subtitle.LineHeight, module.LineHeight);
        Assert.Equal(subtitle.ResolvedWeight, module.Weight);

        Assert.Null(rail.FontFamily);
        Assert.Equal(0f, rail.CharSpacing);
        Assert.Equal(subtitle.Size, rail.Size);
        Assert.Equal(subtitle.ResolvedWeight, rail.Weight);

        foreach (var span in new[] { stat, statAlone })
        {
            Assert.Equal(Display, span.FontFamily);
            Assert.Equal(-6f, span.CharSpacing);
            Assert.Equal(title.Size, span.Size);
            Assert.Equal(title.LineHeight, span.LineHeight);
            Assert.Equal(350, span.Weight);
        }
    }

    [Fact]
    public void The_type_roles_that_are_not_elements_keep_their_values()
    {
        Assert.Equal(Display, R.Type.DisplayFace);
        Assert.Equal(30f, R.Type.EyebrowTracking);
    }

    [Fact]
    public void The_facet_styles_are_the_ones_the_design_properties_built()
    {
        var title = new SelectorBarStyle
        {
            LabelSize = 28f,
            LineHeight = 36f,
            FontFamily = Display,
            CharSpacing = 0f,
            RestWeight = 400,
            SelectedWeight = 600,
            RestColor = Tok.TextSecondary,
            SelectedColor = Tok.TextPrimary,
            HoverColor = Tok.TextPrimary,
            PressedColor = Tok.TextSecondary,
            HoverFill = Tok.FillSubtleSecondary,
            PressedFill = Tok.FillSubtleTertiary,
            ShowPill = false,
            ItemPadding = new Edges4(8f, 2f, 8f, 2f),
            ItemHeight = 40f,
            ItemGap = 24f,
            LeadingInset = -8f,
        };
        Assert.Equal(title, R.Type.FacetTitleStyle);
        Assert.Equal(title with { LabelSize = 20f, LineHeight = 28f, ItemGap = 8f }, R.Type.FacetCompactStyle);
    }

    // ── the space and motion roles, against the literals they replaced ───────────────────────────────────────────────────

    [Fact]
    public void The_space_roles_are_the_design_size_and_dock_literals()
    {
        var space = R.Space;
        Assert.Equal(32f, space.SectionGap);
        Assert.Equal(40f, space.SectionGapWide);
        Assert.Equal(1600f, space.PageMaxW);
        Assert.Equal(148f, space.ShelfCardMin);
        Assert.Equal(188f, space.ShelfCardMax);
        Assert.Equal(12f, space.GridGap);
        Assert.Equal(330f, space.WideTileMin);
        Assert.Equal(440f, space.WideTileMax);
        Assert.Equal(16f / 9f, space.WideTileAspect);
        Assert.Equal(16f, space.FadeChip);
        Assert.Equal(24f, space.FadeShelf);
        Assert.Equal(36f, space.FadeRail);
        Assert.Equal(280f, space.FacePileFlyoutW);
        Assert.Equal(360f, space.FacePileFlyoutH);
        Assert.Equal(264f, space.FacePileListW);
        Assert.Equal(344f, space.FacePileListH);
        Assert.Equal(72f, space.DockReserve);
    }

    [Fact]
    public void The_motion_roles_are_the_design_motion_literals()
    {
        var motion = R.Motion;
        Assert.Equal(new ScaleTier(1.02f, 0.98f), motion.ScaleSubtle);
        Assert.Equal(new ScaleTier(1.04f, 0.96f), motion.ScaleStandard);
        Assert.Equal(new ScaleTier(1.07f, 0.92f), motion.ScaleEmphatic);
        Assert.Equal(40f, motion.StaggerMs);
        Assert.Equal(45f, motion.MastheadStaggerMs);
    }

    [Fact]
    public void A_scale_tier_collapses_to_one_under_reduced_motion_and_for_a_dead_affordance()
    {
        var tier = new ScaleTier(1.04f, 0.96f);
        bool reduced = FluentGpu.Dsl.Motion.ReducedMotion;
        Assert.Equal(reduced ? 1f : 1.04f, tier.Hover);
        Assert.Equal(reduced ? 1f : 0.96f, tier.Press);
        Assert.Equal(1f, tier.HoverIf(false));
        Assert.Equal(1f, tier.PressIf(false));
    }

    [Fact]
    public void Classic_cases_every_kind_of_word_as_written()
    {
        foreach (LabelKind kind in Enum.GetValues<LabelKind>())
            Assert.Equal(TextCase.AsWritten, R.Case.For(kind));
        Assert.Equal(TextCase.Lower, new CaseRoles(TextCase.AsWritten, TextCase.Lower, TextCase.Upper, TextCase.AsWritten).For(LabelKind.Navigation));
        Assert.Equal(TextCase.Upper, new CaseRoles(TextCase.AsWritten, TextCase.Lower, TextCase.Upper, TextCase.AsWritten).For(LabelKind.Heading));
    }

    // ── the table, and the facade over a process that installed no roles ─────────────────────────────────────────────────

    [Fact]
    public void A_process_with_no_installed_roles_reads_classics()
        => Assert.Same(ClassicRoles.Table, R.Current);

    sealed class OtherRoles : FluentGpu.Theming.IAppRoles
    {
        public bool DiffersOnlyInColours(FluentGpu.Theming.IAppRoles other) => true;
    }

    [Fact]
    public void Equal_tables_are_equal_by_value_and_differ_only_in_colours()
    {
        var table = ClassicRoles.Table;
        var copy = table with { Space = table.Space with { } };
        Assert.NotSame(table, copy);
        Assert.Equal(table, copy);
        Assert.Equal(table.GetHashCode(), copy.GetHashCode());
        Assert.True(table.DiffersOnlyInColours(copy));
        Assert.True(table.DiffersOnlyInColours(table));
    }

    [Fact]
    public void A_table_that_changes_any_role_it_defines_is_not_a_colour_only_change()
    {
        var table = ClassicRoles.Table;
        Assert.False(table.DiffersOnlyInColours(table with { Space = table.Space with { SectionGap = 48f } }));
        Assert.False(table.DiffersOnlyInColours(table with { Motion = table.Motion with { StaggerMs = 20f } }));
        Assert.False(table.DiffersOnlyInColours(table with { Type = table.Type with { TrackTitle = new TextRole(TypeRung.BodyStrong, Size: 15f, LineHeight: 21f) } }));
        Assert.False(table.DiffersOnlyInColours(table with { Case = new CaseRoles(TextCase.Lower, TextCase.Lower, TextCase.Lower, TextCase.Lower) }));
        Assert.False(table.DiffersOnlyInColours(new OtherRoles()));   // another implementation is always different
    }

    [Fact]
    public void A_changed_role_builds_a_changed_element_and_the_rest_are_untouched()
    {
        var table = ClassicRoles.Table;
        var bigger = table.Type with { TrackTitle = new TextRole(TypeRung.BodyStrong, Size: 21f, LineHeight: 30f) };
        Assert.Equal(21f, bigger.TrackTitle.Make("x").Size);
        Assert.Equal(30f, bigger.TrackTitle.Make("x").LineHeight);
        Assert.Equal(600, bigger.TrackTitle.Make("x").ResolvedWeight);   // the rung's weight survives a size-only deviation
        Assert.Equal(table.Type.CardTitle, bigger.CardTitle);
        Assert.Equal(14f, table.Type.TrackTitle.Make("x").Size);
    }
}
