// ── Themes/Classic/Structure/Lists/ClassicTrackTable.cs — Classic's track table rows (themes plan 6.1, T8a) ──────────────────
//
// From `Entities/Track.UI.Bound.cs` (the bound grid and every cell of it) and `Entities/Track.Table.cs` (`TableHost.Skin`'s
// picture): the TrackListStyle the structure registers under `StyleKeys.TrackTable`, holding the Modern and the Classic row
// templates. Each template is a PAIR: the bound grid of lanes (built ONCE per slot per shape, every per-item value a bound Prop
// over the slot's presentation memo, frequent flips as `Visible` binds, the rare heavy branches as `Flow.Show`) and the row's
// skin (fills, hover and pressed states, the selection pill, the divider and their motion).
//
// WHAT THE CORE KEEPS. `Track.TableHost` and its two slot components decide which rows exist and what they hold: the
// presentation memo, the like edge, the reveal gate and the shimmer, the drawer, selection, the drag source, the keyboard and
// the context menu. The templates only DRAW what `BoundRow` (the slot's bound inputs) and `TrackRowSkin` (the wired behaviour)
// carry, and they run exactly when the code they came from ran: once per slot per shape, never per bind, never per scroll.
//
// MODERN AND CLASSIC. The row style is the structure's `rowStyle` option (M2), which the core reads as `ThemePolicies.Lists` into the table's
// `ColumnSet.Classic` lane decision; the core picks the template pair by it. The two styles share one body per cell, branching on
// the `classic` argument exactly where the source did, so a row draws the same tree it always drew.
//
// The pure decisions (`RowCellRules`: the # cell's rest state and hover reveal, the chart mark, the duration lane's arm, the link
// maps) and the data helpers (`Track.Withheld`, `Track.NowUnix`, `Track.AddedByLabel`, the tempo label cache) stay in
// `Entities/Track.UI.Bound.cs`, and the cell wrappers and navigation the eager row shares (`Track.RowCenterCell`, `Track.MoreCell`,
// `Track.GoToArtistAt`...) stay on `Track` until T8c. Numbers are `Rules\ClassicTrackRowLayout.cs` (WVTH003 allows no literal look here).

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Classic;

using BoundRow = global::Wavee.Track.BoundRow;
using CellKey = global::Wavee.Track.CellKey;
using ColumnSet = global::Wavee.Track.ColumnSet;
using Format = global::Wavee.Track.Format;
using RowCellRules = global::Wavee.Track.RowCellRules;
using RowMetrics = global::Wavee.Track.RowMetrics;
using RowPresentation = global::Wavee.Track.RowPresentation;

/// <summary>Classic's row templates for the track table: the Modern pair and the Classic pair of (bound grid, skin).</summary>
internal static class ClassicTrackTable
{
    /// <summary>The style Classic registers under <c>StyleKeys.TrackTable</c>. Stateless: a table, a drawer and a test share it.</summary>
    public static readonly TrackListStyle Style = new(
        stamp: 1,
        modern: new TrackRowTemplates(ModernGrid, ModernSkin),
        classic: new TrackRowTemplates(ClassicGrid, ClassicSkin),
        chrome: ClassicTrackChrome.Templates);

    static Element ModernGrid(BoundRow row, in ColumnSet set, TrackSize[] tracks, float rowH, float art)
        => BoundGrid(row, in set, tracks, rowH, art, classic: false);

    static Element ClassicGrid(BoundRow row, in ColumnSet set, TrackSize[] tracks, float rowH, float art)
        => BoundGrid(row, in set, tracks, rowH, art, classic: true);

    static BoxEl ModernSkin(TrackRowSkin skin) => Skin(skin, classic: false);

    static BoxEl ClassicSkin(TrackRowSkin skin) => Skin(skin, classic: true);

    // Single-construction twins of the row's text rungs and the icon glyph: the bound record is built once with its
    // Props in the initializer, never a rung plus a `with` clone.
    static TextEl Factual(bool classic, Prop<string> text, Prop<ColorF> color, Prop<bool>? visible = null) => new(text)
    {
        Size = classic ? 14f : 12f, LineHeight = classic ? 20f : 16f, Color = color,
        Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
        Visible = visible ?? true,
    };

    static TextEl FactualFill(bool classic, Prop<string> text, Prop<ColorF> color, Prop<bool>? visible = null) => new(text)
    {
        Size = classic ? 14f : 12f, LineHeight = classic ? 20f : 16f, Color = color,
        Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
        Grow = 1f, Basis = 0f, MinWidth = 0f,
        Visible = visible ?? true,
    };

    static TextEl Glyph(Prop<string> glyph, float size, Prop<ColorF> color, Prop<bool>? visible = null) => new(glyph)
    {
        FontFamily = Tok.Definition.Fonts.Icon, Size = size, Color = color, Visible = visible ?? true,
    };

    // ══ 3. THE BOUND GRID ════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The bound twin of <c>Grid</c>: same lanes, same keys, same wrappers, every per-item value bound.
    /// Built once per slot per shape; the shape (column set, tracks, row height, art) is the only thing read here.</summary>
    static Element BoundGrid(BoundRow r, in ColumnSet set, TrackSize[] tracks, float rowH, float art, bool classic)
    {
        float artSize = art > 0f ? art : RowMetrics.ThumbSize;
        var cells = new Element[Track.CellCount(in set)];
        int i = 0;

        if (!set.IndexHidden) cells[i++] = BoundNumberCell(r, classic);
        if (set.Heart) cells[i++] = Track.RowCenterCell(BoundHeartReveal(r, classic, set.Quiet), CellKey.Heart);
        if (set.Thumb) cells[i++] = Track.RowCenterCell(BoundArtwork(r, artSize), CellKey.Art);
        cells[i++] = BoundTitleCell(r, in set, classic);
        if (set.Artist) cells[i++] = Track.RowLeftCell(BoundArtistLinks(r, classic), CellKey.Artist);
        if (set.Album) cells[i++] = Track.RowLeftCell(BoundAlbumLink(r, classic), CellKey.Album);
        if (set.By) cells[i++] = BoundAddedByCell(r, classic);
        if (set.Date) cells[i++] = Track.RowLeftCell(BoundDateCell(r, classic), CellKey.Date);
        if (set.Plays) cells[i++] = Track.RowEndCell(BoundPlaysCell(r, classic), CellKey.Plays);
        if (RowMetrics.ShowTempo(in set)) cells[i++] = Track.RowEndCell(BoundTempoCell(r, classic), CellKey.Tempo);
        if (RowMetrics.ShowHeartTrailing(in set)) cells[i++] = Track.RowControlCell(BoundHeartReveal(r, classic, set.Quiet), CellKey.HeartTrailing);
        if (RowMetrics.ShowDuration(in set)) cells[i++] = Track.RowEndCell(BoundDurationCell(r, classic), CellKey.Duration);
        // The Compact row's "…" is a 40 target shown on every row (C-RowStates-Compact): there is no hover to reveal it.
        if (set.Actions) cells[i++] = set.Compact
            ? Track.MoreCell(true, classic, CellKey.More, () => r.Focused, box: ClassicDetailTiers.CompactTarget, atRest: true)
            : Track.LazyMoreCell(r, classic, CellKey.More);
        if (set.Expand) cells[i++] = BoundExpandCell(r);

        float padX = RowMetrics.PadXFor(set.Tier);
        // A Modern row pays its plate's inset out of the gutter; a Classic or a quiet (full-width, D16) row's plate has none.
        float inner = classic || set.Quiet ? padX : padX - RowMetrics.RowInset;
        return new GridEl
        {
            Columns = tracks, ColGap = RowMetrics.ColGapFor(set.Tier), RowHeight = rowH, Grow = 1f,
            Padding = new Edges4(inner, 0f, inner, 0f),
            Children = cells,
        };
    }

    // ── the # cell ───────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Two shapes under one keyed cell, swapped by <c>Flow.Show</c> on the rare withheld flip: the reveal shape
    /// (rest layer fading out, the transport fading in on row hover) and the rest-only shape (no hover reveal).
    /// <para>The cell is a ZStack, like <c>NumberCell</c>'s, and that is load-bearing: the <c>Flow.Show</c> boundary is a
    /// default node (Grow 0), so in a ROW cell it shrink-wrapped the shape to its content's natural width — the 24-DIP
    /// transport box then sat wherever the narrower stack ended and the stack's own clip cut the glyph in half, and the
    /// number and the equalizer centred in that narrow stack instead of the lane. A ZStack hands its one child the whole
    /// lane × row, so every layer centres in the lane the header's "#" is centred in.</para></summary>
    static Element BoundNumberCell(BoundRow r, bool classic)
    {
        Element reveal = new BoxEl
        {
            ZStack = true, MinWidth = 0f, ClipToBounds = true, Grow = 1f, AlignSelf = FlexAlign.Stretch,
            Children =
            [
                BoundRestLayer(r, classic, hoverFade: true),
                // The transport mounts on the row's first hover / focus (Track.Affordance): it draws nothing before.
                Track.Affordance(r, () => new BoxEl
                {
                    Direction = 0, Grow = 1f, AlignItems = FlexAlign.Stretch, Justify = FlexJustify.Center,
                    Opacity = 0f, HoverOpacity = 1f,
                    Children =
                    [
                        new BoxEl
                        {
                            // The click target fills the lane (Basis 0: its width is never the glyph's) and stacks the ring and
                            // the transport, both centred — side by side in a row, the (possibly empty) ring boundary shared
                            // the row's centring with the glyph and could push it off the lane's middle.
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
                                        Glyph(Prop.Of(() => r.P.State is { IsNow: true, IsPlaying: true } ? Icons.Pause : Icons.Play), 12f,
                                              Prop.Of(() => r.P.State.IsNow ? Tok.AccentTextPrimary : Tok.TextPrimary)),
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
            Children = [BoundRestLayer(r, classic, hoverFade: false)],
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

    static RowCellRules.Rest RestOf(BoundRow r, bool classic)
    {
        var p = r.P;
        var st = p.State;
        return RowCellRules.RestOf(in st, classic);
    }

    /// <summary>The rest layer: number + chart mark, star, Volume mark as <c>Visible</c> flips; the equalizer and the
    /// buffering ring as real mounts.</summary>
    static Element BoundRestLayer(BoundRow r, bool classic, bool hoverFade)
    {
        Element[] kids;
        if (classic)
            kids =
            [
                Glyph(Icons.Volume, 13f, Tok.AccentTextPrimary, Prop.Of(() => RestOf(r, true) == RowCellRules.Rest.Volume)),
                Flow.Show(() => RestOf(r, true) == RowCellRules.Rest.Spinner, Track.Spinner()),
            ];
        else
            kids =
            [
                new BoxEl
                {
                    Direction = 0, Gap = ClassicTrackRowLayout.NumberGap, AlignItems = FlexAlign.Center,
                    Visible = Prop.Of(() => RestOf(r, false) == RowCellRules.Rest.Number),
                    Children =
                    [
                        new TextEl(Prop.Of(() => ClassicRowCells.RowNumberLabel(r.P.Display + 1))) { Size = ClassicTrackRowLayout.CaptionSize, LineHeight = ClassicTrackRowLayout.CaptionLine, Color = Tok.TextTertiary },
                        new TextEl(Prop.Of(() => ChartMarkText(RowCellRules.ChartMarkOf(r.P.Chart))))
                        {
                            Size = ClassicTrackRowLayout.ChartMarkSize, LineHeight = ClassicTrackRowLayout.ChartMarkLine, Weight = 700,
                            Color = Prop.Of(() => RowCellRules.ChartMarkOf(r.P.Chart) == RowCellRules.ChartMark.Down
                                ? Tok.SystemFillCritical : Tok.SystemFillSuccess),
                            Visible = Prop.Of(() => RowCellRules.ChartMarkOf(r.P.Chart) != RowCellRules.ChartMark.None),
                        },
                    ],
                },
                Glyph(Icons.FavoriteStarFill, 11f, Tok.AccentTextPrimary, Prop.Of(() => RestOf(r, false) == RowCellRules.Rest.Star)),
                Flow.Show(() => RestOf(r, false) == RowCellRules.Rest.Equalizer,
                    Controls.Equalizer(r.Playing, r.Accent, Track.RowEqualizerHeight, r.Hovered)),
                Flow.Show(() => RestOf(r, false) == RowCellRules.Rest.Spinner, Track.Spinner()),
            ];
        return new BoxEl
        {
            Grow = 1f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            HoverOpacity = hoverFade ? 0f : float.NaN,
            Children = kids,
        };
    }

    static string ChartMarkText(RowCellRules.ChartMark mark) => mark switch
    {
        RowCellRules.ChartMark.Up => "▲",
        RowCellRules.ChartMark.Down => "▼",
        RowCellRules.ChartMark.New => Loc.Get(Track.LocChartNew),
        _ => "",
    };

    // ── heart · art ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The heart lane's row reveal (tuned Classic L2): a liked row keeps its filled heart at rest, an unliked one
    /// shows nothing until the ROW is hovered and then fades its outline in. The reveal rides a NON-interactive wrapper
    /// around the heart's own click target, so the hover it reads is the row skin's (the same idiom as the # cell's
    /// layers); the rest opacity is bound to the presentation and the slot's focus, so a like re-resolves it with no rebuild
    /// and the focused row shows its outline to the keyboard.</summary>
    // Mounts on the row's first hover / focus, or (outside a quiet list) once the row is saved — the resting filled heart.
    static Element BoundHeartReveal(BoundRow r, bool classic, bool quiet) => Track.Affordance(r, () => new BoxEl
    {
        Grow = 1f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        // A quiet list (Liked Songs, D16) rests with no heart at all: every row is liked, so only the hovered or focused row shows it.
        Opacity = quiet ? Prop.Of(() => r.Focused ? 1f : 0f) : Prop.Of(() => r.P.State.Saved || r.Focused ? 1f : 0f), HoverOpacity = 1f,
        Children = [BoundHeart(r, classic)],
    }, quiet ? null : () => r.P.State.Saved);

    /// <summary>The heart (ch 01 §0.3) as three glyphs under one circle: the resting filled heart, the popping filled
    /// heart (a real mount on the like EDGE, so the Enter spring replays exactly once) and the outline.</summary>
    static Element BoundHeart(BoundRow r, bool classic)
    {
        ColorF onInk = classic ? Tok.TextPrimary : Tok.AccentTextPrimary;
        ColorF offInk = classic ? Tok.TextSecondary : Tok.TextTertiary;
        return new BoxEl
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
                    Children = [Icon(Icons.HeartFill, 14f, onInk)],
                },
                Flow.Show(() => r.P.State.Saved && r.LikePop.Value,
                    new BoxEl { Animate = ClassicRowCells.s_heartPopIn, Children = [Icon(Icons.HeartFill, 14f, onInk)] }),
                new BoxEl
                {
                    Visible = Prop.Of(() => !r.P.State.Saved),
                    Children = [Icon(Icons.Heart, 14f, offInk)],
                },
            ],
        }.Interactive(Interaction.Subtle);
    }

    /// <summary>The thumb as <c>Controls.Artwork</c> lays it out (a square decode at the ONE shared
    /// <c>Track.RowArtDecodePx</c> — see its doc for why it is not <c>art * 2</c> — with the small-edge placeholder tile
    /// beneath), with the url bound: the row's own cover, or its album's when it has none (<c>RowArtUrl</c>). The
    /// tint below reads the SAME url, so a fallback cover is graded by its own palette and not left neutral.</summary>
    static Element BoundArtwork(BoundRow r, float art)
    {
        const int decodePx = Track.RowArtDecodePx;
        Func<string?> url = () => Track.RowArtUrl(r.P.Track.ForDisplay);
        return new BoxEl
        {
            ZStack = true, Width = art, Height = art, ClipToBounds = true,
            Corners = CornerRadius4.All(Radii.Control),
            Children =
            [
                new BoxEl
                {
                    Width = art, Height = art, Corners = CornerRadius4.All(Radii.Control),
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
                    Corners = CornerRadius4.All(Radii.Control), Placeholder = ColorF.Transparent,
                },
            ],
        };
    }

    // ── title · metadata ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The plain ellipsis title and, on the now-playing row when the setting allows, the marquee — swapped by a
    /// real mount so the marquee host only exists on that one row. <paramref name="inlineVideo"/> (the Modern row) makes the
    /// plain title one span run that carries the small film glyph right after the title when the track has a video
    /// (tuned Classic L3: a video is marked there, never in a trailing lane).</summary>
    static Element[] BoundTitleRuns(BoundRow r, bool inlineVideo, ushort weight)
    {
        Prop<bool> plainVisible = Prop.Of(() => !(r.P.State.IsNow && r.P.Marquee));
        Element plain;
        if (inlineVideo)
        {
            var buffer = new SpanBuffer();
            plain = new SpanTextEl(Prop.Of(() =>
            {
                buffer.Clear();
                var p = r.P;
                bool now = p.State.IsNow;
                FillClassicSpans(buffer, p.Track.ForDisplay, 0, p.Track.HasVideo, weight,
                    now ? Tok.AccentTextPrimary : Tok.TextPrimary, Tok.TextSecondary, now ? Tok.AccentTextPrimary : Tok.TextTertiary);
                return buffer.Current;
            }))
            {
                Size = ClassicTrackRowLayout.BodySize, LineHeight = ClassicTrackRowLayout.BodyLine,
                Color = Tok.TextPrimary,   // SpanTextEl.Color is a value: the spans carry the live inks and the 600 weight
                Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                Visible = plainVisible,
            };
        }
        else
            // R.Type.TrackTitle's rung (BodyStrong 14/20/600), constructed once with its binds.
            plain = new TextEl(Prop.Of(() => r.P.Track.ForDisplay.Title))
            {
                Size = ClassicTrackRowLayout.BodySize, LineHeight = ClassicTrackRowLayout.BodyLine, Weight = weight,
                Color = Prop.Of(() => r.P.State.IsNow ? Tok.AccentTextPrimary : Tok.TextPrimary),
                Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                Visible = plainVisible,
            };
        var marquee = Flow.Show(() => r.P.State.IsNow && r.P.Marquee,
            Marquee.Of(Prop.Of(() => r.P.Track.ForDisplay.Title), Track.s_rowMarqueeStyle));
        return [plain, marquee];
    }

    static Element BoundTitleCell(BoundRow r, in ColumnSet set, bool classic)
    {
        bool setArtist = set.Artist, setAlbum = set.Album;
        // A quiet list's titles are regular weight (D16): the collection reads as one list, not a column of headings.
        ushort weight = set.Quiet ? ClassicTrackRowLayout.QuietTitleWeight : ClassicTrackRowLayout.TitleWeight;
        var runs = BoundTitleRuns(r, inlineVideo: !classic, weight);
        Element[] column;
        if (classic) column = [BoundClassicTitleLine(r, runs, setArtist, weight)];
        else column = [runs[0], runs[1], BoundMetadataLine(r, setArtist, setAlbum, durationInline: !RowMetrics.ShowDuration(in set))];
        var titleCol = new BoxEl
        {
            Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = Spacing.XXS,
            // Withheld, or a row the offline session cannot reach (`Track.Dimmed`): the title column steps back, the lane says why.
            Opacity = Prop.Of(() =>
            {
                var p = r.P;
                return Track.Dimmed(in p, Track.NowUnix) ? Track.RowNotYetOutOpacity : 1f;
            }),
            Children = column,
        };
        return new BoxEl
        {
            Key = CellKey.Title, Direction = 0, AlignItems = FlexAlign.Center, MinWidth = 0f, ClipToBounds = true,
            Children = [titleCol],
        };
    }

    /// <summary>The Modern subline (ch 01 §1.1): [EPISODE] · [E] · artists · album as one bound span run; the line
    /// collapses when it has nothing to say. <paramref name="durationInline"/> (the Compact row form, board C-TrackList compact: no duration
    /// lane) closes the line with " · 3:20", or a withheld row's short reason.</summary>
    static Element BoundMetadataLine(BoundRow r, bool setArtist, bool setAlbum, bool durationInline = false)
    {
        var buffer = new SpanBuffer();
        int ArtistCount(in RowPresentation p) => Track.ArtistInTitle(in p, setArtist) ? p.Track.ForDisplay.ArtistSlots.Length : 0;
        bool HasContainer(in RowPresentation p, Track d)
            => (Track.ShowAlbumInMeta(in p, false, setAlbum) || d.IsPodcast) && Track.ContainerOf(d).Name.Length > 0;
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = ClassicTrackRowLayout.MetaGap, MinWidth = 0f, ClipToBounds = true,
            // The Compact form's line always states the duration, so it never collapses there.
            Visible = durationInline ? (Prop<bool>)true : Prop.Of(() =>
            {
                var p = r.P;
                return RowCellRules.ShowMeta(false, Track.ArtistInTitle(in p, setArtist), Track.ShowAlbumInMeta(in p, false, setAlbum), Track.Badge(in p));
            }),
            Children =
            [
                // R.Type.Eyebrow's rung (Caption 12/16, 600, tracked) and Controls.ExplicitBadge(14), each built once.
                new TextEl(Prop.Of(() => Loc.Get(Strings.Detail.Badge.Episode)))
                {
                    Size = ClassicTrackRowLayout.CaptionSize, LineHeight = ClassicTrackRowLayout.CaptionLine, Weight = 600, CharSpacing = R.Type.EyebrowTracking,
                    Color = Tok.TextTertiary, Shrink = 0f, MaxLines = 1,
                    Visible = Prop.Of(() => r.P.Track.ForDisplay.IsPodcast),
                },
                Controls.ExplicitBadge(ClassicTrackRowLayout.EpisodeBadge) with { Visible = Prop.Of(() => Track.Badge(r.P)) },
                new SpanTextEl(Prop.Of(() =>
                {
                    buffer.Clear();
                    var p = r.P;
                    var d = p.Track.ForDisplay;
                    FillLinkSpans(buffer, d, ArtistCount(in p), HasContainer(in p, d) ? Track.ContainerOf(d) : default);
                    return buffer.Current;
                }))
                {
                    Size = ClassicTrackRowLayout.CaptionSize, LineHeight = ClassicTrackRowLayout.CaptionLine, Color = Tok.TextSecondary,
                    Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MaxLines = 1,
                    Grow = durationInline ? 0f : 1f, Shrink = 1f, Basis = durationInline ? float.NaN : 0f, MinWidth = 0f,
                    Visible = Prop.Of(() =>
                    {
                        var p = r.P;
                        var d = p.Track.ForDisplay;
                        return RowCellRules.LinkSpanCount(ArtistCount(in p), HasContainer(in p, d)) > 0;
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
                    var d = p.Track.ForDisplay;
                    bool lead = RowCellRules.LinkSpanCount(ArtistCount(in p), HasContainer(in p, d)) > 0;
                    string duration = LaneLabel(in p, Track.NowUnix);
                    return lead ? ClassicTrackRowLayout.InlineDurationLead + duration : duration;
                }))
                {
                    Size = ClassicTrackRowLayout.CaptionSize, LineHeight = ClassicTrackRowLayout.CaptionLine, Color = Tok.TextSecondary,
                    Wrap = TextWrap.NoWrap, MaxLines = 1, Shrink = 0f,
                    Visible = durationInline,
                },
            ],
        };
    }

    /// <summary>Classic's one-line title (ch 01 W8): the folded span run (artists in the title) or the plain title plus
    /// the film glyph, then the EXPLICIT word-mark pinned trailing. Both arms stay mounted; the fold flag picks one.</summary>
    static Element BoundClassicTitleLine(BoundRow r, Element[] runs, bool setArtist, ushort weight)
    {
        var buffer = new SpanBuffer();
        static bool ShowVideo(in RowPresentation p) => p.Track.HasVideo;
        Prop<ColorF> Ink(Func<ColorF> plain) => Prop.Of(() => r.P.State.IsNow ? Tok.AccentTextPrimary : plain());

        Element folded = new SpanTextEl(Prop.Of(() =>
        {
            buffer.Clear();
            var p = r.P;
            bool now = p.State.IsNow;
            FillClassicSpans(buffer, p.Track.ForDisplay, p.Track.ForDisplay.ArtistSlots.Length, ShowVideo(in p), weight,
                now ? Tok.AccentTextPrimary : Tok.TextPrimary, now ? Tok.AccentTextPrimary : Tok.TextSecondary,
                now ? Tok.AccentTextPrimary : Tok.TextTertiary);
            return buffer.Current;
        }))
        {
            Size = ClassicTrackRowLayout.BodySize, LineHeight = ClassicTrackRowLayout.BodyLine, Color = Tok.TextPrimary,   // SpanTextEl.Color is a value: the spans carry the live inks
            Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MaxLines = 1,
            Shrink = 1f, MinWidth = 0f,
            Visible = Prop.Of(() => Track.ArtistInTitle(r.P, setArtist)),
            OnSpanClick = index =>
            {
                var p = r.Presentation.Peek();
                var d = p.Track.ForDisplay;
                int artist = RowCellRules.ClassicArtistOf(index, ShowVideo(in p), d.ArtistSlots.Length);
                if (artist >= 0) Track.GoToArtistAt(d, artist);
            },
        };
        Element unfolded = new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.XS,
            Grow = 1f, Basis = 0f, MinWidth = 0f, ClipToBounds = true,
            Visible = Prop.Of(() => !Track.ArtistInTitle(r.P, setArtist)),
            Children =
            [
                new BoxEl { Grow = 1f, Basis = 0f, MinWidth = 0f, ClipToBounds = true, Children = runs },
                new TextEl(Icons.Movie)
                {
                    FontFamily = Tok.Definition.Fonts.Icon, Size = ClassicTrackRowLayout.FilmGlyphSize, Shrink = 0f, Color = Ink(static () => Tok.TextTertiary),
                    Visible = Prop.Of(() => ShowVideo(r.P)),
                },
            ],
        };
        var lead = new BoxEl
        {
            Direction = 0, Grow = 1f, Basis = 0f, MinWidth = 0f, AlignItems = FlexAlign.Center, ClipToBounds = true,
            Children = [folded, unfolded],
        };
        // ClassicExplicitBadge with its ink bound: accent at full opacity on the now-playing row, else tertiary at 0.6.
        var badge = new BoxEl
        {
            Height = ClassicTrackRowLayout.ClassicBadgeHeight, Padding = new Edges4(Spacing.XXS, 0f, Spacing.XXS, 0f), Shrink = 0f,
            Corners = CornerRadius4.All(2f), BorderWidth = 1f, BorderColor = Ink(static () => Tok.TextTertiary),
            Opacity = Prop.Of(() => r.P.State.IsNow ? 1f : 0.6f),
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Visible = Prop.Of(() => Track.Badge(r.P)),
            Children =
            [
                new TextEl(Prop.Of(() => Loc.Get(Strings.Detail.Badge.Explicit)))
                {
                    Size = ClassicTrackRowLayout.ClassicBadgeSize, LineHeight = ClassicTrackRowLayout.ClassicBadgeLine, Weight = 600, Color = Ink(static () => Tok.TextTertiary),
                },
            ],
        };
        return new BoxEl
        {
            Direction = 0, Grow = 1f, Basis = 0f, MinWidth = 0f, AlignItems = FlexAlign.Center, Gap = Spacing.S,
            ClipToBounds = true,
            Children = [lead, badge],
        };
    }

    // ── the factual lanes ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Classic's now-playing row tints every factual lane accent; otherwise the lane's own rung.</summary>
    static Prop<ColorF> LaneInk(BoundRow r, bool classic, Func<ColorF> plain)
        => classic ? Prop.Of(() => r.P.State.IsNow ? Tok.AccentTextPrimary : plain()) : plain();

    static Element BoundArtistLinks(BoundRow r, bool classic)
    {
        var buffer = new SpanBuffer();
        return new SpanTextEl(Prop.Of(() =>
        {
            buffer.Clear();
            var p = r.P;
            var d = p.Track.ForDisplay;
            ColorF ink = classic && p.State.IsNow ? Tok.AccentTextPrimary : Tok.TextSecondary;
            FillLinkSpans(buffer, d, d.ArtistSlots.Length, default, ink);
            return buffer.Current;
        }))
        {
            Size = classic ? 14f : 12f, LineHeight = classic ? 20f : 16f,
            Color = Tok.TextSecondary,   // SpanTextEl.Color is a value: the spans carry the lane ink
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

    /// <summary>The album lane as one link (an episode row's SHOW): a name-less ref states the absence with the em dash
    /// and stays clickable whenever a uri exists (ch 01 W26).</summary>
    static Element BoundAlbumLink(BoundRow r, bool classic)
    {
        var buffer = new SpanBuffer();
        return new SpanTextEl(Prop.Of(() =>
        {
            buffer.Clear();
            var p = r.P;
            var c = Track.ContainerOf(p.Track.ForDisplay);
            ColorF ink = classic && p.State.IsNow ? Tok.AccentTextPrimary : Tok.TextSecondary;
            if (c.Name.Length == 0 && ink == Tok.TextSecondary) ink = Tok.TextTertiary;
            buffer.Add(new TextSpan(c.Name.Length > 0 ? c.Name : Format.Dash, Color: ink, IsLink: c.Uri.IsValid));
            return buffer.Current;
        }))
        {
            Size = classic ? 14f : 12f, LineHeight = classic ? 20f : 16f,
            Color = Tok.TextSecondary,   // SpanTextEl.Color is a value: the span carries the live ink
            Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MaxLines = 1,
            Grow = 1f, Basis = 0f, MinWidth = 0f,
            OnSpanClick = _ =>
            {
                var d = r.Presentation.Peek().Track.ForDisplay;
                if (Track.ContainerOf(d).Uri.IsValid) Track.GoToContainer(d);
            },
        };
    }

    /// <summary>The Added-by lane (ch 01 §3): avatar + name (Modern), the bare name (Classic); empty for no adder.</summary>
    static Element BoundAddedByCell(BoundRow r, bool classic)
    {
        var label = Prop.Of(() => Track.AddedByLabel(r));
        var ink = LaneInk(r, classic, static () => Tok.TextSecondary);
        var present = Prop.Of(() => r.P.AddedBy.Slot > 0);
        if (classic)
            return Track.RowLeftCell(FactualFill(true, label, ink, present), CellKey.By);
        return new BoxEl
        {
            Key = CellKey.By, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Start, Gap = Spacing.S,
            MinWidth = 0f, ClipToBounds = true,
            Children =
            [
                PersonPicture.Bound(label, Prop.Of(() => Track.AddedByImageUrl(r)), Spacing.XXL) with { Visible = present },
                new TextEl(label)
                {
                    Size = ClassicTrackRowLayout.CaptionSize, LineHeight = ClassicTrackRowLayout.CaptionLine, Color = ink, Grow = 1f, Basis = 0f, MinWidth = 0f, MaxLines = 1,
                    Trim = TextTrim.CharacterEllipsis, Visible = present,
                },
            ],
        };
    }

    static Element BoundDateCell(BoundRow r, bool classic)
        => FactualFill(classic, Prop.Of(() => Format.DateAddedLabel(r.P.AddedAt, Track.NowUnix)),
                       LaneInk(r, classic, static () => Tok.TextSecondary));

    /// <summary>A count of 0 is "not known yet", and a withheld row states the absence rather than a real-looking number.</summary>
    static Element BoundPlaysCell(BoundRow r, bool classic)
        => Factual(classic, Prop.Of(() =>
            {
                var p = r.P;
                return Track.Withheld(in p, Track.NowUnix) || p.Track.PlayCount == 0 ? Format.Dash : Format.PlaysLabel(p.Track.PlayCount);
            }),
            LaneInk(r, classic, static () => Tok.TextTertiary));

    /// <summary>"[swatch] 101.5 · 8B": empty until kind 222 lands (ch 01 §0.7).</summary>
    static Element BoundTempoCell(BoundRow r, bool classic)
    {
        static string? KeyOf(Track t) => Format.CamelotLabel(t.Camelot) ?? Format.KeyLabel(t.Key);
        var primary = LaneInk(r, classic, static () => Tok.TextSecondary);
        var secondary = LaneInk(r, classic, static () => Tok.TextTertiary);
        var hasKey = Prop.Of(() => KeyOf(r.P.Track) is not null);
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.XS,
            Visible = Prop.Of(() => r.P.Track.Knows(TrackFields.Audio) && r.P.Track.Tempo != 0),
            Children =
            [
                new BoxEl
                {
                    Width = ClassicTrackRowLayout.TempoDot, Height = ClassicTrackRowLayout.TempoDot, Corners = CornerRadius4.All(1.5f), Opacity = ClassicTrackRowLayout.TempoDotOpacity, AlignSelf = FlexAlign.Center,
                    Fill = Prop.Of(() => R.Ink.DataDot(r.P.Track.CamelotColor)),
                    Visible = Prop.Of(() => r.P.Track.CamelotColor != 0),
                },
                Factual(classic, Prop.Of(() => Track.s_tempoLabels.Get(r.P.Track.Tempo, Track.s_tempoFormat)), primary),
                Factual(classic, "·", secondary, hasKey),
                Factual(classic, Prop.Of(() => KeyOf(r.P.Track) ?? ""), secondary, hasKey),
            ],
        };
    }

    /// <summary>The lane's words: the clock, a withheld row's short reason ("Unavailable", the release date), or "Not on PC" for a row
    /// only the offline session cannot reach (<see cref="Track.OfflineGone(in RowPresentation)"/>; an indicator, no play gate reads it).</summary>
    static string LaneLabel(in RowPresentation p, long now)
        => !Track.Withheld(in p, now) && Track.OfflineGone(in p) ? Loc.Get(Strings.Track.NotOnThisPc) : Track.DurationLabel(p.Track, now);

    /// <summary>The clock, or for a withheld or offline-gone row its short reason at the reason rung with the long reason as its
    /// tooltip (tuned Classic L4). The reason arm is a real mount (<c>Flow.Show</c>) on the rare flip, so a playable row carries
    /// no tooltip host.</summary>
    static Element BoundDurationCell(BoundRow r, bool classic)
    {
        Func<bool> withheld = () =>
        {
            var p = r.P;
            return Track.Dimmed(in p, Track.NowUnix);
        };
        var label = Prop.Of(() =>
        {
            var p = r.P;
            return LaneLabel(in p, Track.NowUnix);
        });
        var clock = Factual(classic, label, LaneInk(r, classic, static () => Tok.TextSecondary), Prop.Of(() => !withheld()));
        var reason = new TextEl(label)
        {
            Size = ClassicTrackRowLayout.ReasonSize, LineHeight = classic ? ClassicTrackRowLayout.BodyLine : ClassicTrackRowLayout.CaptionLine,
            Color = Tok.TextTertiary, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
        };
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.End, MinWidth = 0f,
            Children =
            [
                clock,
                Flow.Show(withheld, ToolTip.Wrap(reason, Prop.Of<string?>(() =>
                    Track.WithheldReason(r.P.Track, Track.NowUnix) ?? Loc.Get(Strings.Track.NotOnThisPcReason)))),
            ],
        };
    }

    // ── trailing chrome ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary><c>ExpandCell</c> bound: a chevron-down that ROTATES open on the reveal spring with the drawer, inked
    /// accent while open; a recycle onto another track re-seeds it without spinning. Only a row with versions shows it
    /// (tuned Classic L1, <c>Track.HasVersions</c>); every other row keeps the lane empty and reaches its drawer through the
    /// menu's "Track details".</summary>
    // Mounts once the row shows versions (or on its first hover / focus): most rows have none and draw nothing here.
    static Element BoundExpandCell(BoundRow r)
        => Track.RowCenterCell(Track.Affordance(r, () => new BoxEl
        {
            Width = Spacing.XXL, Height = Spacing.XXL, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = Radii.ControlAll, HoverFill = Tok.FillControlSecondary,
            Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
            FocusVisualMargin = Edges4.All(1f),
            OnClick = r.ToggleExpand,
            BlocksDragArm = true,
            Visible = Prop.Of(() => r.P.Track.HasVersions()),
            Children =
            [
                Sidebar.Chevron.Section(() => r.P.Open, size: ClassicTrackRowLayout.ChevronSize, identity: () => r.P.Track.Slot, accentWhenOpen: true),
            ],
        }, () => r.P.Track.HasVersions()), CellKey.Expand);

    // ── span fills (the SpanBuffer twins of BuildLinkSpans / BuildClassicSpans) ──────────────────────────────────────

    static void FillLinkSpans(SpanBuffer b, Track t, int artistCount, (EntityUri Uri, string Name) container, ColorF ink = default)
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

    static void FillClassicSpans(SpanBuffer b, Track t, int artistCount, bool showVideo, ushort weight, ColorF primary, ColorF secondary,
                                 ColorF tertiary)
    {
        b.Add(new TextSpan(t.Title, Weight: weight, Color: primary));
        if (showVideo)
        {
            b.Add(new TextSpan(" "));
            b.Add(new TextSpan(Icons.Movie, Color: tertiary, FontFamily: Tok.Definition.Fonts.Icon));
        }
        if (artistCount > 0) b.Add(new TextSpan("  ·  ", Color: tertiary));
        var slots = t.ArtistSlots;
        for (int i = 0; i < artistCount; i++)
        {
            if (i > 0) b.Add(new TextSpan(", ", Color: secondary));
            var a = new Artist(i < slots.Length ? slots[i] : 0);
            b.Add(new TextSpan(a.Slot > 0 ? a.Name : "", Color: secondary, IsLink: a.Slot > 0 && a.Uri.IsValid));
        }
    }

    // ══ 4. THE SKIN ══════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The bound, shape-stable row container for a TRACK row (from <c>TableHost.Skin</c>): zebra is DISPLAY-index
    /// parity; selection never changes the fill except in Classic (RowHover); the 3×16 pill is the highlight cue and hands
    /// over to the check lane; an .mp4 drag over the row rides the same fill closure instead of an overlay node (ch 04 §0.6).
    /// The core wired the behaviour (<see cref="TrackRowSkin"/>); this draws it. Built by the slot on its rare re-renders
    /// (shape / flow / open), so a recycle only moves the closures' index signal and allocates nothing.</summary>
    static BoxEl Skin(TrackRowSkin a, bool classic)
    {
        bool plain = a.Plain;
        // A quiet (Liked, D16) Modern row is a full-width plate like the stacked flow's, but keeps a 4-DIP corner.
        bool quiet = a.Quiet && !classic && !plain;
        bool flat = plain || quiet;
        var isSel = a.IsSelected;
        var cue = a.Cue;
        var odd = a.Odd;
        var checks = a.ChecksVisible;
        var accent = a.Accent;

        return new BoxEl
        {
            ZStack = true, MinHeight = a.RowHeight, ClipToBounds = true,
            Margin = classic || flat ? default : new Edges4(RowMetrics.RowInset, 0f, RowMetrics.RowInset, 0f),
            // Bottom corners square while THIS row's drawer is open or still closing under it, so the row and its drawer
            // read as one plate.
            Corners = classic || plain ? CornerRadius4.All(0f)
                : quiet ? (a.Squared ? new CornerRadius4(ClassicTrackRowLayout.QuietRowRadius, ClassicTrackRowLayout.QuietRowRadius, 0f, 0f)
                                     : CornerRadius4.All(ClassicTrackRowLayout.QuietRowRadius))
                : a.Squared ? new CornerRadius4(ClassicTrackRowLayout.RowRadius, ClassicTrackRowLayout.RowRadius, 0f, 0f)
                : CornerRadius4.All(ClassicTrackRowLayout.RowRadius),
            Animate = a.Entrance ? ClassicTrackRowLayout.MountEntrance : null,
            DropTarget = a.DropTarget,
            Fill = classic ? Prop.Of(() => cue() || isSel() ? R.Layer.RowHover : ColorF.Transparent)
                : flat ? Prop.Of(() => cue() ? R.Layer.RowHover : ColorF.Transparent)
                : Prop.Of(() => cue() ? R.Layer.RowHover : odd() ? R.Layer.RowZebra : ColorF.Transparent),
            HoverFill = classic || flat ? Prop.Of(static () => R.Layer.RowHover)
                : Prop.Of(() => odd() ? R.Layer.RowHoverZebra : R.Layer.RowHover),
            PressedFill = classic || flat ? Prop.Of(static () => R.Layer.RowPressed)
                : Prop.Of(() => odd() ? R.Layer.RowPressedZebra : R.Layer.RowPressed),
            // No PressScale: a ~1000-DIP row that shrinks 2 % reads as the whole row springing back (ch 01 §0.11).
            // Stationary lift: the row stays at 0.4 while the chip follows the pointer.
            Draggable = a.Draggable,
            BorderWidth = classic || flat ? 0f : 1f,
            BorderColor = classic || flat ? ColorF.Transparent
                : Prop.Of(() => odd() ? Tok.StrokeCardDefault : ColorF.Transparent),
            HoverBorderColor = classic || flat ? ColorF.Transparent : Tok.StrokeCardDefault,
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
                    Direction = 0, Grow = 1f, AlignItems = FlexAlign.Center, Animate = ClassicTrackRowLayout.CheckShift,
                    Children = [a.CheckLane, a.Content],
                },
                new BoxEl
                {
                    Key = "row-pill", Width = ClassicTrackRowLayout.PillWidth, Height = ClassicTrackRowLayout.PillHeight,
                    Margin = new Edges4(ClassicTrackRowLayout.PillLead, 0f, 0f, 0f),
                    Corners = classic ? CornerRadius4.All(0f) : CornerRadius4.All(ClassicTrackRowLayout.PillRadius),
                    Fill = Prop.Of(accent), AlignSelf = FlexAlign.Center,
                    HitTestVisible = false, PressScale = 10f / 16f,
                    Opacity = classic ? (Prop<float>)0f : Prop.Of(() => isSel() && !checks() ? 1f : 0f),
                },
                new BoxEl
                {
                    Key = "row-divider", AlignSelf = FlexAlign.End, JustifySelf = FlexAlign.Stretch, Height = 1f,
                    Fill = classic ? Prop.Of(static () => Tok.StrokeDividerDefault) : (Prop<ColorF>)ColorF.Transparent,
                    HitTestVisible = false,
                },
            ],
        };
    }
}
