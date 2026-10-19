// ── Themes/WinUi/Structure/Lists/WinUiTrackTable.cs — WinUI's track table rows (themes plan 6.2; P6 W3) ───────────────────────────────────
//
// The `TrackListStyle` WinUI registers under `StyleKeys.TrackTable`: the bound grid of lanes and the row's skin (here), and the chrome
// (`WinUiTrackChrome`). Boards: W-RowStates (the row's states, dark and light), W-RowStates-Compact, W-MultiSelect (the check lane and the pill),
// W-Unavailable (the greyed rows that say why), W-Album (an album's numbered one-line rows: Plays, heart, time, "...", versions chevron), W-Playlist and
// W-LikedSongs (a list's two-line rows with a cover), and their -Small / -Compact / -Huge twins.
//
// TWO SKINS, ONE PER `artworkInRows` VALUE (WinUiListRules.ListsFor): ON the MODERN skin, the two-line row (the title over its artists, a 40 cover, 48 tall) with
// an album's rows one line and no cover; OFF the CLASSIC skin, the one-line 40 row with the artist in a lane of its own. The core picks the pair from
// `ThemePolicies.Lists.RowStyle` and re-keys the list on a change (the option is a Transaction).
//
// THE ROW. The lanes are the core's (`Track.ColumnSet`: which exist at this width and tier, the tracks they share); the grid draws them:
//   #        the number (Body 14, tertiary), the accent bars on the playing row (keyed "winui.row.now"), the ring while buffering, the play / pause glyph on
//            hover (none on a withheld row);
//   art      the row's cover at the control radius (r4) where the lane set has a thumb (a list's rows; an album's have none);
//   title    Body 14 (the accent text on the playing row, the marquee on the playing row when the core asks), the explicit "E" and the film glyph after it;
//   subline  Caption 12 in the secondary ink: the linked artists (the Compact form adds " · album", or " · 598.9M plays" on an album: W-Album-Compact);
//   lanes    album, added by, date, plays and time in Body 14 and the secondary ink, a withheld row says "Unavailable" in the time lane (the long reason in its
//            tooltip) and steps back as a whole (45 %);
//   heart    in its own lane before the time (the core's HeartTrailing): a liked row's filled heart at rest in the primary text (the accent on the playing row),
//            an unliked row's outline on hover; a quiet list rests with none;
//   "..."    revealed on hover (and on the focused row); at the Compact form a 40 target on every row.
// THE SKIN (W-RowStates): a full-width r4 backplate, bare at rest; hover the subtle fill 1 (6 %); pressed fill 2; SELECTED = fill 1 plus the 3 x 16 accent pill (and
// selected-and-hovered fill 3); a drop target the 2-px accent line on the top edge (keyed "winui.row.drop", its opacity bound to the cue). The pill also shows
// while the check lane is in (W-MultiSelect keeps both). The playing row has no plate of its own.
//
// WHAT THE CORE KEEPS. `Track.TableHost` and its slot components decide which rows exist and what they hold: the presentation memo, the like edge, the reveal gate
// and the shimmer, the drawer, selection, the drag source, the keyboard and the context menu; the row height (the density, `StyleMetrics.TrackRows`) and the lanes.
// The templates run once per slot per shape, never per bind: every per-item value is a bound Prop over the slot's presentation, so a recycle rebinds and allocates
// nothing. `Stamp` stays 1: no theme option changes either template (the skin pair is picked by the core).
//
// Numbers are `Rules\WinUiListRules.cs` (WVTH003).

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.WinUi;

using BoundRow = global::Wavee.Track.BoundRow;
using CellKey = global::Wavee.Track.CellKey;
using ColumnSet = global::Wavee.Track.ColumnSet;
using Format = global::Wavee.Track.Format;
using RowCellRules = global::Wavee.Track.RowCellRules;
using RowMetrics = global::Wavee.Track.RowMetrics;
using RowPresentation = global::Wavee.Track.RowPresentation;

/// <summary>WinUI's track table: the Modern (two-line) and Classic (one-line) rows, bound grid and skin, and the chrome.</summary>
internal static class WinUiTrackTable
{
    /// <summary>The key of a row skin's drop line, of its selection pill, and of the playing row's bars (a test finds WinUI's rows by them).</summary>
    public const string DropKey = "winui.row.drop", PillKey = "winui.row.pill";

    /// <summary>The style WinUI registers under <c>StyleKeys.TrackTable</c>. Stateless: a table, a drawer and a test share it.</summary>
    public static readonly TrackListStyle Style = new(
        stamp: 1,
        modern: new TrackRowTemplates(ModernGrid, Skin),
        classic: new TrackRowTemplates(OneLineGrid, Skin),
        chrome: WinUiTrackChrome.Templates);

    static Element ModernGrid(BoundRow row, in ColumnSet set, TrackSize[] tracks, float rowH, float art) => Grid(row, in set, tracks, rowH, art, oneLine: false);

    static Element OneLineGrid(BoundRow row, in ColumnSet set, TrackSize[] tracks, float rowH, float art) => Grid(row, in set, tracks, rowH, art, oneLine: true);

    static Prop<ColorF> RowInk(BoundRow r, Func<ColorF> rest) => Prop.Of(() => WinUiListRules.RowInk(r.P.State.IsNow, Tok.AccentTextPrimary, rest()));

    static TextEl Glyph(Prop<string> glyph, float size, Prop<ColorF> color, Prop<bool>? visible = null) => new(glyph)
    {
        FontFamily = Tok.Definition.Fonts.Icon, Size = size, Color = color, Visible = visible ?? true,
    };

    /// <summary>The factual rung (Body 14: the boards' album, date, plays and time), built once with its binds.</summary>
    static TextEl Factual(Prop<string> text, Prop<ColorF> color, bool fill = false, Prop<bool>? visible = null)
    {
        var body = R.Type.DenseTitle("");
        return new TextEl(text)
        {
            Size = body.Size, LineHeight = body.LineHeight, Weight = body.ResolvedWeight, Color = color,
            Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
            Grow = fill ? 1f : 0f, Basis = fill ? 0f : float.NaN,
            Visible = visible ?? true,
        };
    }

    // ══ THE BOUND GRID ═══════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The lanes of <paramref name="set"/>, every per-item value bound to <paramref name="r"/>. Built once per slot per shape.</summary>
    static Element Grid(BoundRow r, in ColumnSet set, TrackSize[] tracks, float rowH, float art, bool oneLine)
    {
        float artSize = art > 0f ? art : RowMetrics.ThumbSize;
        var cells = new Element[Track.CellCount(in set)];
        int i = 0;

        if (!set.IndexHidden) cells[i++] = NumberCell(r);
        if (set.Heart) cells[i++] = Track.RowCenterCell(HeartReveal(r, set.Quiet, set.Compact), CellKey.Heart);
        if (set.Thumb) cells[i++] = Track.RowCenterCell(Artwork(r, artSize), CellKey.Art);
        cells[i++] = TitleCell(r, in set, oneLine);
        if (set.Artist) cells[i++] = Track.RowLeftCell(ArtistLinks(r), CellKey.Artist);
        if (set.Album) cells[i++] = Track.RowLeftCell(AlbumLink(r), CellKey.Album);
        if (set.By) cells[i++] = Track.RowLeftCell(Factual(Prop.Of(() => Track.AddedByLabel(r)), RowInk(r, static () => Tok.TextSecondary),
                                                        fill: true, visible: Prop.Of(() => r.P.AddedBy.Slot > 0)), CellKey.By);
        if (set.Date) cells[i++] = Track.RowLeftCell(Factual(Prop.Of(() => Format.DateAddedLabel(r.P.AddedAt, Track.NowUnix)),
                                                          RowInk(r, static () => Tok.TextSecondary), fill: true), CellKey.Date);
        if (set.Plays) cells[i++] = Track.RowEndCell(PlaysCell(r), CellKey.Plays);
        if (RowMetrics.ShowTempo(in set)) cells[i++] = Track.RowEndCell(TempoCell(r), CellKey.Tempo);
        if (RowMetrics.ShowHeartTrailing(in set)) cells[i++] = Track.RowCenterCell(HeartReveal(r, set.Quiet, set.Compact), CellKey.HeartTrailing);
        if (RowMetrics.ShowDuration(in set)) cells[i++] = Track.RowEndCell(DurationCell(r), CellKey.Duration);
        // The Compact row's "..." is a 40 target shown on every row (W-RowStates-Compact): there is no hover to reveal it.
        if (set.Actions) cells[i++] = set.Compact
            ? Track.MoreCell(true, classic: true, CellKey.More, () => r.Focused, box: WinUiListRules.CompactTarget, atRest: true)
            : Track.MoreCell(true, classic: true, CellKey.More, () => r.Focused);
        if (set.Expand) cells[i++] = ExpandCell(r);

        // The plate is full width: the grid pays only the small inset that puts the "#" 14 in and the art 46 in (the boards' 14 / 48).
        float padX = WinUiListRules.RowPad;
        var grid = new GridEl
        {
            Columns = tracks, ColGap = RowMetrics.ColGapFor(set.Tier), RowHeight = rowH, Grow = 1f,
            Padding = new Edges4(padX, 0f, padX, 0f),
            Children = cells,
        };
        // W-Unavailable / W-RowStates: "disabled text, art at 35 %": the whole row steps back (not just its title column), whether the song is withheld or the
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

    /// <summary>Two shapes under one keyed ZStack cell, swapped by <c>Flow.Show</c> on the rare withheld flip: the reveal shape (the rest layer fades out, the
    /// transport fades in on row hover) and the rest-only shape. A ZStack hands its child the whole lane, so every layer centres in the lane the header's "#" sits
    /// over.</summary>
    static Element NumberCell(BoundRow r)
    {
        Element reveal = new BoxEl
        {
            ZStack = true, MinWidth = 0f, ClipToBounds = true, Grow = 1f, AlignSelf = FlexAlign.Stretch,
            Children =
            [
                RestLayer(r, hoverFade: true),
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
                                              WinUiListRules.TransportGlyph, Prop.Of(() => r.P.State.IsNow ? Tok.AccentTextPrimary : Tok.TextPrimary)),
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
            Children = [RestLayer(r, hoverFade: false)],
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

    /// <summary>The rest layer: the number (with the chart mark) as a <c>Visible</c> flip (WinUI shows the number for the album's top track too: no star), the
    /// accent bars and the buffering ring as real mounts.</summary>
    static Element RestLayer(BoundRow r, bool hoverFade)
    {
        var body = R.Type.DenseTitle("");
        Element number = new BoxEl
        {
            Direction = 0, Gap = WinUiListRules.NumberGap, AlignItems = FlexAlign.Center,
            Visible = Prop.Of(() => RestOf(r) is RowCellRules.Rest.Number or RowCellRules.Rest.Star),
            Children =
            [
                new TextEl(Prop.Of(() => WinUiRowCells.RowNumberLabel(r.P.Display + 1)))
                {
                    Size = body.Size, LineHeight = body.LineHeight, Weight = body.ResolvedWeight,
                    Color = Tok.TextTertiary, MaxLines = 1, Wrap = TextWrap.NoWrap,
                },
                new TextEl(Prop.Of(() => ChartMarkText(RowCellRules.ChartMarkOf(r.P.Chart))))
                {
                    Size = WinUiListRules.ChartMarkSize, LineHeight = WinUiListRules.ChartMarkLine, Weight = 700,
                    Color = Prop.Of(() => RowCellRules.ChartMarkOf(r.P.Chart) == RowCellRules.ChartMark.Down ? Tok.SystemFillCritical : Tok.SystemFillSuccess),
                    Visible = Prop.Of(() => RowCellRules.ChartMarkOf(r.P.Chart) != RowCellRules.ChartMark.None),
                },
            ],
        };
        return new BoxEl
        {
            Grow = 1f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            HoverOpacity = hoverFade ? 0f : float.NaN,
            Children =
            [
                number,
                Flow.Show(() => RestOf(r) == RowCellRules.Rest.Equalizer, new BoxEl
                {
                    Key = WinUiRowCells.NowKey, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Children = [Controls.Equalizer(r.Playing, WinUiRowCells.Accent, Track.RowEqualizerHeight, r.Hovered)],
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

    /// <summary>The heart's row reveal: a liked row keeps its filled heart at rest, an unliked one shows it on row hover; a quiet list (Liked Songs: every row is
    /// liked) rests with none. The focused row shows it to the keyboard.</summary>
    static Element HeartReveal(BoundRow r, bool quiet, bool compact) => new BoxEl
    {
        Grow = 1f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Opacity = quiet ? Prop.Of(() => r.Focused ? 1f : 0f) : Prop.Of(() => r.P.State.Saved || r.Focused ? 1f : 0f), HoverOpacity = 1f,
        Children = [Heart(r, compact)],
    };

    /// <summary>Three glyphs under one target: the resting filled heart (the primary text; the accent on the playing row), the popping one (a real mount on the like
    /// EDGE, so the spring replays once) and the outline.</summary>
    static Element Heart(BoundRow r, bool compact)
    {
        Prop<ColorF> filled = Prop.Of(() => WinUiListRules.HeartInk(r.P.State.IsNow, R.Ink.AccentAction, Tok.TextPrimary));
        return new BoxEl
        {
            Width = RowMetrics.HeartCol, Height = compact ? WinUiListRules.CompactTarget : RowMetrics.HeartCol,   // the Compact row's 40 target
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = CornerRadius4.All(WinUiListRules.RowRadius),
            Cursor = CursorId.Hand, OnClick = r.Like, Role = AutomationRole.Button,
            BlocksDragArm = true,
            Children =
            [
                new BoxEl
                {
                    Visible = Prop.Of(() => r.P.State.Saved && !r.LikePop.Value),
                    Children = [Glyph(Icons.HeartFill, WinUiListRules.HeartGlyph, filled)],
                },
                Flow.Show(() => r.P.State.Saved && r.LikePop.Value,
                    new BoxEl { Animate = WinUiListRules.HeartPop, Children = [Glyph(Icons.HeartFill, WinUiListRules.HeartGlyph, filled)] }),
                new BoxEl
                {
                    Visible = Prop.Of(() => !r.P.State.Saved),
                    Children = [Glyph(Icons.Heart, WinUiListRules.HeartGlyph, Tok.TextPrimary)],
                },
            ],
        }.Interactive(Interaction.Subtle);
    }

    /// <summary>The thumb (the row's own cover, else its album's) at the control radius, over the placeholder graded by the same url.</summary>
    static Element Artwork(BoundRow r, float art)
    {
        const int decodePx = Track.RowArtDecodePx;
        Func<string?> url = () => Track.RowArtUrl(r.P.Track.ForDisplay);
        var corners = CornerRadius4.All(R.Shape.Control);
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

    static Element TitleCell(BoundRow r, in ColumnSet set, bool oneLine)
    {
        bool setArtist = set.Artist, compact = set.Compact;
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
        var film = Glyph(Icons.Movie, WinUiListRules.FilmGlyph, RowInk(r, static () => Tok.TextTertiary), Prop.Of(() => r.P.Track.HasVideo)) with { Shrink = 0f };
        var line = new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = WinUiListRules.ExplicitGap, MinWidth = 0f, ClipToBounds = true,
            Children =
            [
                new BoxEl { Direction = 0, Shrink = 1f, MinWidth = 0f, ClipToBounds = true, Children = [title, marquee] },
                WinUiRowCells.ExplicitMark() with { Visible = Prop.Of(() => r.P.Track.ForDisplay.IsExplicit) },
                film,
            ],
        };
        var column = new BoxEl
        {
            Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = WinUiListRules.TitleGap,
            Children = oneLine ? [line] : [line, Subline(r, setArtist, compact)],
        };
        return new BoxEl
        {
            Key = CellKey.Title, Direction = 0, AlignItems = FlexAlign.Center, MinWidth = 0f, ClipToBounds = true,
            Children = [column],
        };
    }

    /// <summary>The subline: [EPISODE] · the "on this PC" ring · the linked artists as one bound span run that collapses when it has nothing to say. The Compact
    /// form (W-Album-Compact, W-Playlist-Compact, W-MultiSelect-Compact) always names the artists and closes the line with what the lanes dropped: the album on a
    /// list, "598.9M plays" on an album.</summary>
    static Element Subline(BoundRow r, bool setArtist, bool compact)
    {
        var buffer = new SpanBuffer();
        var meta = R.Type.TrackMeta("");
        int ArtistCount(in RowPresentation p) => compact || Track.ArtistInTitle(in p, setArtist) ? p.Track.ForDisplay.ArtistSlots.Length : 0;
        // A podcast row names its show; the Compact list row its album. (The playlist's Small band keeps the artists alone: W-Playlist-Small.)
        bool HasContainer(in RowPresentation p, Track d) => (d.IsPodcast || compact && p.ShowAlbumColumn) && Track.ContainerOf(d).Name.Length > 0;
        string PlaysTail(in RowPresentation p) => compact && !p.ShowAlbumColumn && p.Track.PlayCount > 0
            ? Strings.Detail.Row.PlaysFull(Format.PlaysLabel(p.Track.PlayCount)) : "";
        Prop<ColorF> ink = RowInk(r, static () => Tok.TextSecondary);
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = WinUiListRules.MetaGap, MinWidth = 0f, ClipToBounds = true,
            Visible = Prop.Of(() =>
            {
                var p = r.P;
                return p.State.OnThisPc || compact || Track.ArtistInTitle(in p, setArtist) || p.Track.ForDisplay.IsPodcast;
            }),
            Children =
            [
                new TextEl(Prop.Of(() => Loc.Get(Strings.Detail.Badge.Episode)))
                {
                    Size = R.Type.Eyebrow("").Size, LineHeight = R.Type.Eyebrow("").LineHeight, Weight = 600,
                    Color = Tok.TextTertiary, Shrink = 0f, MaxLines = 1,
                    Visible = Prop.Of(() => r.P.Track.ForDisplay.IsPodcast),
                },
                // The "on this PC" ring leads the subline. A Visible flip, not a mount: an absent mount would still cost the subline's gap.
                WinUiRowCells.OnThisPcMark(ink) with { Visible = Prop.Of(() => r.P.State.OnThisPc) },
                new SpanTextEl(Prop.Of(() =>
                {
                    buffer.Clear();
                    var p = r.P;
                    var d = p.Track.ForDisplay;
                    ColorF spanInk = WinUiListRules.RowInk(p.State.IsNow, Tok.AccentTextPrimary, Tok.TextSecondary);
                    FillLinkSpans(buffer, d, ArtistCount(in p), HasContainer(in p, d) ? Track.ContainerOf(d) : default, PlaysTail(in p), spanInk);
                    return buffer.Current;
                }))
                {
                    Size = meta.Size, LineHeight = meta.LineHeight, Color = Tok.TextSecondary,   // SpanTextEl.Color is a value: the spans carry the live ink
                    Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MaxLines = 1,
                    Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f,
                    Visible = Prop.Of(() =>
                    {
                        var p = r.P;
                        return RowCellRules.LinkSpanCount(ArtistCount(in p), HasContainer(in p, p.Track.ForDisplay)) > 0 || PlaysTail(in p).Length > 0;
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
            ],
        };
    }

    // ── the factual lanes ────────────────────────────────────────────────────────────────────────────────────────────────

    static Element ArtistLinks(BoundRow r)
    {
        var buffer = new SpanBuffer();
        var body = R.Type.DenseTitle("");
        return new SpanTextEl(Prop.Of(() =>
        {
            buffer.Clear();
            var p = r.P;
            var d = p.Track.ForDisplay;
            FillLinkSpans(buffer, d, d.ArtistSlots.Length, default, "", WinUiListRules.RowInk(p.State.IsNow, Tok.AccentTextPrimary, Tok.TextSecondary));
            return buffer.Current;
        }))
        {
            Size = body.Size, LineHeight = body.LineHeight, Color = Tok.TextSecondary,
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

    /// <summary>The album lane as one link (an episode row's show); a name-less ref states the absence with the em dash and stays clickable whenever a uri exists.</summary>
    static Element AlbumLink(BoundRow r)
    {
        var buffer = new SpanBuffer();
        var body = R.Type.DenseTitle("");
        return new SpanTextEl(Prop.Of(() =>
        {
            buffer.Clear();
            var p = r.P;
            var c = Track.ContainerOf(p.Track.ForDisplay);
            ColorF ink = c.Name.Length == 0 && !p.State.IsNow ? Tok.TextTertiary
                : WinUiListRules.RowInk(p.State.IsNow, Tok.AccentTextPrimary, Tok.TextSecondary);
            buffer.Add(new TextSpan(c.Name.Length > 0 ? c.Name : Format.Dash, Color: ink, IsLink: c.Uri.IsValid));
            return buffer.Current;
        }))
        {
            Size = body.Size, LineHeight = body.LineHeight, Color = Tok.TextSecondary,
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
            RowInk(r, static () => Tok.TextSecondary));

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
                    Width = WinUiListRules.TempoDot, Height = WinUiListRules.TempoDot, Opacity = WinUiListRules.TempoDotOpacity, AlignSelf = FlexAlign.Center,
                    Corners = CornerRadius4.All(1.5f),
                    Fill = Prop.Of(() => R.Ink.DataDot(r.P.Track.CamelotColor)),
                    Visible = Prop.Of(() => r.P.Track.CamelotColor != 0),
                },
                Factual(Prop.Of(() => Track.s_tempoLabels.Get(r.P.Track.Tempo, Track.s_tempoFormat)), RowInk(r, static () => Tok.TextSecondary)),
                Factual("·", RowInk(r, static () => Tok.TextTertiary), visible: hasKey),
                Factual(Prop.Of(() => KeyOf(r.P.Track) ?? ""), RowInk(r, static () => Tok.TextTertiary), visible: hasKey),
            ],
        };
    }

    /// <summary>The time, or for a withheld row its short reason ("Unavailable", the release date) at the reason rung with the long reason as its tooltip (a real
    /// mount on the rare withheld flip, so a playable row carries no tooltip host). W-Unavailable: "This track isn't available in your country".</summary>
    static Element DurationCell(BoundRow r)
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
            Size = WinUiListRules.ReasonSize, LineHeight = R.Type.DenseMeta("").LineHeight,
            Color = Tok.TextTertiary, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
        };
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.End, MinWidth = 0f,
            Children =
            [
                clock,
                Flow.Show(withheld, ToolTip.Wrap(reason, Prop.Of<string?>(() => Track.WithheldReason(r.P.Track, Track.NowUnix) ?? Loc.Get(Strings.Track.NotOnThisPcReason)))),
            ],
        };
    }

    /// <summary>The versions chevron (only a row with versions shows it), rotating open with the drawer, accent while open.</summary>
    static Element ExpandCell(BoundRow r)
        => Track.RowCenterCell(Track.Affordance(r, () => new BoxEl
        {
            Width = Spacing.XXL, Height = Spacing.XXL, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = Radii.ControlAll, HoverFill = Prop.Of(static () => R.Layer.RowHover),
            Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
            FocusVisualMargin = Edges4.All(1f),
            OnClick = r.ToggleExpand,
            BlocksDragArm = true,
            Visible = Prop.Of(() => r.P.Track.HasVersions()),
            Children =
            [
                Sidebar.Chevron.Section(() => r.P.Open, size: WinUiListRules.ChevronGlyph, identity: () => r.P.Track.Slot, accentWhenOpen: true),
            ],
        }, () => r.P.Track.HasVersions()), CellKey.Expand);

    /// <summary>The artists, the container and a plain tail as one span run (separators and the tail are not links).</summary>
    static void FillLinkSpans(SpanBuffer b, Track t, int artistCount, (EntityUri Uri, string Name) container, string tail, ColorF ink)
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
            if (artistCount > 0) b.Add(new TextSpan(WinUiListRules.InlineLead, Color: ink));
            b.Add(new TextSpan(container.Name, Color: ink, IsLink: container.Uri.IsValid));
        }
        if (tail.Length > 0)
        {
            if (artistCount > 0 || container.Name is { Length: > 0 }) b.Add(new TextSpan(WinUiListRules.InlineLead, Color: ink));
            b.Add(new TextSpan(tail, Color: ink));
        }
    }

    // ══ THE SKIN ═════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The check lane steps back with the row (W-RowStates: the unavailable row's check square dims too). The content dims itself (<see cref="Grid"/>).</summary>
    static Element DimmedLane(Element lane, Func<bool> dimmed) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0f,
        Opacity = Prop.Of(() => dimmed() ? Track.RowNotYetOutOpacity : 1f),
        Children = [lane],
    };

    /// <summary>The row's container (W-RowStates): a full-width r4 backplate, bare at rest, fill 1 on hover, fill 2 pressed; SELECTED = fill 1 + the 3 x 16 accent
    /// pill (selected and hovered, fill 3); an .mp4 dragged over the row rides the selected fill with the accent line on its top edge. The pill stays while the
    /// check lane is in. Applies every wired member of <paramref name="a"/>.</summary>
    static BoxEl Skin(TrackRowSkin a)
    {
        var isSel = a.IsSelected;
        var cue = a.Cue;
        // Bottom corners square while THIS row's drawer is open or still closing under it, so the row and its drawer read as one plate.
        var corners = a.Squared
            ? new CornerRadius4(WinUiListRules.RowRadius, WinUiListRules.RowRadius, 0f, 0f)
            : CornerRadius4.All(WinUiListRules.RowRadius);
        return new BoxEl
        {
            ZStack = true, MinHeight = a.RowHeight, ClipToBounds = true, Corners = corners,
            Animate = a.Entrance ? WinUiListRules.MountEntrance : null,
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
                    Direction = 0, Grow = 1f, AlignItems = FlexAlign.Center, Animate = WinUiListRules.CheckShift,
                    Children = [a.Dimmed is { } dim ? DimmedLane(a.CheckLane, dim) : a.CheckLane, a.Content],
                },
                new BoxEl
                {
                    Key = PillKey, Width = WinUiListRules.PillWidth, Height = WinUiListRules.PillHeight, AlignSelf = FlexAlign.Center,
                    Corners = CornerRadius4.All(WinUiListRules.PillRadius), Fill = Prop.Of(static () => R.Ink.AccentSelection),
                    HitTestVisible = false, PressScale = 10f / 16f,
                    Opacity = Prop.Of(() => isSel() ? 1f : 0f),
                },
                new BoxEl
                {
                    Key = DropKey, AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Stretch, Height = WinUiListRules.DropLine,
                    Fill = Prop.Of(static () => R.Ink.AccentAction), Opacity = Prop.Of(() => cue() ? 1f : 0f),
                    HitTestVisible = false,
                },
            ],
        };
    }
}
