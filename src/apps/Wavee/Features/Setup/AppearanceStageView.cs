using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>Appearance page 4's stage — a BoxEl-only DIAGRAM of the shell behind the plate, not a screenshot of it (the
/// un-dimmed shell itself, per work package I's <c>SetupCover.Live</c>, is the real preview). Built entirely from
/// <see cref="AppearanceStageModel.Result"/>, so a control's write only ever has to update the model's inputs — the
/// view has no settings/context reads of its own. Palette and theme need no plumbing here at all: every fill below
/// resolves through <c>Tok</c>, which <c>WaveeTheme.ApplyPalette</c>/<c>ApplyThemeMode</c> already re-themes live the
/// moment a row writes.</summary>
static class AppearanceStageView
{
    const float Width = 296f;
    const float Height = 312f;
    const float NavRailWidth = 40f;
    const float ContentWidth = 256f;
    const float ContentHeight = 248f;
    const float HeroBandHeight = 64f;
    const float RailIdentityWidth = 64f;
    const float HeroArtEdge = 48f;
    const float RailArtEdge = 56f;

    /// <summary>The track-list area's height at 1x inside the miniature's content pane (256×248, padded 8) for each
    /// layout arm — the rail arm gives the whole padded height to the list beside its metadata rail; the hero arm gives
    /// up some of it to the artwork+identity block stacked above the list. Kept beside the geometry they describe
    /// (rather than in <see cref="AppearanceStageModel"/>, which knows nothing about pixels) so a future edit to either
    /// arm's shape updates the number that actually matches it.</summary>
    public const float RailListHeight = 232f;
    public const float HeroListHeight = 176f;

    public static Element Build(in AppearanceStageModel.Result r, Image? cover)
    {
        var ink = WaveePicker.Ink.For(true);

        Element navRail = new BoxEl
        {
            Width = NavRailWidth, Height = ContentHeight, Shrink = 0f, Direction = 1, Gap = Spacing.XS,
            AlignItems = FlexAlign.Center, Padding = new Edges4(0f, Spacing.S, 0f, Spacing.S),
            Fill = Tok.FillSolidBaseAlt,
            Children =
            [
                Icon(Icons.Home, 12f, Tok.AccentTextPrimary),
                Icon(Icons.Search, 12f, Tok.TextTertiary),
                Icon(Icons.MusicNote, 12f, Tok.TextTertiary),
                new BoxEl { Grow = 1f, Basis = 0f, MinHeight = 0f },
                Icon(Icons.Settings, 12f, Tok.TextTertiary),
            ],
        };

        Element identity = r.HeroLayout ? HeroIdentity(in r, cover, ink) : RailIdentity(in r, cover, ink);

        Element inner = new BoxEl
        {
            Width = ContentWidth, Height = ContentHeight, Direction = 1, Gap = Spacing.S,
            Padding = Edges4.All(Spacing.S), MinHeight = 0f,
            Children = [identity],
        };

        Element wash = new BoxEl
        {
            Width = ContentWidth, Height = r.TintHeroOnly ? HeroBandHeight : ContentHeight,
            Fill = Tok.AccentDefault with { A = r.TintAlpha },
        };

        Element content = new BoxEl
        {
            Width = ContentWidth, Height = ContentHeight, Shrink = 0f, ZStack = true, ClipToBounds = true,
            Children = [wash, inner],
        };

        Element body = new BoxEl
        {
            Height = ContentHeight, Shrink = 0f, Direction = 0, ClipToBounds = true,
            Children = [navRail, content],
        };

        return new BoxEl
        {
            Width = Width, Height = Height, Shrink = 0f, Direction = 1, ClipToBounds = true,
            Corners = Radii.CardAll, Fill = r.MicaAlt ? Tok.FillSolidBaseAlt : Tok.FillSolidBase,
            BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault, Shadow = Elevation.Card,
            Children = [SetupMiniChrome.TitleBar(ink), body, SetupMiniChrome.PlayerBar(r.LyricsMotion, ink)],
        };
    }

    // The rail arm: a narrow metadata rail (art + two identity bars + a pill) beside a column of full-width track
    // rows — the "Automatic" page-layout grammar (side rail beside tracks) drawn at the miniature's own scale.
    static Element RailIdentity(in AppearanceStageModel.Result r, Image? cover, WaveePicker.Ink ink) => new BoxEl
    {
        Direction = 0, Gap = Spacing.S, Grow = 1f, Basis = 0f, MinWidth = 0f, MinHeight = 0f,
        Children =
        [
            new BoxEl
            {
                Width = RailIdentityWidth, Shrink = 0f, Direction = 1, Gap = Spacing.XS, Justify = FlexJustify.Center,
                Children =
                [
                    Art(cover, RailArtEdge, ink),
                    WaveePicker.Bar(1f, ink, strong: true),
                    WaveePicker.Bar(0.6f, ink),
                    new BoxEl { Width = 24f, Height = 8f, Shrink = 0f, Corners = Radii.ControlAll, Fill = ink.Block },
                ],
            },
            Rows(in r, ink),
        ],
    };

    // The hero arm: adaptive artwork + compact identity ABOVE the track rows — the "Hero" page-layout grammar.
    static Element HeroIdentity(in AppearanceStageModel.Result r, Image? cover, WaveePicker.Ink ink) => new BoxEl
    {
        Direction = 1, Gap = Spacing.S, Grow = 1f, Basis = 0f, MinWidth = 0f, MinHeight = 0f,
        Children =
        [
            new BoxEl
            {
                Height = HeroArtEdge, Shrink = 0f, Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center,
                Children =
                [
                    Art(cover, HeroArtEdge, ink),
                    new BoxEl
                    {
                        Direction = 1, Gap = Spacing.XXS, Grow = 1f, Basis = 0f, MinWidth = 0f,
                        Children = [WaveePicker.Bar(1f, ink, strong: true), WaveePicker.Bar(0.6f, ink)],
                    },
                ],
            },
            Rows(in r, ink),
        ],
    };

    // The page-level artwork (rail thumb / hero art) is independent of the TRACK-row art edge (r.ArtEdge covers only
    // the per-row thumbnail column, gated by "Always hide track artwork" + track style) — a cover falls back to a
    // plain ink block only when there is genuinely no image to show, never when track-row art is hidden.
    static Element Art(Image? cover, float edge, WaveePicker.Ink ink) => cover is not null
        ? Surfaces.Artwork(cover, 6, edge, edge, Radii.Control, decodePx: 96)
        : new BoxEl { Width = edge, Height = edge, Shrink = 0f, Corners = Radii.ControlAll, Fill = ink.Block };

    static Element Rows(in AppearanceStageModel.Result r, WaveePicker.Ink ink)
    {
        var rows = new Element[r.RowCount];
        for (int i = 0; i < r.RowCount; i++)
            rows[i] = i == 0 ? FirstRow(in r, ink)
                : r.Classic ? WaveePicker.ClassicRow(ink, r.RowHeight)
                : WaveePicker.ModernRow(ink, r.RowHeight, r.ArtEdge, r.TwoLineRows);
        return new BoxEl
        {
            Direction = 1, Gap = Spacing.XXS, Grow = 1f, Basis = 0f, MinWidth = 0f, MinHeight = 0f,
            Children = rows,
        };
    }

    // The one row that visibly demonstrates the Marquee flag: overflow-and-clip when it's on (the real Modern row's
    // own behaviour), a short bar + an ellipsis dot-run when it's off. Classic's three-lane grammar has no single
    // "title" bar to overflow, so it keeps its ordinary row untouched.
    static Element FirstRow(in AppearanceStageModel.Result r, WaveePicker.Ink ink)
    {
        if (r.Classic) return WaveePicker.ClassicRow(ink, r.RowHeight);

        Element titleLane = r.Marquee
            ? new BoxEl
            {
                Grow = 1f, Basis = 0f, MinWidth = 0f, ClipToBounds = true,
                Children = [new BoxEl { Width = r.RowHeight * 6f, Height = Spacing.XXS, Shrink = 0f, Corners = Radii.PillAll, Fill = ink.Block }],
            }
            : new BoxEl
            {
                Direction = 0, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = Spacing.XXS, AlignItems = FlexAlign.Center,
                Children = [WaveePicker.Bar(1f, ink, strong: true), Dots(ink)],
            };

        Element[] kids = r.ArtEdge > 0f
            ? [new BoxEl { Width = r.ArtEdge, Height = r.ArtEdge, Shrink = 0f, Corners = Radii.ControlAll, Fill = ink.Block }, titleLane]
            : [titleLane];

        return new BoxEl
        {
            Height = r.RowHeight, Direction = 0, Gap = Spacing.XS, AlignItems = FlexAlign.Center,
            Padding = new Edges4(Spacing.XS, 0f, Spacing.XS, 0f), ClipToBounds = true,
            Corners = Radii.ControlAll, Fill = ink.Faint,
            Children = kids,
        };
    }

    static Element Dots(WaveePicker.Ink ink) => new BoxEl
    {
        Direction = 0, Gap = 1.5f, Shrink = 0f, AlignItems = FlexAlign.Center,
        Children = [Dot(ink), Dot(ink), Dot(ink)],
    };

    static Element Dot(WaveePicker.Ink ink) => new BoxEl { Width = 2f, Height = 2f, Shrink = 0f, Corners = Radii.Circle(2f), Fill = ink.Faint };
}
