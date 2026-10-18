// ── Themes/Zune/Structure/Lists/ZuneTrackTable.cs — Zune's track table rows (themes plan 6.3; P5 Z3) ───────────────────────────────
//
// The `TrackListStyle` Zune registers under `StyleKeys.TrackTable`: the bound grid of lanes and the row's skin, and the chrome
// (`ZuneTrackChrome`). Boards: Z-State-Rows (the row's states), Z-State-Rows-Compact, Z-Album (an album's numbered rows: no art, plays and
// time lanes), Z-Liked and Z-Playlist-Owned-Followed (a list's rows with art), the -Small / -Huge / -Huge4K twins.
//
// ONE SKIN. Zune draws one row (ZunePolicies publishes `TrackRowStyle.Modern`, so the core's lane sets are the two-line row's); both slots of
// the record carry it, so the core never draws a skin Zune does not have.
//
// THE ROW. The lanes are the core's (`Track.ColumnSet`: which exist at this width and tier, the tracks they share); the grid draws them:
//   #        the number in the Numeral role (22·300, tertiary), the accent bars on the playing row (keyed "zune.row.now"), the ring while
//            buffering, the play / pause glyph on hover (none on a withheld row);
//   heart    revealed on hover (and on the focused row), filled accent when liked, popping on a like edge;
//   art      the row's cover at the Cover radius (2) where the lane set has a thumb (a list's rows; an album's have none);
//   title    the TrackTitle role (15/20), the accent on the playing row, the marquee on the playing row when the core asks for it, the
//            film glyph after a title with a video;
//   subline  the TrackMeta role (13/18): [EPISODE] · [E] · the artists · the album, accent on the playing row; the Compact form closes it with
//            the time ("Kavinsky · 4:18", Z-State-Rows-Compact);
//   lanes    album, added by, date, plays, tempo and time in the DenseMeta role (13/18), the accent on the playing row; a withheld row says
//            why in the time lane (the long reason in its tooltip);
//   "..."    revealed on hover (and on the focused row); at Compact a 40 target shown on every row.
// THE SKIN (Z-State-Rows): rest bare (no zebra, no border, no corners, no pill); hover the 5 % ink wash; pressed the pressed wash; selected the
// 12 % accent wash; a drop target the 2-DIP accent line on the top edge (keyed "zune.row.drop", its opacity bound to the cue). The playing
// row has no plate. A row whose drawer is open keeps its square corners (it already has no others).
//
// WHAT THE CORE KEEPS. `Track.TableHost` and its slot components decide which rows exist and what they hold: the presentation memo, the like
// edge, the reveal gate and the shimmer, the drawer, selection, the drag source, the keyboard and the context menu; the row height (the
// style's density, `StyleMetrics.TrackRows`: 44 / 52 / 60) and the lanes. The templates run once per slot per shape, never per bind: every
// per-item value is a bound Prop over the slot's presentation, so a recycle rebinds and allocates nothing, and the list key carries no
// tier or column count. `Stamp` stays 1: no theme option changes either template.
//
// Numbers are `Rules\ZuneListRules.cs` (WVTH003).

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Zune;

using BoundRow = global::Wavee.Track.BoundRow;
using CellKey = global::Wavee.Track.CellKey;
using ColumnSet = global::Wavee.Track.ColumnSet;
using Format = global::Wavee.Track.Format;
using RowCellRules = global::Wavee.Track.RowCellRules;
using RowMetrics = global::Wavee.Track.RowMetrics;
using RowPresentation = global::Wavee.Track.RowPresentation;

/// <summary>Zune's track table: one row (bound grid and skin) in both slots, and the chrome.</summary>
internal static class ZuneTrackTable
{
    /// <summary>The key of a row skin's drop line (a test finds Zune's rows by it).</summary>
    public const string DropKey = "zune.row.drop";

    static readonly TrackRowTemplates s_row = new(Grid, Skin);

    /// <summary>The style Zune registers under <c>StyleKeys.TrackTable</c>. Stateless: a table, a drawer and a test share it.</summary>
    public static readonly TrackListStyle Style = new(stamp: 1, modern: s_row, classic: s_row, chrome: ZuneTrackChrome.Templates);

    static Prop<ColorF> RowInk(BoundRow r, Func<ColorF> rest) => Prop.Of(() => ZuneListRules.RowInk(r.P.State.IsNow, R.Ink.AccentDecor, rest()));

    static TextEl Glyph(Prop<string> glyph, float size, Prop<ColorF> color, Prop<bool>? visible = null) => new(glyph)
    {
        FontFamily = Tok.Definition.Fonts.Icon, Size = size, Color = color, Visible = visible ?? true,
    };

    /// <summary>The factual rung (DenseMeta 13/18), built once with its binds.</summary>
    static TextEl Factual(Prop<string> text, Prop<ColorF> color, bool fill = false, Prop<bool>? visible = null)
    {
        var meta = R.Type.DenseMeta("");
        return new TextEl(text)
        {
            Size = meta.Size, LineHeight = meta.LineHeight, Weight = meta.ResolvedWeight, Color = color,
            Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
            Grow = fill ? 1f : 0f, Basis = fill ? 0f : float.NaN,
            Visible = visible ?? true,
        };
    }

    // ══ THE BOUND GRID ═══════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The lanes of <paramref name="set"/>, every per-item value bound to <paramref name="r"/>. Built once per slot per shape.</summary>
    static Element Grid(BoundRow r, in ColumnSet set, TrackSize[] tracks, float rowH, float art)
    {
        float artSize = art > 0f ? art : RowMetrics.ThumbSize;
        var cells = new Element[Track.CellCount(in set)];
        int i = 0;

        if (!set.IndexHidden) cells[i++] = NumberCell(r, set.HoverActions);
        if (set.Heart) cells[i++] = Track.RowCenterCell(HeartReveal(r, set.Quiet), CellKey.Heart);
        if (set.Thumb) cells[i++] = Track.RowCenterCell(Artwork(r, artSize), CellKey.Art);
        cells[i++] = TitleCell(r, in set);
        if (set.Artist) cells[i++] = Track.RowLeftCell(ArtistLinks(r), CellKey.Artist);
        if (set.Album) cells[i++] = Track.RowLeftCell(AlbumLink(r), CellKey.Album);
        if (set.By) cells[i++] = Track.RowLeftCell(Factual(Prop.Of(() => Track.AddedByLabel(r)), RowInk(r, static () => Tok.TextSecondary),
                                                        fill: true, visible: Prop.Of(() => r.P.AddedBy.Slot > 0)), CellKey.By);
        if (set.Date) cells[i++] = Track.RowLeftCell(Factual(Prop.Of(() => Format.DateAddedLabel(r.P.AddedAt, Track.NowUnix)),
                                                          RowInk(r, static () => Tok.TextSecondary), fill: true), CellKey.Date);
        if (set.Plays) cells[i++] = Track.RowEndCell(PlaysCell(r), CellKey.Plays);
        if (RowMetrics.ShowTempo(in set)) cells[i++] = Track.RowEndCell(TempoCell(r), CellKey.Tempo);
        if (RowMetrics.ShowHeartTrailing(in set)) cells[i++] = Track.RowCenterCell(HeartReveal(r, set.Quiet), CellKey.HeartTrailing);
        if (RowMetrics.ShowDuration(in set)) cells[i++] = Track.RowEndCell(DurationCell(r, set.HoverActions, set.Quiet), CellKey.Duration);
        // The Compact row's "..." is a 40 target shown on every row (Z-State-Rows-Compact): there is no hover to reveal it.
        if (set.Actions) cells[i++] = set.Compact
            ? Track.MoreCell(true, classic: true, CellKey.More, () => r.Focused, box: ZuneListRules.CompactTarget, atRest: true)
            : Track.MoreCell(true, classic: true, CellKey.More, () => r.Focused);
        if (set.Expand) cells[i++] = ExpandCell(r);

        // Zune's rows are full-width plates (no inset): the grid pays the whole gutter.
        float padX = RowMetrics.PadXFor(set.Tier);
        var grid = new GridEl
        {
            Columns = tracks, ColGap = RowMetrics.ColGapFor(set.Tier), RowHeight = rowH, Grow = 1f,
            Padding = new Edges4(padX, 0f, padX, 0f),
            Children = cells,
        };
        // Z-State-Rows: "unavailable · 45% ink": the whole row steps back (not just its title column), whether the song is withheld or the
        // session is down and the audio is not on this PC. The grid has no opacity of its own, so one box carries the bound dim.
        return new BoxEl
        {
            Direction = 0, Grow = 1f, AlignItems = FlexAlign.Stretch, MinWidth = 0f,
            Opacity = Prop.Of(() =>
            {
                var p = r.P;
                return Track.Dimmed(in p, Track.NowUnix) ? Track.RowNotYetOutOpacity : 1f;
            }),
            Children = [grid],
        };
    }

    // ── the # cell ───────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Two shapes under one keyed ZStack cell, swapped by <c>Flow.Show</c> on the rare withheld flip: the reveal shape (the rest layer
    /// fades out, the transport fades in on row hover) and the rest-only shape. A ZStack hands its child the whole lane, so every layer
    /// centres in the lane the header's "#" sits over.</summary>
    static Element NumberCell(BoundRow r, bool leading)
    {
        Element reveal = new BoxEl
        {
            ZStack = true, MinWidth = 0f, ClipToBounds = true, Grow = 1f, AlignSelf = FlexAlign.Stretch,
            Children =
            [
                RestLayer(r, hoverFade: true, leading),
                // The transport mounts on the row's first hover / focus (Track.Affordance): it draws nothing before.
                Track.Affordance(r, () => new BoxEl
                {
                    Direction = 0, Grow = 1f, AlignItems = FlexAlign.Stretch, Justify = FlexJustify.Center,
                    Opacity = 0f, HoverOpacity = 1f,
                    Children =
                    [
                        new BoxEl
                        {
                            ZStack = true, Grow = 1f, Basis = 0f, MinWidth = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                            OnClick = r.Play, Cursor = CursorId.Hand, Role = AutomationRole.Button, BlocksDragArm = true,
                            Children =
                            [
                                Flow.Show(() => r.P.State.IsBuffering, Track.Spinner()),
                                new BoxEl
                                {
                                    Width = Track.RowTransportBox, Height = Track.RowTransportBox,
                                    AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                                    PressScale = R.Motion.ScaleEmphatic.Press,
                                    Visible = Prop.Of(() => !r.P.State.IsBuffering),
                                    Children =
                                    [
                                        Glyph(Prop.Of(() => r.P.State is { IsNow: true, IsPlaying: true } ? Icons.Pause : Icons.Play),
                                              ZuneListRules.TransportGlyph, RowInk(r, static () => Tok.TextPrimary)),
                                    ],
                                },
                            ],
                        },
                    ],
                }),
            ],
        };
        Element restOnly = new BoxEl
        {
            ZStack = true, MinWidth = 0f, ClipToBounds = true, Grow = 1f, AlignSelf = FlexAlign.Stretch,
            Children = [RestLayer(r, hoverFade: false, leading)],
        };
        return new BoxEl
        {
            Key = CellKey.Num, ZStack = true, MinWidth = 0f, ClipToBounds = true,
            Children =
            [
                Flow.Show(() =>
                {
                    var p = r.P;
                    var st = p.State;
                    return RowCellRules.Reveal(in st, Track.Withheld(in p, Track.NowUnix));
                }, reveal, restOnly),
            ],
        };
    }

    static RowCellRules.Rest RestOf(BoundRow r)
    {
        var st = r.P.State;
        return RowCellRules.RestOf(in st, classic: false);
    }

    /// <summary>The rest layer: the number (with the chart mark) as a <c>Visible</c> flip (Zune shows the number for the album's top track too:
    /// no star), the accent bars and the buffering ring as real mounts.</summary>
    static Element RestLayer(BoundRow r, bool hoverFade, bool leading)
    {
        var numeral = R.Type.Numeral("");
        Element number = new BoxEl
        {
            Direction = 0, Gap = ZuneListRules.NumberGap, AlignItems = FlexAlign.Center,
            Visible = Prop.Of(() => RestOf(r) is RowCellRules.Rest.Number or RowCellRules.Rest.Star),
            Children =
            [
                new TextEl(Prop.Of(() => ZuneRowCells.RowNumberLabel(r.P.Display + 1)))
                {
                    FontFamily = numeral.FontFamily, Size = numeral.Size, LineHeight = numeral.LineHeight, Weight = numeral.ResolvedWeight,
                    Color = Tok.TextTertiary, MaxLines = 1, Wrap = TextWrap.NoWrap,
                },
                new TextEl(Prop.Of(() => ChartMarkText(RowCellRules.ChartMarkOf(r.P.Chart))))
                {
                    Size = ZuneListRules.ChartMarkSize, LineHeight = ZuneListRules.ChartMarkLine, Weight = 700,
                    Color = Prop.Of(() => RowCellRules.ChartMarkOf(r.P.Chart) == RowCellRules.ChartMark.Down ? Tok.SystemFillCritical : Tok.SystemFillSuccess),
                    Visible = Prop.Of(() => RowCellRules.ChartMarkOf(r.P.Chart) != RowCellRules.ChartMark.None),
                },
            ],
        };
        return new BoxEl
        {
            // The boards set an album's number at the lane's start (Z-Album: 4 in), not centred.
            Grow = 1f, AlignItems = FlexAlign.Center, Justify = leading ? FlexJustify.Start : FlexJustify.Center,
            Padding = leading ? new Edges4(ZuneListRules.NumberInset, 0f, 0f, 0f) : default,
            HoverOpacity = hoverFade ? 0f : float.NaN,
            Children =
            [
                number,
                Flow.Show(() => RestOf(r) == RowCellRules.Rest.Equalizer, new BoxEl
                {
                    Key = ZuneRowCells.NowKey, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Children = [Controls.Equalizer(r.Playing, ZuneRowCells.Accent, Track.RowEqualizerHeight, r.Hovered)],
                }),
                Flow.Show(() => RestOf(r) == RowCellRules.Rest.Spinner, Track.Spinner()),
            ],
        };
    }

    static string ChartMarkText(RowCellRules.ChartMark mark) => mark switch
    {
        RowCellRules.ChartMark.Up => "▲",
        RowCellRules.ChartMark.Down => "▼",
        RowCellRules.ChartMark.New => Loc.Get(Track.LocChartNew),
        _ => "",
    };

    // ── heart · art ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The heart's row reveal: a liked row keeps its filled heart at rest, an unliked one shows it on row hover; a quiet list (Liked
    /// Songs: every row is liked) rests with none. The focused row shows it to the keyboard.</summary>
    static Element HeartReveal(BoundRow r, bool quiet) => new BoxEl
    {
        Grow = 1f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Opacity = quiet ? Prop.Of(() => r.Focused ? 1f : 0f) : Prop.Of(() => r.P.State.Saved || r.Focused ? 1f : 0f), HoverOpacity = 1f,
        Children = [Heart(r)],
    };

    /// <summary>Three glyphs under one target: the resting filled heart, the popping one (a real mount on the like EDGE, so the spring
    /// replays once) and the outline.</summary>
    static Element Heart(BoundRow r) => new BoxEl
    {
        Width = RowMetrics.HeartCol, Height = RowMetrics.HeartCol,
        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Corners = Radii.Circle(RowMetrics.HeartCol),
        Cursor = CursorId.Hand, OnClick = r.Like, Role = AutomationRole.Button,
        BlocksDragArm = true,
        Children =
        [
            new BoxEl
            {
                Visible = Prop.Of(() => r.P.State.Saved && !r.LikePop.Value),
                Children = [Glyph(Icons.HeartFill, ZuneListRules.HeartGlyph, Prop.Of(static () => R.Ink.AccentAction))],
            },
            Flow.Show(() => r.P.State.Saved && r.LikePop.Value,
                new BoxEl { Animate = ZuneListRules.HeartPop, Children = [Glyph(Icons.HeartFill, ZuneListRules.HeartGlyph, Prop.Of(static () => R.Ink.AccentAction))] }),
            new BoxEl
            {
                Visible = Prop.Of(() => !r.P.State.Saved),
                Children = [Glyph(Icons.Heart, ZuneListRules.HeartGlyph, Prop.Of(static () => Tok.TextSecondary))],
            },
        ],
    }.Interactive(Interaction.Subtle);

    /// <summary>The thumb (the row's own cover, else its album's) at the Cover radius, over the placeholder graded by the same url.</summary>
    static Element Artwork(BoundRow r, float art)
    {
        const int decodePx = Track.RowArtDecodePx;
        Func<string?> url = () => Track.RowArtUrl(r.P.Track.ForDisplay);
        var corners = R.Shape.All(Wavee.Theming.Roles.ShapeRole.Cover);
        return new BoxEl
        {
            ZStack = true, Width = art, Height = art, ClipToBounds = true, Corners = corners,
            Children =
            [
                new BoxEl
                {
                    Width = art, Height = art, Corners = corners,
                    Fill = Prop.Of(() =>
                    {
                        string? u = url();
                        if (u is { Length: > 0 }) _ = Wavee.Palette.Watch(u).Value;
                        return Design.PlaceholderFor(u);
                    }),
                },
                new ImageEl
                {
                    Source = Prop.Of(() => url() ?? ""), Fit = ImageFit.Cover, AspectRatio = 1f, DecodePx = decodePx,
                    Corners = corners, Placeholder = ColorF.Transparent,
                },
            ],
        };
    }

    // ── title · subline ──────────────────────────────────────────────────────────────────────────────────────────────────

    static Element TitleCell(BoundRow r, in ColumnSet set)
    {
        bool setArtist = set.Artist, setAlbum = set.Album;
        bool durationInline = !RowMetrics.ShowDuration(in set);
        var role = R.Type.TrackTitle("");
        Prop<bool> plainVisible = Prop.Of(() => !(r.P.State.IsNow && r.P.Marquee));
        var title = new TextEl(Prop.Of(() => r.P.Track.ForDisplay.Title))
        {
            FontFamily = role.FontFamily, Size = role.Size, LineHeight = role.LineHeight, Weight = role.ResolvedWeight,
            Color = RowInk(r, static () => Tok.TextPrimary),
            Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f,
            Visible = plainVisible,
        };
        var marquee = Flow.Show(() => r.P.State.IsNow && r.P.Marquee,
            Marquee.Of(Prop.Of(() => r.P.Track.ForDisplay.Title), Track.s_rowMarqueeStyle));
        var film = Glyph(Icons.Movie, ZuneListRules.FilmGlyph, RowInk(r, static () => Tok.TextTertiary), Prop.Of(() => r.P.Track.HasVideo)) with { Shrink = 0f };
        var line = new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.XS, MinWidth = 0f, ClipToBounds = true,
            Children = [new BoxEl { Direction = 0, Shrink = 1f, MinWidth = 0f, ClipToBounds = true, Children = [title, marquee] }, film],
        };
        var column = new BoxEl
        {
            Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f,
            Children = [line, Subline(r, setArtist, setAlbum, durationInline)],
        };
        return new BoxEl
        {
            Key = CellKey.Title, Direction = 0, AlignItems = FlexAlign.Center, MinWidth = 0f, ClipToBounds = true,
            Children = [column],
        };
    }

    /// <summary>The subline: [EPISODE] · [E] · the artists · the album as one bound span run that collapses when it has nothing to say;
    /// <paramref name="durationInline"/> (the Compact form) closes it with " · 4:18", or a withheld row's short reason.</summary>
    static Element Subline(BoundRow r, bool setArtist, bool setAlbum, bool durationInline)
    {
        var buffer = new SpanBuffer();
        var meta = R.Type.TrackMeta("");
        int ArtistCount(in RowPresentation p) => Track.ArtistInTitle(in p, setArtist) ? p.Track.ForDisplay.ArtistSlots.Length : 0;
        bool HasContainer(in RowPresentation p, Track d)
            => (Track.ShowAlbumInMeta(in p, false, setAlbum) || d.IsPodcast) && Track.ContainerOf(d).Name.Length > 0;
        Prop<ColorF> ink = RowInk(r, static () => Tok.TextSecondary);
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = ZuneListRules.MetaGap, MinWidth = 0f, ClipToBounds = true,
            Visible = durationInline ? (Prop<bool>)true : Prop.Of(() =>
            {
                var p = r.P;
                return p.State.OnThisPc
                    || RowCellRules.ShowMeta(false, Track.ArtistInTitle(in p, setArtist), Track.ShowAlbumInMeta(in p, false, setAlbum), Track.Badge(in p));
            }),
            Children =
            [
                new TextEl(Prop.Of(() => Loc.Get(Strings.Detail.Badge.Episode)))
                {
                    Size = R.Type.Eyebrow("").Size, LineHeight = R.Type.Eyebrow("").LineHeight, Weight = 600,
                    Case = R.Case.For(Wavee.Theming.Roles.LabelKind.Label),
                    Color = Tok.TextTertiary, Shrink = 0f, MaxLines = 1,
                    Visible = Prop.Of(() => r.P.Track.ForDisplay.IsPodcast),
                },
                // The "on this PC" ring leads the subline (Z-Liked). A Visible flip, not a mount: an absent mount would still cost the subline's gap.
                ZuneRowCells.OnThisPcMark(ink) with { Visible = Prop.Of(() => r.P.State.OnThisPc) },
                ZuneRowCells.ExplicitMark() with { Visible = Prop.Of(() => Track.Badge(r.P)) },
                new SpanTextEl(Prop.Of(() =>
                {
                    buffer.Clear();
                    var p = r.P;
                    var d = p.Track.ForDisplay;
                    ColorF spanInk = ZuneListRules.RowInk(p.State.IsNow, R.Ink.AccentDecor, Tok.TextSecondary);
                    FillLinkSpans(buffer, d, ArtistCount(in p), HasContainer(in p, d) ? Track.ContainerOf(d) : default, spanInk);
                    return buffer.Current;
                }))
                {
                    Size = meta.Size, LineHeight = meta.LineHeight, Color = Tok.TextSecondary,   // SpanTextEl.Color is a value: the spans carry the live ink
                    Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MaxLines = 1,
                    Grow = durationInline ? 0f : 1f, Shrink = 1f, Basis = durationInline ? float.NaN : 0f, MinWidth = 0f,
                    Visible = Prop.Of(() =>
                    {
                        var p = r.P;
                        return RowCellRules.LinkSpanCount(ArtistCount(in p), HasContainer(in p, p.Track.ForDisplay)) > 0;
                    }),
                    OnSpanClick = index =>
                    {
                        var p = r.Presentation.Peek();
                        var d = p.Track.ForDisplay;
                        int target = RowCellRules.LinkTarget(index, ArtistCount(in p), HasContainer(in p, d));
                        if (target >= 0) Track.GoToArtistAt(d, target);
                        else if (target == RowCellRules.Container) Track.GoToContainer(d);
                    },
                },
                new TextEl(Prop.Of(() =>
                {
                    var p = r.P;
                    bool lead = RowCellRules.LinkSpanCount(ArtistCount(in p), HasContainer(in p, p.Track.ForDisplay)) > 0;
                    string duration = Track.DurationLabel(p.Track, Track.NowUnix);
                    return lead ? ZuneListRules.InlineDurationLead + duration : duration;
                }))
                {
                    Size = meta.Size, LineHeight = meta.LineHeight, Color = ink,
                    Wrap = TextWrap.NoWrap, MaxLines = 1, Shrink = 0f,
                    Visible = durationInline,
                },
            ],
        };
    }

    // ── the factual lanes ────────────────────────────────────────────────────────────────────────────────────────────────

    static Element ArtistLinks(BoundRow r)
    {
        var buffer = new SpanBuffer();
        var meta = R.Type.DenseMeta("");
        return new SpanTextEl(Prop.Of(() =>
        {
            buffer.Clear();
            var p = r.P;
            var d = p.Track.ForDisplay;
            FillLinkSpans(buffer, d, d.ArtistSlots.Length, default, ZuneListRules.RowInk(p.State.IsNow, R.Ink.AccentAction, Tok.TextSecondary));
            return buffer.Current;
        }))
        {
            Size = meta.Size, LineHeight = meta.LineHeight, Color = Tok.TextSecondary,
            Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MaxLines = 1, MinWidth = 0f,
            Visible = Prop.Of(() => r.P.Track.ForDisplay.ArtistSlots.Length > 0),
            OnSpanClick = index =>
            {
                var d = r.Presentation.Peek().Track.ForDisplay;
                int target = RowCellRules.LinkTarget(index, d.ArtistSlots.Length, album: false);
                if (target >= 0) Track.GoToArtistAt(d, target);
            },
        };
    }

    /// <summary>The album lane as one link (an episode row's show); a name-less ref states the absence with the em dash and stays clickable
    /// whenever a uri exists.</summary>
    static Element AlbumLink(BoundRow r)
    {
        var buffer = new SpanBuffer();
        var meta = R.Type.DenseMeta("");
        return new SpanTextEl(Prop.Of(() =>
        {
            buffer.Clear();
            var p = r.P;
            var c = Track.ContainerOf(p.Track.ForDisplay);
            ColorF ink = c.Name.Length == 0 && !p.State.IsNow ? Tok.TextTertiary
                : ZuneListRules.RowInk(p.State.IsNow, R.Ink.AccentDecor, Tok.TextSecondary);
            buffer.Add(new TextSpan(c.Name.Length > 0 ? c.Name : Format.Dash, Color: ink, IsLink: c.Uri.IsValid));
            return buffer.Current;
        }))
        {
            Size = meta.Size, LineHeight = meta.LineHeight, Color = Tok.TextSecondary,
            Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MaxLines = 1,
            Grow = 1f, Basis = 0f, MinWidth = 0f,
            OnSpanClick = _ =>
            {
                var d = r.Presentation.Peek().Track.ForDisplay;
                if (Track.ContainerOf(d).Uri.IsValid) Track.GoToContainer(d);
            },
        };
    }

    /// <summary>A count of 0 is "not known yet", and a withheld row states the absence rather than a real-looking number.</summary>
    static Element PlaysCell(BoundRow r)
        => Factual(Prop.Of(() =>
            {
                var p = r.P;
                return Track.Withheld(in p, Track.NowUnix) || p.Track.PlayCount == 0 ? Format.Dash : Format.PlaysLabel(p.Track.PlayCount);
            }),
            RowInk(r, static () => Tok.TextTertiary));

    /// <summary>"[swatch] 101.5 · 8B": empty until the audio facts land.</summary>
    static Element TempoCell(BoundRow r)
    {
        static string? KeyOf(Track t) => Format.CamelotLabel(t.Camelot) ?? Format.KeyLabel(t.Key);
        var hasKey = Prop.Of(() => KeyOf(r.P.Track) is not null);
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.XS,
            Visible = Prop.Of(() => r.P.Track.Knows(TrackFields.Audio) && r.P.Track.Tempo != 0),
            Children =
            [
                new BoxEl
                {
                    Width = ZuneListRules.TempoDot, Height = ZuneListRules.TempoDot, Opacity = ZuneListRules.TempoDotOpacity, AlignSelf = FlexAlign.Center,
                    Fill = Prop.Of(() => R.Ink.DataDot(r.P.Track.CamelotColor)),
                    Visible = Prop.Of(() => r.P.Track.CamelotColor != 0),
                },
                Factual(Prop.Of(() => Track.s_tempoLabels.Get(r.P.Track.Tempo, Track.s_tempoFormat)), RowInk(r, static () => Tok.TextSecondary)),
                Factual("·", RowInk(r, static () => Tok.TextTertiary), visible: hasKey),
                Factual(Prop.Of(() => KeyOf(r.P.Track) ?? ""), RowInk(r, static () => Tok.TextTertiary), visible: hasKey),
            ],
        };
    }

    /// <summary>The time, or for a withheld row its short reason at the reason rung with the long reason as its tooltip (a real mount on the
    /// rare withheld flip, so a playable row carries no tooltip host). Z-State-Rows: "unavailable · 45% ink, says why in the time column".</summary>
    static Element DurationCell(BoundRow r, bool hoverActions, bool quiet)
    {
        Func<bool> withheld = () =>
        {
            var p = r.P;
            return Track.Dimmed(in p, Track.NowUnix);
        };
        // A withheld row says why (unavailable / the release date); a row only the offline session cannot reach says "not on this PC".
        var label = Prop.Of(() =>
        {
            var p = r.P;
            return !Track.Withheld(in p, Track.NowUnix) && Track.OfflineGone(in p)
                ? Loc.Get(Strings.Track.NotOnThisPc)
                : Track.DurationLabel(p.Track, Track.NowUnix);
        });
        var clock = Factual(label, RowInk(r, static () => Tok.TextSecondary), visible: Prop.Of(() => !withheld()));
        var reason = new TextEl(label)
        {
            Size = ZuneListRules.ReasonSize, LineHeight = R.Type.DenseMeta("").LineHeight,
            Color = Tok.TextTertiary, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
        };
        var time = new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.End, MinWidth = 0f,
            Children =
            [
                hoverActions
                    ? new BoxEl
                    {
                        Direction = 0, Shrink = 0f, Gap = ZuneListRules.RestHeartGap, AlignItems = FlexAlign.Center,
                        // The focused row hands the lane to the heart and the "..." too (they show for the keyboard): never both over one another.
                        Opacity = Prop.Of(() => r.Focused ? 0f : 1f), HoverOpacity = 0f,
                        Children =
                        [
                            // A liked track keeps its filled heart at rest, beside the time (Liked Songs rests with none: every row is liked).
                            Glyph(Icons.HeartFill, ZuneListRules.RestHeartGlyph, Prop.Of(static () => R.Ink.AccentAction),
                                  quiet ? (Prop<bool>)false : Prop.Of(() => r.P.State.Saved)) with { Shrink = 0f },
                            clock,
                        ],
                    }
                    : clock,
                Flow.Show(withheld, ToolTip.Wrap(reason, Prop.Of<string?>(() => Track.WithheldReason(r.P.Track, Track.NowUnix) ?? Loc.Get(Strings.Track.NotOnThisPcReason)))),
            ],
        };
        if (!hoverActions) return time;
        // The heart and the "..." take the time's place on row hover and focus (Z-Album, Z-Liked): no lane of their own, no reserve.
        return new BoxEl
        {
            ZStack = true, Width = Track.Lane.DurationHover, MinWidth = 0f, AlignSelf = FlexAlign.Stretch, AlignItems = FlexAlign.Center, Justify = FlexJustify.End,
            Children =
            [
                time with { Width = Track.Lane.DurationHover, AlignSelf = FlexAlign.Stretch },
                // The cluster overlays the reserved time lane (this ZStack's fixed width), so mounting it on the row's first
                // hover / focus (Track.Affordance) moves nothing: about twenty nodes the row no longer mounts up front.
                Track.Affordance(r, () => new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.End, MinWidth = 0f, Grow = 1f, AlignSelf = FlexAlign.Stretch,
                    Opacity = Prop.Of(() => r.Focused ? 1f : 0f), HoverOpacity = 1f,
                    Children =
                    [
                        // The versions chevron (a row with versions) leads the cluster: it has no lane of its own here.
                        new BoxEl { Width = Spacing.XXL, Direction = 0, Shrink = 0f, Children = [ExpandCell(r)] },
                        new BoxEl { Width = RowMetrics.HeartCol, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Children = [Heart(r)] },
                        new BoxEl { Width = ZuneListRules.HoverMoreBox, Direction = 0, Children = [Track.MoreCell(true, classic: true, CellKey.More, () => r.Focused)] },
                    ],
                }),
            ],
        };
    }

    /// <summary>The versions chevron (only a row with versions shows it), rotating open with the drawer, accent while open.</summary>
    static Element ExpandCell(BoundRow r)
        => Track.RowCenterCell(new BoxEl
        {
            Width = Spacing.XXL, Height = Spacing.XXL, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            HoverFill = Prop.Of(static () => R.Layer.RowHover),
            Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
            FocusVisualMargin = Edges4.All(1f),
            OnClick = r.ToggleExpand,
            BlocksDragArm = true,
            Visible = Prop.Of(() => r.P.Track.HasVersions()),
            Children =
            [
                Sidebar.Chevron.Section(() => r.P.Open, size: ZuneListRules.ChevronGlyph, identity: () => r.P.Track.Slot, accentWhenOpen: true),
            ],
        }, CellKey.Expand);

    /// <summary>The artists and the container as one link run (separators are not links).</summary>
    static void FillLinkSpans(SpanBuffer b, Track t, int artistCount, (EntityUri Uri, string Name) container, ColorF ink)
    {
        var slots = t.ArtistSlots;
        if (artistCount > slots.Length) artistCount = slots.Length;
        for (int i = 0; i < artistCount; i++)
        {
            if (i > 0) b.Add(new TextSpan(", ", Color: ink));
            var a = new Artist(slots[i]);
            b.Add(new TextSpan(a.Name, Color: ink, IsLink: a.Uri.IsValid));
        }
        if (container.Name is { Length: > 0 })
        {
            if (artistCount > 0) b.Add(new TextSpan(" · ", Color: ink));
            b.Add(new TextSpan(container.Name, Color: ink, IsLink: container.Uri.IsValid));
        }
    }

    // ══ THE SKIN ═════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The row's container (Z-State-Rows): bare at rest, the 5 % ink wash on hover, the pressed wash, the 12 % accent wash while
    /// selected (or while an .mp4 is dragged over it, with the accent line on its top edge). Square, no inset, no border, no zebra, no pill.
    /// Applies every wired member of <paramref name="a"/>.</summary>
    static BoxEl Skin(TrackRowSkin a)
    {
        var isSel = a.IsSelected;
        var cue = a.Cue;
        return new BoxEl
        {
            ZStack = true, MinHeight = a.RowHeight, ClipToBounds = true,
            Animate = a.Entrance ? ZuneListRules.MountEntrance : null,
            DropTarget = a.DropTarget,
            Fill = Prop.Of(() => isSel() || cue() ? R.Layer.SelectedRest : ColorF.Transparent),
            HoverFill = Prop.Of(() => isSel() ? R.Layer.SelectedHover : R.Layer.RowHover),
            PressedFill = Prop.Of(() => isSel() ? R.Layer.SelectedPressed : R.Layer.RowPressed),
            Draggable = a.Draggable,
            FocusVisualMargin = Design.FocusInsetRow,
            Focusable = false,                   // the ItemsView roving effect owns the one tab stop
            Role = AutomationRole.Button,
            OnPointerReleased = a.OnPointerReleased,
            OnKeyDown = a.OnKeyDown,
            OnFocusChanged = a.OnFocusChanged,
            OnHoverMove = a.OnHoverMove,
            OnPointerExit = a.OnPointerExit,
            Children =
            [
                new BoxEl
                {
                    Direction = 0, Grow = 1f, AlignItems = FlexAlign.Center, Animate = ZuneListRules.CheckShift,
                    Children = [a.CheckLane, a.Content],
                },
                new BoxEl
                {
                    Key = DropKey, AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Stretch, Height = ZuneListRules.DropLine,
                    Fill = Prop.Of(static () => R.Ink.AccentAction), Opacity = Prop.Of(() => cue() ? 1f : 0f),
                    HitTestVisible = false,
                },
            ],
        };
    }
}
