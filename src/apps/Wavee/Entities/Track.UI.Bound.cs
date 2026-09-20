// ── Entities/Track.UI.Bound.cs ─────────────────────────────────────────────────────────────────────────────────────
// the table's BOUND row grid: the same lanes as `Grid`, built ONCE per slot. Every per-item value is a bound Prop over
// the slot's presentation memo (fluentgpu rule 13), frequent flips are `Visible` binds and the rare, heavy branches
// (spinner, equalizer, marquee, the withheld # cell) are `Flow.Show`. A recycle rebinds; it never rebuilds an element.
// `Grid` stays the one-shot row for the eager rows and the drawers.
//
// Role: UI
// Owner: M
// Wave: 4.5
// Budget: 700 lines
// Spec: ch 01 §9

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

public readonly partial struct Track
{
    // ══ 1. THE PURE DECISIONS (Wavee.Tests/TrackRowCellRulesTests.cs) ═══════════════════════════════════════════════

    /// <summary>What the bound cells decide per item, engine-free: the # cell's rest state and its hover reveal, the
    /// chart mark, the duration lane's arm, the metadata line's presence and the span-index → link target maps.</summary>
    public static class RowCellRules
    {
        public enum Rest : byte { Empty, Spinner, Volume, Equalizer, Star, Number }
        public enum ChartMark : byte { None, Up, Down, New }
        public enum DurationKind : byte { Clock, Unavailable, ReleaseDate, Dash }

        /// <summary>A span click that resolves to the container (album / show) link.</summary>
        public const int Container = -1;
        /// <summary>A span click that resolves to nothing (a separator, a plain-text name, a lane with no album).</summary>
        public const int None = -2;

        /// <summary>The # cell at rest (ch 01 §0.2): buffering ring first; Classic states now-playing with the Volume mark
        /// and nothing else; Modern shows the equalizer, the album's top-track star, else the number.</summary>
        public static Rest RestOf(in RowState st, bool classic)
        {
            if (st.IsBuffering) return Rest.Spinner;
            if (classic) return st.IsNow ? Rest.Volume : Rest.Empty;
            if (st.IsNow) return Rest.Equalizer;
            if (st.IsTop) return Rest.Star;
            return Rest.Number;
        }

        /// <summary>Does row hover reveal the transport? Never on a withheld row (not out / ruled unavailable, ch 01 W6),
        /// except that a buffering ring still shows.</summary>
        public static bool Reveal(in RowState st, bool withheld) => st.IsBuffering || !withheld;

        /// <summary>The chart byte as <c>Spotify.Decode.ChartStatus</c> writes it: 2 Up · 3 Down · 4 New; Equal and Unknown
        /// draw nothing (ch 01 W5).</summary>
        public static ChartMark ChartMarkOf(byte status) => status switch
        {
            2 => ChartMark.Up,
            3 => ChartMark.Down,
            4 => ChartMark.New,
            _ => ChartMark.None,
        };

        /// <summary>The duration lane's arm. Unplayable is tested FIRST: with no release instant the not-yet-out predicate
        /// also holds, and it would print a dash where the verdict has a word.</summary>
        public static DurationKind DurationKindOf(bool unplayable, bool notYetOut, int availableAt, long nowUnixSeconds)
            => unplayable ? DurationKind.Unavailable
                : notYetOut ? (availableAt > nowUnixSeconds ? DurationKind.ReleaseDate : DurationKind.Dash)
                : DurationKind.Clock;

        /// <summary>The Modern metadata subline is present when it has anything to say.</summary>
        public static bool ShowMeta(bool classic, bool artistInTitle, bool showAlbumInMeta, bool badge)
            => !classic && (artistInTitle || showAlbumInMeta || badge);

        /// <summary>Span count of a link run: [artist0 ", " artist1 …] [" · " container].</summary>
        public static int LinkSpanCount(int artistCount, bool container)
        {
            int artistSpans = artistCount == 0 ? 0 : artistCount * 2 - 1;
            return artistSpans + (container ? (artistSpans > 0 ? 2 : 1) : 0);
        }

        /// <summary>A click on span <paramref name="spanIndex"/> of a link run: artist i sits at span 2i; the container is
        /// the LAST span (only when <paramref name="album"/>). Separators resolve to <see cref="None"/>.</summary>
        public static int LinkTarget(int spanIndex, int artistCount, bool album)
        {
            int artistSpans = artistCount == 0 ? 0 : artistCount * 2 - 1;
            if (spanIndex < artistSpans) return (spanIndex & 1) == 0 ? spanIndex / 2 : None;
            if (!album) return None;
            // FillLinkSpans: [artists…] then " · " (only when artists precede) then the container.
            int containerIndex = artistCount == 0 ? 0 : artistSpans + 1;
            return spanIndex == containerIndex ? Container : None;
        }

        /// <summary>A click on a Classic folded title run — title 600 · [" " film] · "  ·  " · linked artists: the artist
        /// index, or <see cref="None"/> for the head, the glyph and every separator.</summary>
        public static int ClassicArtistOf(int spanIndex, bool showVideo, int artistCount)
        {
            int first = 1 + (showVideo ? 2 : 0) + 1;
            int rel = spanIndex - first;
            return artistCount > 0 && rel >= 0 && (rel & 1) == 0 ? rel / 2 : None;
        }
    }

    // ══ 2. THE SLOT'S INPUTS ═════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>One slot's bound inputs, created once at mount: the presentation memo every cell reads, the like-edge
    /// memo, the row hover signal, the page accent and the three handlers (each resolves the current item itself).</summary>
    sealed class BoundRow(IReadSignal<TableHost.RowPresentation> presentation, IReadSignal<bool> likePop,
                          IReadSignal<bool> hovered, Func<ColorF> accent, Action play, Action like, Action toggleExpand)
    {
        public readonly IReadSignal<TableHost.RowPresentation> Presentation = presentation;
        public readonly IReadSignal<bool> LikePop = likePop;
        public readonly IReadSignal<bool> Hovered = hovered;
        public readonly Func<ColorF> Accent = accent;
        public readonly Action Play = play, Like = like, ToggleExpand = toggleExpand;

        /// <summary>The current presentation — a subscribing read, for bind thunks only.</summary>
        public TableHost.RowPresentation P => Presentation.Value;
    }

    static long NowUnix => Store.ToUnix(Entities.Now);

    /// <summary>The ONE not-yet-out / unavailable predicate: the dim, the play gate and the duration lane never disagree.</summary>
    static bool Withheld(in TableHost.RowPresentation p, long now) => p.Track.NotYetOut(now) || p.Track.Unplayable();

    static bool ArtistInTitle(in TableHost.RowPresentation p, bool setArtist) => p.ShowTrackArtist && !setArtist;

    static bool ShowAlbumInMeta(in TableHost.RowPresentation p, bool classic, bool setAlbum)
        => !classic && p.ShowAlbumColumn && !setAlbum;

    static bool Badge(in TableHost.RowPresentation p) => p.ShowAlbumColumn && p.Track.ForDisplay.IsExplicit;

    // The Tempo figure is the one row format `Track.Format` did not already cache. (The Added-by id cache went with
    // the id fallback itself, below.)
    static readonly FormatCache<ushort> s_tempoLabels = FormatCache.Create<ushort>();
    static readonly Func<ushort, string> s_tempoFormat = Format.TempoLabel;

    /// <summary>The Added-by name when identity is known, and "" until it is - never the raw membership id, which
    /// reads as a real name when it is only a guess. The twin of `Track.UI.AddedByCell` and of the deleted
    /// `Playlist.NameOf` fallback.</summary>
    static string AddedByLabel(User by)
    {
        if (by.Slot <= 0) return "";
        return by.Knows(UserFields.Identity) ? Entities.Strings.Resolve(by.NameId) : "";
    }

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
        FontFamily = Theme.IconFont, Size = size, Color = color, Visible = visible ?? true,
    };

    // ══ 3. THE BOUND GRID ════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The bound twin of <see cref="Grid"/>: same lanes, same keys, same wrappers, every per-item value bound.
    /// Built once per slot per shape; the shape (column set, tracks, row height, art) is the only thing read here.</summary>
    static Element BoundGrid(BoundRow r, in ColumnSet set, TrackSize[] tracks, float rowH, float art)
    {
        bool classic = set.Classic;
        float artSize = art > 0f ? art : RowMetrics.ThumbSize;
        var cells = new Element[CellCount(in set)];
        int i = 0;

        cells[i++] = BoundNumberCell(r, classic);
        if (set.Heart) cells[i++] = RowCenterCell(BoundHeart(r, classic), CellKey.Heart);
        if (set.Thumb) cells[i++] = RowCenterCell(BoundArtwork(r, artSize), CellKey.Art);
        cells[i++] = BoundTitleCell(r, in set);
        if (set.Artist) cells[i++] = RowLeftCell(BoundArtistLinks(r, classic), CellKey.Artist);
        if (set.Album) cells[i++] = RowLeftCell(BoundAlbumLink(r, classic), CellKey.Album);
        if (set.By) cells[i++] = BoundAddedByCell(r, classic);
        if (set.Date) cells[i++] = RowLeftCell(BoundDateCell(r, classic), CellKey.Date);
        if (set.Plays) cells[i++] = RowEndCell(BoundPlaysCell(r, classic), CellKey.Plays);
        if (RowMetrics.ShowTempo(in set)) cells[i++] = RowEndCell(BoundTempoCell(r, classic), CellKey.Tempo);
        cells[i++] = RowEndCell(BoundDurationCell(r, classic), CellKey.Duration);
        if (set.Video) cells[i++] = RowCenterCell(BoundVideoMoreCell(r), CellKey.Video);
        if (set.Actions) cells[i++] = MoreCell(true, classic, CellKey.More);
        if (set.Expand) cells[i++] = BoundExpandCell(r);

        float padX = RowMetrics.PadXFor(set.Tier);
        float inner = classic ? padX : padX - RowMetrics.RowInset;
        return new GridEl
        {
            Columns = tracks, ColGap = RowMetrics.ColGapFor(set.Tier), RowHeight = rowH, Grow = 1f,
            Padding = new Edges4(inner, 0f, inner, 0f),
            Children = cells,
        };
    }

    // ── the # cell ───────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Two shapes under one keyed cell, swapped by <c>Flow.Show</c> on the rare withheld flip: the reveal shape
    /// (rest layer fading out, the transport fading in on row hover) and the rest-only shape (no hover reveal).</summary>
    static Element BoundNumberCell(BoundRow r, bool classic)
    {
        Element reveal = new BoxEl
        {
            ZStack = true, MinWidth = 0f, ClipToBounds = true, Grow = 1f, AlignSelf = FlexAlign.Stretch,
            Children =
            [
                BoundRestLayer(r, classic, hoverFade: true),
                new BoxEl
                {
                    Direction = 0, Grow = 1f, AlignItems = FlexAlign.Stretch, Justify = FlexJustify.Center,
                    Opacity = 0f, HoverOpacity = 1f,
                    Children =
                    [
                        new BoxEl
                        {
                            Grow = 1f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                            OnClick = r.Play, Cursor = CursorId.Hand, Role = AutomationRole.Button, BlocksDragArm = true,
                            Children =
                            [
                                Flow.Show(() => r.P.State.IsBuffering, Spinner()),
                                new BoxEl
                                {
                                    Width = RowTransportBox, Height = RowTransportBox,
                                    AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                                    PressScale = Design.Motion.ScaleEmphatic.Press,
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
                },
            ],
        };
        Element restOnly = new BoxEl
        {
            ZStack = true, MinWidth = 0f, ClipToBounds = true, Grow = 1f, AlignSelf = FlexAlign.Stretch,
            Children = [BoundRestLayer(r, classic, hoverFade: false)],
        };
        return new BoxEl
        {
            Key = CellKey.Num, Direction = 0, MinWidth = 0f, ClipToBounds = true,
            Children =
            [
                Flow.Show(() =>
                {
                    var p = r.P;
                    var st = p.State;
                    return RowCellRules.Reveal(in st, Withheld(in p, NowUnix));
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
                Flow.Show(() => RestOf(r, true) == RowCellRules.Rest.Spinner, Spinner()),
            ];
        else
            kids =
            [
                new BoxEl
                {
                    Direction = 0, Gap = 2f, AlignItems = FlexAlign.Center,
                    Visible = Prop.Of(() => RestOf(r, false) == RowCellRules.Rest.Number),
                    Children =
                    [
                        new TextEl(Prop.Of(() => RowNumberLabel(r.P.Display + 1))) { Size = 12f, LineHeight = 16f, Color = Tok.TextTertiary },
                        new TextEl(Prop.Of(() => ChartMarkText(RowCellRules.ChartMarkOf(r.P.Chart))))
                        {
                            Size = 8f, LineHeight = 12f, Weight = 700,
                            Color = Prop.Of(() => RowCellRules.ChartMarkOf(r.P.Chart) == RowCellRules.ChartMark.Down
                                ? Tok.SystemFillCritical : Tok.SystemFillSuccess),
                            Visible = Prop.Of(() => RowCellRules.ChartMarkOf(r.P.Chart) != RowCellRules.ChartMark.None),
                        },
                    ],
                },
                Glyph(Icons.FavoriteStarFill, 11f, Tok.AccentTextPrimary, Prop.Of(() => RestOf(r, false) == RowCellRules.Rest.Star)),
                Flow.Show(() => RestOf(r, false) == RowCellRules.Rest.Equalizer,
                    Controls.Equalizer(Playback.IsPlaying, r.Accent, RowEqualizerHeight, r.Hovered)),
                Flow.Show(() => RestOf(r, false) == RowCellRules.Rest.Spinner, Spinner()),
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
        RowCellRules.ChartMark.New => Loc.Get(LocChartNew),
        _ => "",
    };

    // ── heart · art ──────────────────────────────────────────────────────────────────────────────────────────────────

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
                    new BoxEl { Animate = s_heartPopIn, Children = [Icon(Icons.HeartFill, 14f, onInk)] }),
                new BoxEl
                {
                    Visible = Prop.Of(() => !r.P.State.Saved),
                    Children = [Icon(Icons.Heart, 14f, offInk)],
                },
            ],
        }.Interactive(Interaction.Subtle);
    }

    /// <summary>The thumb as <c>Controls.Artwork</c> lays it out (a square decode at the ONE shared
    /// <see cref="RowArtDecodePx"/> — see its doc for why it is not <c>art * 2</c> — with the small-edge placeholder tile
    /// beneath), with the url bound: the row's own cover, or its album's when it has none (<see cref="RowArtUrl"/>). The
    /// tint below reads the SAME url, so a fallback cover is graded by its own palette and not left neutral.</summary>
    static Element BoundArtwork(BoundRow r, float art)
    {
        const int decodePx = RowArtDecodePx;
        Func<string?> url = () => RowArtUrl(r.P.Track.ForDisplay);
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
    /// real mount so the marquee host only exists on that one row.</summary>
    static Element[] BoundTitleRuns(BoundRow r)
    {
        // Design.Type.TrackTitle's rung (BodyStrong 14/20/600), constructed once with its binds.
        var plain = new TextEl(Prop.Of(() => r.P.Track.ForDisplay.Title))
        {
            Size = 14f, LineHeight = 20f, Weight = 600,
            Color = Prop.Of(() => r.P.State.IsNow ? Tok.AccentTextPrimary : Tok.TextPrimary),
            Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
            Visible = Prop.Of(() => !(r.P.State.IsNow && r.P.Marquee)),
        };
        var marquee = Flow.Show(() => r.P.State.IsNow && r.P.Marquee,
            Marquee.Of(Prop.Of(() => r.P.Track.ForDisplay.Title), s_rowMarqueeStyle));
        return [plain, marquee];
    }

    static Element BoundTitleCell(BoundRow r, in ColumnSet set)
    {
        bool classic = set.Classic;
        bool setArtist = set.Artist, setAlbum = set.Album;
        int tier = set.Tier;
        var runs = BoundTitleRuns(r);
        Element[] column;
        if (classic) column = [BoundClassicTitleLine(r, runs, setArtist, tier)];
        else column = [runs[0], runs[1], BoundMetadataLine(r, setArtist, setAlbum)];
        var titleCol = new BoxEl
        {
            Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = Spacing.XXS,
            Opacity = Prop.Of(() =>
            {
                var p = r.P;
                return Withheld(in p, NowUnix) ? RowNotYetOutOpacity : 1f;
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
    /// collapses when it has nothing to say.</summary>
    static Element BoundMetadataLine(BoundRow r, bool setArtist, bool setAlbum)
    {
        var buffer = new SpanBuffer();
        int ArtistCount(in TableHost.RowPresentation p) => ArtistInTitle(in p, setArtist) ? p.Track.ForDisplay.ArtistSlots.Length : 0;
        bool HasContainer(in TableHost.RowPresentation p, Track d)
            => (ShowAlbumInMeta(in p, false, setAlbum) || d.IsPodcast) && ContainerOf(d).Name.Length > 0;
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = 4f, MinWidth = 0f, ClipToBounds = true,
            Visible = Prop.Of(() =>
            {
                var p = r.P;
                return RowCellRules.ShowMeta(false, ArtistInTitle(in p, setArtist), ShowAlbumInMeta(in p, false, setAlbum), Badge(in p));
            }),
            Children =
            [
                // Design.Type.Eyebrow's rung (Caption 12/16, 600, tracked) and Controls.ExplicitBadge(14), each built once.
                new TextEl(Prop.Of(() => Loc.Get(Strings.Detail.Badge.Episode)))
                {
                    Size = 12f, LineHeight = 16f, Weight = 600, CharSpacing = Design.Type.EyebrowTracking,
                    Color = Tok.TextTertiary, Shrink = 0f, MaxLines = 1,
                    Visible = Prop.Of(() => r.P.Track.ForDisplay.IsPodcast),
                },
                new BoxEl
                {
                    Width = 14f, Height = 14f, Shrink = 0f,
                    AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Corners = CornerRadius4.All(2f), Fill = Tok.TextTertiary,
                    Visible = Prop.Of(() => Badge(r.P)),
                    Children = [new TextEl("E") { Size = 14f * 0.68f, Weight = 700, Color = Tok.TextInverse }],
                },
                new SpanTextEl(Prop.Of(() =>
                {
                    buffer.Clear();
                    var p = r.P;
                    var d = p.Track.ForDisplay;
                    FillLinkSpans(buffer, d, ArtistCount(in p), HasContainer(in p, d) ? ContainerOf(d) : default);
                    return buffer.Current;
                }))
                {
                    Size = 12f, LineHeight = 16f, Color = Tok.TextSecondary,
                    Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MaxLines = 1,
                    Grow = 1f, Basis = 0f, MinWidth = 0f,
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
                        if (target >= 0) GoToArtistAt(d, target);
                        else if (target == RowCellRules.Container) GoToContainer(d);
                    },
                },
            ],
        };
    }

    /// <summary>Classic's one-line title (ch 01 W8): the folded span run (artists in the title) or the plain title plus
    /// the film glyph, then the EXPLICIT word-mark pinned trailing. Both arms stay mounted; the fold flag picks one.</summary>
    static Element BoundClassicTitleLine(BoundRow r, Element[] runs, bool setArtist, int tier)
    {
        var buffer = new SpanBuffer();
        bool ShowVideo(in TableHost.RowPresentation p) => TableRules.ShowClassicInlineVideo(true, p.Track.HasVideo, tier);
        Prop<ColorF> Ink(Func<ColorF> plain) => Prop.Of(() => r.P.State.IsNow ? Tok.AccentTextPrimary : plain());

        Element folded = new SpanTextEl(Prop.Of(() =>
        {
            buffer.Clear();
            var p = r.P;
            bool now = p.State.IsNow;
            FillClassicSpans(buffer, p.Track.ForDisplay, p.Track.ForDisplay.ArtistSlots.Length, ShowVideo(in p),
                now ? Tok.AccentTextPrimary : Tok.TextPrimary, now ? Tok.AccentTextPrimary : Tok.TextSecondary,
                now ? Tok.AccentTextPrimary : Tok.TextTertiary);
            return buffer.Current;
        }))
        {
            Size = 14f, LineHeight = 20f, Color = Tok.TextPrimary,   // SpanTextEl.Color is a value: the spans carry the live inks
            Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MaxLines = 1,
            Shrink = 1f, MinWidth = 0f,
            Visible = Prop.Of(() => ArtistInTitle(r.P, setArtist)),
            OnSpanClick = index =>
            {
                var p = r.Presentation.Peek();
                var d = p.Track.ForDisplay;
                int artist = RowCellRules.ClassicArtistOf(index, ShowVideo(in p), d.ArtistSlots.Length);
                if (artist >= 0) GoToArtistAt(d, artist);
            },
        };
        Element unfolded = new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.XS,
            Grow = 1f, Basis = 0f, MinWidth = 0f, ClipToBounds = true,
            Visible = Prop.Of(() => !ArtistInTitle(r.P, setArtist)),
            Children =
            [
                new BoxEl { Grow = 1f, Basis = 0f, MinWidth = 0f, ClipToBounds = true, Children = runs },
                new TextEl(Icons.Movie)
                {
                    FontFamily = Theme.IconFont, Size = 12f, Shrink = 0f, Color = Ink(static () => Tok.TextTertiary),
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
            Height = 14f, Padding = new Edges4(Spacing.XXS, 0f, Spacing.XXS, 0f), Shrink = 0f,
            Corners = CornerRadius4.All(2f), BorderWidth = 1f, BorderColor = Ink(static () => Tok.TextTertiary),
            Opacity = Prop.Of(() => r.P.State.IsNow ? 1f : 0.6f),
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Visible = Prop.Of(() => Badge(r.P)),
            Children =
            [
                new TextEl(Prop.Of(() => Loc.Get(Strings.Detail.Badge.Explicit)))
                {
                    Size = 9f, LineHeight = 12f, Weight = 600, Color = Ink(static () => Tok.TextTertiary),
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
                if (target >= 0) GoToArtistAt(d, target);
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
            var c = ContainerOf(p.Track.ForDisplay);
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
                if (ContainerOf(d).Uri.IsValid) GoToContainer(d);
            },
        };
    }

    /// <summary>The Added-by lane (ch 01 §3): avatar + name (Modern), the bare name (Classic); empty for no adder.</summary>
    static Element BoundAddedByCell(BoundRow r, bool classic)
    {
        var label = Prop.Of(() => AddedByLabel(r.P.AddedBy));
        var ink = LaneInk(r, classic, static () => Tok.TextSecondary);
        var present = Prop.Of(() => r.P.AddedBy.Slot > 0);
        if (classic)
            return RowLeftCell(FactualFill(true, label, ink, present), CellKey.By);
        return new BoxEl
        {
            Key = CellKey.By, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Start, Gap = Spacing.S,
            MinWidth = 0f, ClipToBounds = true,
            Children =
            [
                PersonPicture.Bound(label, Prop.Of(() => Controls.ArtUrl(r.P.AddedBy.ImageId) ?? ""), Spacing.XXL) with { Visible = present },
                new TextEl(label)
                {
                    Size = 12f, LineHeight = 16f, Color = ink, Grow = 1f, Basis = 0f, MinWidth = 0f, MaxLines = 1,
                    Trim = TextTrim.CharacterEllipsis, Visible = present,
                },
            ],
        };
    }

    static Element BoundDateCell(BoundRow r, bool classic)
        => FactualFill(classic, Prop.Of(() => Format.DateAddedLabel(r.P.AddedAt, NowUnix)),
                       LaneInk(r, classic, static () => Tok.TextSecondary));

    /// <summary>A count of 0 is "not known yet", and a withheld row states the absence rather than a real-looking number.</summary>
    static Element BoundPlaysCell(BoundRow r, bool classic)
        => Factual(classic, Prop.Of(() =>
            {
                var p = r.P;
                return Withheld(in p, NowUnix) || p.Track.PlayCount == 0 ? Format.Dash : Format.PlaysLabel(p.Track.PlayCount);
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
                    Width = 6f, Height = 6f, Corners = CornerRadius4.All(1.5f), Opacity = 0.85f, AlignSelf = FlexAlign.Center,
                    Fill = Prop.Of(() => Design.Palette.DataDotInk(r.P.Track.CamelotColor, Tok.Theme)),
                    Visible = Prop.Of(() => r.P.Track.CamelotColor != 0),
                },
                Factual(classic, Prop.Of(() => s_tempoLabels.Get(r.P.Track.Tempo, s_tempoFormat)), primary),
                Factual(classic, "·", secondary, hasKey),
                Factual(classic, Prop.Of(() => KeyOf(r.P.Track) ?? ""), secondary, hasKey),
            ],
        };
    }

    /// <summary>"Unavailable" for a ruled-unavailable row, the release date for a pending one, else the clock.</summary>
    static Element BoundDurationCell(BoundRow r, bool classic)
        => Factual(classic,
            Prop.Of(() =>
            {
                var t = r.P.Track;
                long now = NowUnix;
                return RowCellRules.DurationKindOf(t.Unplayable(), t.NotYetOut(now), t.AvailableAt, now) switch
                {
                    RowCellRules.DurationKind.Unavailable => Loc.Get(Strings.Detail.TrackFacts.Unavailable),
                    RowCellRules.DurationKind.ReleaseDate => Format.ReleaseDateLabel(t.AvailableAt, now),
                    RowCellRules.DurationKind.Dash => Format.Dash,
                    _ => Format.DurationCell(t.ForDisplay.DurationMs),
                };
            }),
            Prop.Of(() =>
            {
                var p = r.P;
                if (classic && p.State.IsNow) return Tok.AccentTextPrimary;
                return Withheld(in p, NowUnix) ? Tok.TextTertiary : Tok.TextSecondary;
            }));

    // ── trailing chrome ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary><see cref="VideoMoreCell"/> bound: the film glyph at rest on a row with a video, the quiet "…" on one
    /// without; the full-strength "…" on row hover either way.</summary>
    static Element BoundVideoMoreCell(BoundRow r)
        => new BoxEl
        {
            ZStack = true, MinWidth = 0f,
            Children =
            [
                new BoxEl
                {
                    Grow = 1f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, HoverOpacity = 0f,
                    Children = [Glyph(Icons.Movie, 13f, Tok.TextTertiary, Prop.Of(() => r.P.Track.HasVideo))],
                },
                new BoxEl
                {
                    Direction = 0, Grow = 1f, AlignItems = FlexAlign.Stretch,
                    Opacity = Prop.Of(() => r.P.Track.HasVideo ? 0f : Controls.MoreRestOpacity),
                    HoverOpacity = 1f,
                    Children =
                    [
                        new BoxEl
                        {
                            Grow = 1f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                            Cursor = CursorId.Hand, ClickRequestsContext = true, HitTestVisible = true,
                            Role = AutomationRole.Button, BlocksDragArm = true,
                            Children = [Icon(Icons.More, 16f, Tok.TextSecondary)],
                        },
                    ],
                },
            ],
        };

    /// <summary><see cref="ExpandCell"/> bound: the glyph swaps ChevronRight secondary ↔ ChevronDown accent with the open
    /// state; an episode row (no versions) collapses the chevron.</summary>
    static Element BoundExpandCell(BoundRow r)
        => RowCenterCell(new BoxEl
        {
            Width = Spacing.XXL, Height = Spacing.XXL, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = Radii.ControlAll, HoverFill = Tok.FillControlSecondary,
            Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
            FocusVisualMargin = Edges4.All(1f),
            OnClick = r.ToggleExpand,
            BlocksDragArm = true,
            Visible = Prop.Of(() => r.P.Track.Uri.Kind == EntityKind.Track),
            Children =
            [
                Glyph(Prop.Of(() => r.P.Open ? Icons.ChevronDown : Icons.ChevronRight), 12f,
                      Prop.Of(() => r.P.Open ? Tok.AccentTextPrimary : Tok.TextSecondary)),
            ],
        }, CellKey.Expand);

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

    static void FillClassicSpans(SpanBuffer b, Track t, int artistCount, bool showVideo, ColorF primary, ColorF secondary,
                                 ColorF tertiary)
    {
        b.Add(new TextSpan(t.Title, Weight: 600, Color: primary));
        if (showVideo)
        {
            b.Add(new TextSpan(" "));
            b.Add(new TextSpan(Icons.Movie, Color: tertiary, FontFamily: Theme.IconFont));
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
}
