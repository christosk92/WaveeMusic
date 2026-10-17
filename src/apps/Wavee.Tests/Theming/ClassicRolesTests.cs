// ── Wavee.Tests/Theming/ClassicRolesTests.cs — Classic's roles are today's `Design` values, and `R` reads them (T2a) ────
//
// Classic renders pixel-identical to before T2a because every value a view reads through `R` equals what it read from
// `Design`. This file pins that: the type roles against the bodies they replaced (the engine rung and its deviations, written
// out here as the old `Design.Type` wrote them), the space and motion roles against the literals of `Design.Size`,
// `Design.Dock` and `Design.Motion`, and the table's value equality (what keeps `Tok.Use` from calling an equal swap a change).
// Contract 1.26 (P5, ZV) adds the pivot-and-panorama type roles and the shape roles: Classic fills them with what its surfaces draw
// today (pinned below against those surfaces' numbers), every style defines every role (reflection over the role records), and a
// shape difference is a NON-colour change.
//
// NO TOKEN CHANGES HERE. `Tok` is process-wide and this assembly runs its facts in parallel, so nothing installs a definition:
// a process with none reads the roles the app registered as its baseline, which are Classic's. The facade's reaction to a
// definition change is `Wavee.ShellTests\StyleRolesTests.cs`, in the process where tokens may change.

using System.Reflection;
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
        // contract 1.26: the voice Classic already sets for the same job
        "Panorama", "PanoramaCompact", "Hero", "Pivot", "SubPivot", "Numeral", "NumeralLarge", "NowPlayingDisplay", "Wordmark",
        "LyricsLine", "Heading", "QuickplayHead",
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
        // The 1.26 roles, against what the Classic surface doing the same job sets today.
        "Panorama" => Before("SurfaceDisplay", s),                        // a place's name: the surface masthead
        "PanoramaCompact" => Before("PageHero", s),                       // the page title
        "Hero" => Before("DetailHero", s),                                // ClassicDetailHero's record title
        "Pivot" => Before("PivotLabel", s),                               // the pivot tab header
        "SubPivot" => Ui.Body(s),                                         // the stock SelectorBar's 14/20 word
        "Numeral" or "NumeralLarge" => Ui.Caption(s),                     // ClassicRowCells.NumberCell (the track table and the artist chart)
        "NowPlayingDisplay" => Ui.TitleLarge(s) with { FontFamily = Display },   // the stage hero's 40/52 title (ClassicStageIdentity, Stage.TitleFont)
        "Wordmark" => Ui.Caption(s) with { Weight = 600 },                // the stage top bar's "Wavee" (ClassicStageGeometry.BrandSize/Line/Weight)
        "LyricsLine" => Ui.Title(s) with { Size = 36f, LineHeight = 46f, Weight = 700 },   // the stage's timed line, Lyrics.Surface.Timed(large: true)
        "Heading" => Before("RailHeader", s),                             // a section header
        "QuickplayHead" => Before("ModuleHeader", s),                     // a Home module header
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
        "Panorama" => R.Type.Panorama(s),
        "PanoramaCompact" => R.Type.PanoramaCompact(s),
        "Hero" => R.Type.Hero(s),
        "Pivot" => R.Type.Pivot(s),
        "SubPivot" => R.Type.SubPivot(s),
        "Numeral" => R.Type.Numeral(s),
        "NumeralLarge" => R.Type.NumeralLarge(s),
        "NowPlayingDisplay" => R.Type.NowPlayingDisplay(s),
        "Wordmark" => R.Type.Wordmark(s),
        "LyricsLine" => R.Type.LyricsLine(s),
        "Heading" => R.Type.Heading(s),
        "QuickplayHead" => R.Type.QuickplayHead(s),
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
        Assert.False(table.DiffersOnlyInColours(table with { Shape = table.Shape with { Cover = 2f } }));   // a shape is geometry: a transaction
        Assert.False(table.DiffersOnlyInColours(table with { Type = table.Type.WithPanorama(PanoramaSize.TitleLarge40) }));
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

    // ── contract 1.26: the Zune vocabulary under Classic ─────────────────────────────────────────────────────────────────

    [Fact]
    public void The_lyric_line_role_is_the_stage_lyrics_row()
    {
        var row = Lyrics.Surface.Timed(large: true);
        var line = R.Type.LyricsLine("x");
        Assert.Equal(row.FontSize, line.Size);
        Assert.Equal(row.LineHeight, line.LineHeight);
        Assert.Equal(row.Weight, line.ResolvedWeight);
    }

    [Fact]
    public void The_panorama_helper_puts_the_title_on_the_named_rung_and_keeps_the_face_and_tracking()
    {
        var type = ClassicRoles.Table.Type;
        var display = type.WithPanorama(PanoramaSize.Display68);
        var large = type.WithPanorama(PanoramaSize.TitleLarge40);

        var d = display.Panorama.Make("x");
        Assert.Equal(Ui.Display("x").Size, d.Size);                 // 68/92/600: the ramp's Display rung, the style's size deviations cleared
        Assert.Equal(Ui.Display("x").LineHeight, d.LineHeight);
        Assert.Equal(Ui.Display("x").ResolvedWeight, d.ResolvedWeight);
        Assert.Equal(Display, d.FontFamily);                         // the style's face and tracking survive
        Assert.Equal(-12f, d.CharSpacing);

        var l = large.Panorama.Make("x");
        Assert.Equal(Ui.TitleLarge("x").Size, l.Size);
        Assert.Equal(Ui.TitleLarge("x").LineHeight, l.LineHeight);
        Assert.Equal(Ui.TitleLarge("x").ResolvedWeight, l.ResolvedWeight);

        // Every call starts from the table it is given (no state carried from an earlier call), and nothing but the panorama moves.
        Assert.Equal(display, large.WithPanorama(PanoramaSize.Display68));
        Assert.Equal(type with { Panorama = display.Panorama }, display);
        Assert.Equal(type.PanoramaCompact, large.PanoramaCompact);
    }

    [Fact]
    public void The_big_numerals_helper_is_the_table_when_on_and_body_numerals_when_off()
    {
        var type = ClassicRoles.Table.Type;
        Assert.Same(type, type.WithBigNumerals(true));
        var off = type.WithBigNumerals(false);
        foreach (var role in new[] { off.Numeral, off.NumeralLarge })
        {
            var el = role.Make("7");
            Assert.Equal(Ui.Body("7").Size, el.Size);
            Assert.Equal(Ui.Body("7").LineHeight, el.LineHeight);
            Assert.Equal(Ui.Body("7").ResolvedWeight, el.ResolvedWeight);
        }
        Assert.Equal(type with { Numeral = off.Numeral, NumeralLarge = off.NumeralLarge }, off);
    }

    // The shape roles, against the radii the Classic surfaces draw today. The engine ramp's rungs are read from Classic's own definition
    // (`ClassicStyle` publishes `Design.Definition` carrying the table), so a ramp change that moved a Classic surface fails here too.
    [Fact]
    public void Classic_shape_roles_are_the_radii_its_surfaces_draw_today()
    {
        var shape = R.Shape;
        var radii = Design.Definition.Radii;
        Assert.Equal(radii.Card, shape.Card);                                 // Surface.Bound's card plate
        Assert.Equal(ClassicLibraryRules.CardArtCorner, shape.Cover);         // 6: the card art, the player bar's art, the episode row's art
        Assert.Equal(radii.Control, shape.Control);
        Assert.Equal(radii.Overlay, shape.Overlay);
        Assert.Equal(radii.Card, shape.Tile);                                 // ClassicSurface off the row form
        Assert.Equal(6f, shape.PinArt);                                       // Sidebar.Cover.Radius at 32 and 40
        Assert.Equal(radii.Full, shape.Avatar);
        Assert.Equal(SidebarGeometry.PillRadius, shape.CurrentMark);
        Assert.Equal(Design.Size.ContentPaneCorners, new CornerRadius4(shape.ContentSeam, 0f, 0f, 0f));
        Assert.Equal(radii.Control, shape.OmniRow);                           // ClassicSearch's suggestion row
        Assert.Equal(Slider.DefaultStyle.TrackCornerRadius, shape.SeekTrack); // ClassicTransport draws the engine slider's track
        Assert.Equal((8f, 6f, 4f, 8f, 8f, 6f, 999f, 2f, 8f, 4f, 2f),
            (shape.Card, shape.Cover, shape.Control, shape.Overlay, shape.Tile, shape.PinArt, shape.Avatar, shape.CurrentMark,
             shape.ContentSeam, shape.OmniRow, shape.SeekTrack));
    }

    [Fact]
    public void A_shape_role_reads_by_kind_and_as_uniform_corners()
    {
        var shape = ClassicRoles.Table.Shape;
        foreach (ShapeRole role in Enum.GetValues<ShapeRole>())
        {
            // The enum and the record name the same kinds: For(role) is the member of the role's name.
            var member = typeof(ShapeRoles).GetProperty(role.ToString());
            Assert.NotNull(member);
            Assert.Equal((float)member!.GetValue(shape)!, shape.For(role));
            Assert.Equal(CornerRadius4.All(shape.For(role)), shape.All(role));
        }
        Assert.Equal(Enum.GetValues<ShapeRole>().Length, FloatMembers(typeof(ShapeRoles)).Count());
    }

    // ── every style defines every role (themes plan S2): reflection over the role records ───────────────────────────────

    static IEnumerable<PropertyInfo> Members(Type record) => record
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.GetIndexParameters().Length == 0 && p.Name != "EqualityContract");

    static IEnumerable<PropertyInfo> FloatMembers(Type record) => Members(record).Where(p => p.PropertyType == typeof(float));

    /// <summary>The styles the app ships (Classic today; each new style's table joins this list and its switch below).</summary>
    public static TheoryData<string> Styles => new() { "Classic" };

    static WaveeStyleRoles TableOf(string style) => style switch
    {
        "Classic" => ClassicRoles.Table,
        _ => throw new ArgumentOutOfRangeException(nameof(style), style, null),
    };

    [Theory]
    [MemberData(nameof(Styles))]
    public void Every_style_defines_every_role(string style)
    {
        // No null family, no null text or facet role, every shape radius a finite number at or above zero.
        var table = TableOf(style);
        foreach (var family in Members(typeof(WaveeStyleRoles)))
            Assert.True(family.GetValue(table) is not null, style + ": " + family.Name + " is not defined");
        foreach (var role in Members(typeof(TypeRoles)))
            Assert.True(role.GetValue(table.Type) is not null, style + ": Type." + role.Name + " is not defined");
        foreach (var radius in FloatMembers(typeof(ShapeRoles)))
        {
            float r = (float)radius.GetValue(table.Shape)!;
            Assert.True(float.IsFinite(r) && r >= 0f, style + ": Shape." + radius.Name + " is " + r);
        }
    }

    [Fact]
    public void Every_text_role_has_its_call_and_the_call_builds_that_role()
    {
        // A role a view cannot call is dead vocabulary: every TextRole of the table has a TypeFactory method of the same name taking the text.
        var calls = typeof(TypeFactory).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.ReturnType == typeof(TextEl) && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(string))
            .ToDictionary(m => m.Name);
        object factory = R.Type;
        // StatHero is the one role built only as a numeral and its unit on one baseline (R.Type.StatHero(value, unit)).
        foreach (var role in Members(typeof(TypeRoles)).Where(p => p.PropertyType == typeof(TextRole) && p.Name != "StatHero"))
        {
            Assert.True(calls.TryGetValue(role.Name, out var call), "R.Type." + role.Name + "(text) is missing");
            var expected = ((TextRole)role.GetValue(ClassicRoles.Table.Type)!).Make("x");
            SameLook(expected, (TextEl)call!.Invoke(factory, ["x"])!, role.Name);
        }
    }
}
