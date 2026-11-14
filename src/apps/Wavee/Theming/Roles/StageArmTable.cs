// ── Theming/Roles/StageArmTable.cs — the immersive stage's polarity as a role table (themes plan 5.4, T2b) ─────────────
//
// What was `Design.StageArm` + `Design.StageInk`: the one place in the app that knows which POLARITY the immersive stage is
// painted in, as a PURE value over `ThemeKind`.
//
// The stage used to be single-theme (always art-dark, on the argument that "the room is lit by the playing track"). It now flips
// with the theme, and the whole of that decision lives HERE so the renderers stay theme-blind: a face reads `Ink.Ink`,
// `Ink.ScrimRest`, `Ink.Veil` and never asks which theme it is in. The dark arm of Classic's table delegates to the on-media
// ladder VERBATIM (a test pins it), which is what makes "dark theme is byte-identical to what shipped" an executable claim and
// keeps the on-media ladder theme-INVARIANT: it must NOT become theme-aware, because on a cover thumbnail white-on-scrim is right in
// both themes. The stage is the one surface whose "media" is a full-bleed blurred backdrop it also owns the scrim of, which is
// exactly why it, and only it, gets a polarity.
//
// A style gives every member for BOTH kinds (`ColorRole.By(kind => …)`); `For(kind)` answers a pure per-kind view (the value tests
// drive both arms without mutating the active theme) and `StageInk` is the live view (the kind read at the point of consumption,
// because a ColorF frozen into a component's constructor does not follow a live theme flip: props freeze at mount).

using FluentGpu.Dsl;
using FluentGpu.Animation;
using FluentGpu.Foundation;

namespace Wavee.Theming.Roles;

/// <summary>The stage-arm roles of a style. Every style defines all of them (themes plan S2). Equality is by value.</summary>
/// <param name="IsDark">The stage's POLARITY for a theme kind, for the handful of decisions that are a polarity rather than a colour (a visualizer palette's direction).
/// Everything else takes a rung instead of asking this.</param>
/// <param name="Veil">The colour every scrim alpha is mixed FROM, and the opaque floor under the backdrop.</param>
/// <param name="Floor">The opaque plate beneath the (possibly missing, still-decoding) cover. It IS the veil.</param>
/// <param name="Ink">The loudest thing on the surface: the title, a sung glyph, the active line.</param>
/// <param name="InkSecondary">Secondary stage ink.</param>
/// <param name="InkTertiary">Tertiary stage ink.</param>
/// <param name="GlassRest">The stage's interaction ramp for a control carrying NO resting plate: nothing at rest.</param>
/// <param name="GlassHover">Glass hover.</param>
/// <param name="GlassPressed">Glass pressed.</param>
/// <param name="GlassPlate">The resting ground of the one control that must be found (the way out).</param>
/// <param name="GlassPlateHover">The ink plate's hover.</param>
/// <param name="GlassPlatePressed">The ink plate's press.</param>
/// <param name="ScrimRest">A small plate floated over ARTWORK (the secondary-line toggle).</param>
/// <param name="ScrimHover">The scrim plate's hover.</param>
/// <param name="ScrimPressed">The scrim plate's press.</param>
/// <param name="Stroke">The hairline that rings a plate. It inverts WITH the ink: a white hairline on a light plate is an absent one.</param>
/// <param name="ButtonFill">The stage's play/pause, the only plate on the surface that is not a hover state.</param>
/// <param name="ButtonFillHover">The play disc's hover.</param>
/// <param name="ButtonFillPressed">The play disc's press.</param>
/// <param name="ButtonInk">The glyph ON that disc: the ground it stands on, so the two can never collide.</param>
/// <param name="SkeletonBar">The lyrics loading shimmer (it used to paint a THEME fill straight onto the stage).</param>
/// <param name="Plate">A quiet plate INSIDE a card or on the stage (a settings row, a gallery tile, the artist card): ink at a low alpha.</param>
/// <param name="PlateHover">The quiet plate's hover.</param>
/// <param name="Card">The acrylic under the stage's floating cards (the transport, the now-playing card, the visualizer gallery).</param>
/// <param name="AccentFrom">The stage's art-derived accent from the CHROME accent of a cover: the lifted chrome accent as is where the stage is dark, and the same hue
/// re-solved as INK against the brightest ground this stage can produce where it is light (a lifted accent on a pale veil is a highlighter).</param>
/// <param name="SelectedTileWash">The wash of a selected gallery tile (stronger where the stage is dark).</param>
/// <param name="ScrimBaseA">The stage's plateau scrim alpha: the ONE number both the accent solve and the stage layout read.</param>
public sealed record StageArmTable(
    Func<ThemeKind, bool> IsDark,
    ColorRole Veil, ColorRole Floor,
    ColorRole Ink, ColorRole InkSecondary, ColorRole InkTertiary,
    ColorRole GlassRest, ColorRole GlassHover, ColorRole GlassPressed,
    ColorRole GlassPlate, ColorRole GlassPlateHover, ColorRole GlassPlatePressed,
    ColorRole ScrimRest, ColorRole ScrimHover, ColorRole ScrimPressed, ColorRole Stroke,
    ColorRole ButtonFill, ColorRole ButtonFillHover, ColorRole ButtonFillPressed, ColorRole ButtonInk,
    ColorRole SkeletonBar, ColorRole Plate, ColorRole PlateHover,
    Func<ThemeKind, AcrylicSpec> Card,
    Func<ThemeKind, ColorF, ColorF> AccentFrom,
    ModePair<float> SelectedTileWash,
    float ScrimBaseA)
{
    /// <summary>The arm for <paramref name="kind"/>: a pure entry point (no global read), so value tests drive both arms.</summary>
    public StageArmView For(ThemeKind kind) => new(this, kind);
}

/// <summary>One arm of a <see cref="StageArmTable"/>: the table at a theme kind. A readonly view over the roles, so it allocates nothing.</summary>
public readonly struct StageArmView
{
    private readonly StageArmTable _table;
    private readonly ThemeKind _kind;

    internal StageArmView(StageArmTable table, ThemeKind kind)
    {
        _table = table;
        _kind = kind;
    }

    /// <summary>The kind this arm answers for.</summary>
    public ThemeKind Kind => _kind;

    /// <inheritdoc cref="StageArmTable.IsDark"/>
    public bool Dark => _table.IsDark(_kind);

    /// <inheritdoc cref="StageArmTable.Veil"/>
    public ColorF Veil => _table.Veil.Resolve(_kind);
    /// <inheritdoc cref="StageArmTable.Floor"/>
    public ColorF Floor => _table.Floor.Resolve(_kind);
    /// <inheritdoc cref="StageArmTable.Ink"/>
    public ColorF Ink => _table.Ink.Resolve(_kind);
    /// <inheritdoc cref="StageArmTable.InkSecondary"/>
    public ColorF InkSecondary => _table.InkSecondary.Resolve(_kind);
    /// <inheritdoc cref="StageArmTable.InkTertiary"/>
    public ColorF InkTertiary => _table.InkTertiary.Resolve(_kind);
    /// <inheritdoc cref="StageArmTable.GlassRest"/>
    public ColorF GlassRest => _table.GlassRest.Resolve(_kind);
    /// <inheritdoc cref="StageArmTable.GlassHover"/>
    public ColorF GlassHover => _table.GlassHover.Resolve(_kind);
    /// <inheritdoc cref="StageArmTable.GlassPressed"/>
    public ColorF GlassPressed => _table.GlassPressed.Resolve(_kind);
    /// <inheritdoc cref="StageArmTable.GlassPlate"/>
    public ColorF GlassPlate => _table.GlassPlate.Resolve(_kind);
    /// <inheritdoc cref="StageArmTable.GlassPlateHover"/>
    public ColorF GlassPlateHover => _table.GlassPlateHover.Resolve(_kind);
    /// <inheritdoc cref="StageArmTable.GlassPlatePressed"/>
    public ColorF GlassPlatePressed => _table.GlassPlatePressed.Resolve(_kind);
    /// <inheritdoc cref="StageArmTable.ScrimRest"/>
    public ColorF ScrimRest => _table.ScrimRest.Resolve(_kind);
    /// <inheritdoc cref="StageArmTable.ScrimHover"/>
    public ColorF ScrimHover => _table.ScrimHover.Resolve(_kind);
    /// <inheritdoc cref="StageArmTable.ScrimPressed"/>
    public ColorF ScrimPressed => _table.ScrimPressed.Resolve(_kind);
    /// <inheritdoc cref="StageArmTable.Stroke"/>
    public ColorF Stroke => _table.Stroke.Resolve(_kind);
    /// <inheritdoc cref="StageArmTable.ButtonFill"/>
    public ColorF ButtonFill => _table.ButtonFill.Resolve(_kind);
    /// <inheritdoc cref="StageArmTable.ButtonFillHover"/>
    public ColorF ButtonFillHover => _table.ButtonFillHover.Resolve(_kind);
    /// <inheritdoc cref="StageArmTable.ButtonFillPressed"/>
    public ColorF ButtonFillPressed => _table.ButtonFillPressed.Resolve(_kind);
    /// <inheritdoc cref="StageArmTable.ButtonInk"/>
    public ColorF ButtonInk => _table.ButtonInk.Resolve(_kind);
    /// <inheritdoc cref="StageArmTable.SkeletonBar"/>
    public ColorF SkeletonBar => _table.SkeletonBar.Resolve(_kind);
    /// <inheritdoc cref="StageArmTable.Plate"/>
    public ColorF Plate => _table.Plate.Resolve(_kind);
    /// <inheritdoc cref="StageArmTable.PlateHover"/>
    public ColorF PlateHover => _table.PlateHover.Resolve(_kind);
    /// <inheritdoc cref="StageArmTable.Card"/>
    public AcrylicSpec Card => _table.Card(_kind);
    /// <inheritdoc cref="StageArmTable.SelectedTileWash"/>
    public float SelectedTileWash => _table.SelectedTileWash.For(_kind);
    /// <inheritdoc cref="StageArmTable.ScrimBaseA"/>
    public float ScrimBaseA => _table.ScrimBaseA;

    /// <inheritdoc cref="StageArmTable.AccentFrom"/>
    public ColorF AccentFrom(ColorF chrome) => _table.AccentFrom(_kind, chrome);
}

/// <summary>The LIVE facade over the active style's <see cref="StageArmTable"/>. <see cref="Arm"/> is the pure entry point;
/// everything else re-reads the active theme AT THE POINT OF CONSUMPTION, which is the whole contract: a <c>ColorF</c> frozen into a
/// component's constructor does not follow a live theme flip, because component props freeze at mount. The shell's UI files
/// alias it (<c>using Ink = Wavee.Theming.Roles.StageInk;</c>) so a face reads <c>Ink.Plate</c>.</summary>
public static class StageInk
{
    /// <summary>The arm for <paramref name="kind"/> of the active style (a pure entry point).</summary>
    public static StageArmView Arm(ThemeKind kind) => R.StageArm.For(kind);

    /// <summary>The arm for the ACTIVE theme kind. This, and only this, is the stage's theme branch.</summary>
    private static StageArmView Live => R.StageArm.For(Tok.Theme);

    /// <summary>Which arm is live, for the handful of decisions that are a POLARITY rather than a colour (the lyrics bloom's
    /// direction, a visualizer palette's lightness). Everything else takes a rung instead of asking this.</summary>
    public static bool IsDark => Live.Dark;

    public static ColorF Veil => Live.Veil;
    public static ColorF Floor => Live.Floor;
    public static ColorF Ink => Live.Ink;
    public static ColorF InkSecondary => Live.InkSecondary;
    public static ColorF InkTertiary => Live.InkTertiary;
    public static ColorF GlassRest => Live.GlassRest;
    public static ColorF GlassHover => Live.GlassHover;
    public static ColorF GlassPressed => Live.GlassPressed;
    public static ColorF GlassPlate => Live.GlassPlate;
    public static ColorF GlassPlateHover => Live.GlassPlateHover;
    public static ColorF GlassPlatePressed => Live.GlassPlatePressed;
    public static ColorF ScrimRest => Live.ScrimRest;
    public static ColorF ScrimHover => Live.ScrimHover;
    public static ColorF ScrimPressed => Live.ScrimPressed;
    public static ColorF Stroke => Live.Stroke;
    public static ColorF ButtonFill => Live.ButtonFill;
    public static ColorF ButtonFillHover => Live.ButtonFillHover;
    public static ColorF ButtonFillPressed => Live.ButtonFillPressed;
    public static ColorF ButtonInk => Live.ButtonInk;
    public static ColorF SkeletonBar => Live.SkeletonBar;
    public static AcrylicSpec Card => Live.Card;
    public static ColorF Plate => Live.Plate;
    public static ColorF PlateHover => Live.PlateHover;

    /// <summary>The wash of a selected gallery tile on the live arm.</summary>
    public static float SelectedTileWash => Live.SelectedTileWash;

    /// <summary>The stage's accent for a cover url. The cover LOOKUP lives here rather than on the arm so the arm stays a pure value over the
    /// roles, which is what lets value tests drive both arms with no cover plane, no theme and no window. With the structure's "Now playing
    /// colours" off (<see cref="ArtworkColorPolicy.NowPlayingColors"/>, CT9 U3) it is the system accent whatever the cover: a neutral stage.
    /// The policy read is tracked, so a stage effect that derives its accent here re-runs when the option flips.</summary>
    public static ColorF Accent(ReadOnlySpan<char> coverUrl)
        => NowPlayingColors && Design.ChromeSchemeFor(coverUrl) is { } scheme
            ? Live.AccentFrom(Design.Palette.ChromeAccent(scheme))
            : Tok.AccentDefault;

    /// <summary>The backdrop's stand-in while the cover is missing or still decoding, in the STAGE's polarity rather than the page's. The cover
    /// TINT survives (it is what stops the slot reading as a hole); only the neutral it is blended toward follows the stage. With "Now playing
    /// colours" off the stand-in is the neutral tile.</summary>
    public static ColorF ArtStandIn(ReadOnlySpan<char> url) => Design.PlaceholderFor(NowPlayingColors ? url : default, light: !IsDark);

    /// <summary>The now-playing surfaces take the playing cover's colour (the active structure's <see cref="ArtworkColorPolicy.NowPlayingColors"/>). Tracked.</summary>
    public static bool NowPlayingColors => ThemeHost.Policies.Value.Artwork.NowPlayingColors;
}

/// <summary>The caption's VEIL, the ground that guarantees the caption's fixed on-media inks (the lyric over the stage: previous, live and
/// next line). The inks never change; the veil is solved for them.
/// <para>WHERE: inside the caption's own band, never over the picture. It HUGS the text block: a box at the veil's strength exactly under the
/// lines' laid-out rects, grown past them by <see cref="ReachX"/> on the sides and <see cref="ReachY"/> above and below, the whole growth a fade
/// (a smoothstep ramp of gradient stops above and below, the engine's edge fade to the sides: no blur pass) so the ground dissolves outward
/// from the text.
/// Above and below it reaches <c>Stage.Layout.Pad</c>, the gutter every structure keeps between the caption's band and the face's safe rect above
/// it (and the caption's own gap to the transport below), so the cover and the face are never touched; to the sides the stage's veil pad.</para>
/// <para>HOW STRONG: the smallest alpha at which the weakest caption ink (<see cref="StageInk.InkSecondary"/>) holds the text floor
/// (<see cref="ColorScience.TextContrast"/>) over what the stage's BACKDROP actually paints under the band: the floor (or, where the structure
/// bakes the cover in, the cover's own pixels under the band), each of the Field's blobs at the alpha it is drawn with (its own alpha times the
/// Field's highest opacity), all under the mode's scrim. Over moving video (an unknown ground) white and black. Never below the stage's own
/// veil alpha (<c>Stage.Tone.CaptionVeilA</c>).</para></summary>
public static class StageCaptionVeil
{
    /// <summary>How far the veil reaches past the text block to each side (all of it the fade): the stage's caption veil pad.</summary>
    public static float ReachX => Stage.Layout.CaptionVeilPadX;

    /// <summary>How far the veil reaches above and below the text block (all of it the fade): the stage's gutter, which the band keeps clear.</summary>
    public static float ReachY => Stage.Layout.Pad;

    /// <summary>The veil box's growth past the text block (negative margins on a layer the size of the block).</summary>
    public static Edges4 Growth => new(-ReachX, -ReachY, -ReachX, -ReachY);

    /// <summary>The veil (<see cref="StageCaptionVeil"/>), the first layer of the stack that hugs the caption's lines: grown past them by its reach,
    /// full strength over the lines, faded to nothing over the growth. Above and below, a smoothstep ramp of stops over <see cref="ReachY"/>
    /// (a static gradient, laid out by the column it sits in, so it follows the lines' height); to the sides the engine's edge fade over
    /// <see cref="ReachX"/>. Its bounds ease when a hand-off changes the lines (<see cref="Move"/>).</summary>
    public static Element Ground(float alpha)
    {
        ColorF veil = StageInk.Veil;
        return new BoxEl
        {
            Key = "cap:veil", HitTestVisible = false, Layout = Move, Margin = Growth, Direction = 1,
            EdgeFade = new EdgeFadeSpec(EdgeMask.Horizontal, ReachX, 0f, ReachX, 0f),
            Children =
            [
                new BoxEl { Height = ReachY, HitTestVisible = false, Gradient = Ui.GradientDown(Ramp(veil, alpha, rising: true)) },
                new BoxEl { Grow = 1f, HitTestVisible = false, Fill = veil with { A = alpha } },
                new BoxEl { Height = ReachY, HitTestVisible = false, Gradient = Ui.GradientDown(Ramp(veil, alpha, rising: false)) },
            ],
        };
    }

    /// <summary>A smoothstep from clear to <paramref name="alpha"/> (or back) as five stops.</summary>
    static GradientStop[] Ramp(ColorF veil, float alpha, bool rising)
    {
        var stops = new GradientStop[RampStops];
        for (int i = 0; i < RampStops; i++)
        {
            float t = i / (float)(RampStops - 1);
            float s = t * t * (3f - 2f * t);
            stops[i] = new GradientStop(t, veil with { A = alpha * (rising ? s : 1f - s) });
        }
        return stops;
    }

    const int RampStops = 5;

    /// <summary>The veil's move when the lines it hugs change (a hand-off, a wrap): its bounds ease with the hand-off's enter, so the ground
    /// grows or shrinks under the lines instead of snapping.</summary>
    public static LayoutTransition Move => new(TransitionChannels.Bounds, MotionTok.StandardEnter.ToDynamics(), SizeMode.Relayout);

    /// <summary>The veil's alpha for the stage the caption is in, over the caption's band (<paramref name="band"/>, stage DIP: the block at its tallest).
    /// <paramref name="artBaked"/>: the structure's backdrop bakes the cover under its Field (Classic, WinUI): its pixels under the band are
    /// read from the cover's own luminance map (<c>Palette.Tone</c>, the measurement the photo scrims use; until it lands, the cover's graded base
    /// colours). Reads the base palette, the playing row, the look and the map, so the template re-renders when any changes. No stage (a preview):
    /// the stage's own veil alpha.</summary>
    public static float For(Stage.StageCtx? stage, bool artBaked, RectF band)
    {
        if (stage is null) return Stage.Tone.CaptionVeilA;
        var look = stage.Look.Value;
        if (look.Mode == Stage.Mode.Video) return Alpha(default, default, video: true, artBaked: false, scrimA: 0f);
        var L = stage.Layout.Value;
        var track = stage.RowValue();
        string? url = artBaked && track.IsValid ? Controls.ArtUrl(track.ImageId) : null;
        float? artGrey = null;
        if (url is { Length: > 0 } && Palette.Tone(url) is { } map)
            artGrey = MediaScrim.GreyAt(map, MediaScrim.CoverUv(map.Aspect, L.W, L.H, band.X, band.Y, band.X + band.W, band.Y + band.H), MediaScrim.Percentile);
        Scheme? scheme = url is { Length: > 0 } && artGrey is null ? Design.SchemeFor(url) : null;
        return Alpha(stage.BasePalette.Value, scheme, video: false, artBaked, Stage.LayoutRules.ScrimFor(look.Mode, look.Eff, stage.Kind.Value), artGrey);
    }

    /// <summary>The solve (see the type): the grounds the backdrop paints, then <see cref="ColorScience.ScrimAlpha"/>.</summary>
    public static float Alpha(in Visualizer.Palette palette, Scheme? scheme, bool video, bool artBaked, float scrimA, float? artGrey = null)
    {
        Span<ColorF> grounds = stackalloc ColorF[GroundsMax];
        int n = 0;
        if (video) { grounds[n++] = ColorF.FromRgba(0xFF, 0xFF, 0xFF); grounds[n++] = ColorF.FromRgba(0, 0, 0); }
        else
        {
            // the bottoms the Field's blobs lie on: the floor, or the baked cover's graded base colours (its blurred average)
            Span<ColorF> bottoms = stackalloc ColorF[2];
            int b = 0;
            if (artBaked && artGrey is { } g) bottoms[b++] = new ColorF(g, g, g, 1f);
            else if (artBaked && scheme is { IsEmpty: false } sc)
            {
                bottoms[b++] = ColorScience.ToColor(sc.BackgroundBase) with { A = 1f };
                bottoms[b++] = ColorScience.ToColor(sc.BackgroundTintedBase) with { A = 1f };
            }
            else bottoms[b++] = StageInk.Floor with { A = 1f };
            float fieldA = MathF.Max(Stage.Tone.BaseFieldA + Stage.Tone.BaseFieldBreathA, Stage.Tone.BaseFieldVisualizerA);
            var scrim = StageInk.Veil with { A = scrimA };
            for (int k = 0; k < b; k++)
            {
                grounds[n++] = ColorContrast.Over(scrim, bottoms[k]);
                for (int i = 0; i < Visualizer.Field.Blobs; i++)
                {
                    var blob = Visualizer.Field.BlobTint(in palette, i);
                    grounds[n++] = ColorContrast.Over(scrim, ColorContrast.Over(blob with { A = Visualizer.Field.BlobAlpha(i) * fieldA }, bottoms[k]));
                }
            }
        }
        return ColorScience.ScrimAlpha(StageInk.InkSecondary, StageInk.Veil with { A = 1f }, grounds[..n], ColorScience.TextContrast, Stage.Tone.CaptionVeilA);
    }

    /// <summary>Room for the grounds: two bottoms, each bare and under every blob.</summary>
    const int GroundsMax = 2 * (1 + 4);
}
