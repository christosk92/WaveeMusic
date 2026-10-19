// ── Themes/WinUi/Structure/Lists/WinUiRowCells.cs — WinUI's track-row cells and the eager grid (themes plan 6.2; P6 W3) ───────────────────
//
// The ONE-SHOT row the eager lists build (Home's top tracks, the library reader's blocks, Recents, the album drawer's rows) and the cells WinUI's rows share,
// drawn from W-RowStates, W-Album and W-Playlist:
//
//   #        the track number (Body 14, tertiary). The playing row trades it for the accent bars (`Controls.Equalizer`, keyed "winui.row.now" so a test
//            can find the playing row); a buffering row for the ring; on ROW hover the play / pause glyph fades in over it (no hover play for a withheld row).
//   heart    filled (the primary text, the accent on the playing row) when liked, the outline when not, revealed on row hover, popping on a like edge.
//   title    the TrackTitle role (Body 14), the accent text on the playing row; the explicit "E" and a video's film glyph after it.
//   subline  the TrackMeta role (Caption 12, secondary): [EPISODE] · the linked artists · the album, the accent text on the playing row.
//   factual  every album, adder, date, plays and time in Body 14, the secondary ink, the accent text on the playing row.
//
// WHAT THE CORE KEEPS. The state a cell reflects (`Track.StateOf`, the like edge, `Track.LikedNow`), the data helpers (`Track.ContainerOf`, `NowUnix`,
// `AddedByLabel`, `DeckPlaying`), the navigation (`GoToArtistAt`, `GoToContainer`) and the lane wrappers (`Track.RowCenterCell`, `RowLeftCell`, `RowEndCell`,
// `MoreCell`). `Track.RowState.OnThisPc` is drawn as the ring that leads the subline (`OnThisPcMark`); an offline session dims a row it cannot reach.
//
// Every reveal (`HoverOpacity`) sits on a NON-interactive wrapper so it inherits the row's hover; every in-row affordance sets `BlocksDragArm`.
// Numbers are `Rules\WinUiListRules.cs` (WVTH003).

using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.WinUi;

using CellKey = global::Wavee.Track.CellKey;
using ColumnSet = global::Wavee.Track.ColumnSet;
using Format = global::Wavee.Track.Format;
using GridOptions = global::Wavee.Track.GridOptions;
using RowMetrics = global::Wavee.Track.RowMetrics;
using RowState = global::Wavee.Track.RowState;

/// <summary>WinUi's eager grid and the track-row cells it is made of.</summary>
internal static class WinUiRowCells
{
    /// <summary>The key of the playing row's bars (W-RowStates: "accent eq").</summary>
    public const string NowKey = "winui.row.now";

    /// <summary>The accent of the bars (W-RowStates "accent eq": the accent fill, #60cdff).</summary>
    public static readonly Func<ColorF> Accent = static () => R.Ink.AccentAction;

    // ══ THE EAGER GRID ═══════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The eager row: the lanes of <paramref name="set"/> over <paramref name="tracks"/>, every cell keyed with <see cref="CellKey"/>,
    /// every wrapper <c>MinWidth = 0</c> + clipped so a squeezed lane yields. A set that reserves the Actions / Expand lane always gets a cell
    /// there, so the cell count equals the track count.</summary>
    public static Element Grid(Track t, int displayIndex, in RowState st, in ColumnSet set, TrackSize[] tracks,
                               float rowH, Element title, in GridOptions o)
    {
        long now = o.NowUnixSeconds > 0 ? o.NowUnixSeconds : Track.NowUnix;
        bool unplayable = t.NotYetOut(now) || t.Unplayable();
        bool gone = !unplayable && Track.OfflineGone(t, st.OnThisPc);   // the session is down and the audio is not on this PC
        bool withheld = unplayable || gone;
        Track d = t.ForDisplay;
        bool isNow = st.IsNow;
        ColorF accent = Tok.AccentTextPrimary;
        ColorF secondary = WinUiListRules.RowInk(isNow, accent, Tok.TextSecondary);
        float art = o.Art > 0f ? o.Art : RowMetrics.ThumbSize;
        bool artistInTitle = o.ShowTrackArtist && !set.Artist;

        var cells = new Element[Track.CellCount(in set)];
        int i = 0;

        if (!set.IndexHidden)
            cells[i++] = NumberCell(displayIndex, in st, withheld ? null : o.OnPlay, o.HoverPaused, o.ChartStatus, CellKey.Num);
        if (set.Heart)
            cells[i++] = Track.RowCenterCell(HeartReveal(Heart(st.Saved, o.OnLike, o.LikePop, isNow), LikedNow(t)), CellKey.Heart);
        if (set.Thumb)
            cells[i++] = Track.RowCenterCell(Controls.Artwork(Track.RowArtUrl(d), art, art, R.Shape.Control,
                                                        decodePx: Track.RowArtDecodePx), CellKey.Art);

        bool showMeta = artistInTitle || o.ShowAlbumInMeta || st.OnThisPc;
        Element titleLine = TitleLine(title, isNow, d.IsExplicit, t.HasVideo);
        cells[i++] = new BoxEl
        {
            Key = CellKey.Title, Direction = 0, AlignItems = FlexAlign.Center, MinWidth = 0f, ClipToBounds = true,
            Children =
            [
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f,
                    Children = showMeta ? [titleLine, MetadataLine(d, artistInTitle, o.ShowAlbumInMeta, secondary, st.OnThisPc)] : [titleLine],
                },
            ],
        };

        if (set.Artist) cells[i++] = Track.RowLeftCell(ArtistLinks(d, secondary), CellKey.Artist);
        if (set.Album) cells[i++] = Track.RowLeftCell(AlbumLink(d, secondary), CellKey.Album);
        if (set.By) cells[i++] = Track.RowLeftCell(Factual(AddedByName(o.AddedBy), secondary, fill: true), CellKey.By);
        if (set.Date) cells[i++] = Track.RowLeftCell(Factual(Format.DateAddedLabel(o.AddedAt, now), secondary, fill: true), CellKey.Date);
        if (set.Plays)
            cells[i++] = Track.RowEndCell(Factual(withheld || t.PlayCount == 0 ? Format.Dash : Format.PlaysLabel(t.PlayCount),
                                                  WinUiListRules.RowInk(isNow, accent, Tok.TextTertiary)), CellKey.Plays);
        if (RowMetrics.ShowTempo(in set)) cells[i++] = Track.RowEndCell(TempoCell(t, isNow ? accent : null), CellKey.Tempo);
        if (RowMetrics.ShowHeartTrailing(in set))
            cells[i++] = Track.RowCenterCell(HeartReveal(Heart(st.Saved, o.OnLike, o.LikePop, isNow), LikedNow(t)), CellKey.HeartTrailing);

        if (RowMetrics.ShowDuration(in set))
        {
            string durationText = gone ? Loc.Get(Strings.Track.NotOnThisPc) : Track.DurationLabel(t, now);
            cells[i++] = Track.RowEndCell(withheld
                ? ToolTip.Wrap(Reason(durationText), Track.WithheldReason(t, now) ?? Loc.Get(Strings.Track.NotOnThisPcReason))
                : Factual(durationText, secondary), CellKey.Duration);
        }

        if (set.Actions)
            cells[i++] = o.ActionsCell is { } actions ? Keyed(actions, CellKey.More) : Track.RowCenterCell(new BoxEl(), CellKey.More);
        if (set.Expand)
            cells[i++] = o.ExpandCell is { } expand ? Keyed(expand, CellKey.Expand) : Track.RowCenterCell(new BoxEl(), CellKey.Expand);

        // WinUI's rows are full-width plates: the grid pays only the small inset that puts the "#" 14 in (as the table's rows do).
        float padX = WinUiListRules.RowPad;
        var grid = new GridEl
        {
            Columns = tracks, ColGap = RowMetrics.ColGapFor(set.Tier), RowHeight = rowH, Grow = 1f,
            Padding = new Edges4(padX, 0f, padX, 0f),
            Children = cells,
        };
        // The whole row steps back (W-RowStates); the grid has no opacity, so a withheld row (a rare, static fact) wraps it in one box.
        return withheld
            ? new BoxEl { Direction = 0, Grow = 1f, AlignItems = FlexAlign.Stretch, MinWidth = 0f, Opacity = Track.RowNotYetOutOpacity, Children = [grid] }
            : grid;
    }

    /// <summary>The eager title: one plain ellipsis run in the TrackTitle role, the accent on the playing row.</summary>
    public static TextEl TitleCell(Track t, bool isNow)
        => R.Type.TrackTitle(t.ForDisplay.Title) with
        {
            Color = WinUiListRules.RowInk(isNow, R.Ink.AccentDecor, Tok.TextPrimary),
            Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
        };

    // ══ THE CELLS ════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The # cell: buffering → the ring; playing → the accent bars (keyed <see cref="NowKey"/>); otherwise the number in the Numeral
    /// role with the chart mark beside it. On ROW hover the rest layer fades out and the play / pause glyph fades in; a null
    /// <paramref name="onPlay"/> (a withheld row, a skeleton) reveals nothing.</summary>
    public static Element NumberCell(int displayIndex, in RowState st, Action? onPlay, IReadSignal<bool>? hoverPaused, byte chartStatus,
                                     string? key = null, bool large = false)
    {
        ColorF accent = Tok.AccentTextPrimary;
        Element rest;
        if (st.IsBuffering) rest = Track.Spinner();
        else if (st.IsNow)
            rest = new BoxEl
            {
                Key = NowKey, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Children = [Controls.Equalizer(Track.DeckPlaying, Accent, Track.RowEqualizerHeight, hoverPaused)],
            };
        else
        {
            TextEl number = (large ? R.Type.NumeralLarge(RowNumberLabel(displayIndex + 1)) : R.Type.DenseTitle(RowNumberLabel(displayIndex + 1)))
                with { Color = Tok.TextTertiary, MaxLines = 1, Wrap = TextWrap.NoWrap };
            rest = ChartGlyph(chartStatus) is { } glyph
                ? new BoxEl { Direction = 0, Gap = WinUiListRules.NumberGap, AlignItems = FlexAlign.Center, Children = [number, glyph] }
                : number;
        }

        bool reveal = st.IsBuffering || onPlay is not null;
        var restLayer = new BoxEl
        {
            Grow = 1f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            HoverOpacity = reveal ? 0f : float.NaN,
            Children = [rest],
        };
        if (!reveal)
            return new BoxEl { Key = key, ZStack = true, MinWidth = 0f, ClipToBounds = true, Children = [restLayer] };

        Element transport = st.IsBuffering
            ? Track.Spinner()
            : new BoxEl
            {
                Width = Track.RowTransportBox, Height = Track.RowTransportBox, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                PressScale = R.Motion.ScaleEmphatic.Press,
                Children = [Icon(st.IsNow && st.IsPlaying ? WinUiDetailRules.PauseSolid : WinUiDetailRules.PlaySolid, WinUiListRules.TransportGlyph,
                                 st.IsNow ? accent : Tok.TextPrimary)],
            };
        Element revealed = onPlay is null
            ? transport
            : new BoxEl
            {
                Grow = 1f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                OnClick = onPlay, Cursor = CursorId.Hand, Role = AutomationRole.Button, BlocksDragArm = true,
                Children = [transport],
            };
        return new BoxEl
        {
            Key = key, ZStack = true, MinWidth = 0f, ClipToBounds = true,
            Children =
            [
                restLayer,
                new BoxEl
                {
                    Direction = 0, Grow = 1f, AlignItems = onPlay is null ? FlexAlign.Center : FlexAlign.Stretch,
                    Justify = FlexJustify.Center, Opacity = 0f, HoverOpacity = 1f,
                    Children = [revealed],
                },
            ],
        };
    }

    const byte ChartUp = 2, ChartDown = 3, ChartNew = 4;

    /// <summary>The chart movement beside the number: up / down / "new" / nothing for Equal and Unknown (never a guessed arrow).</summary>
    static Element? ChartGlyph(byte status) => status switch
    {
        ChartUp => ChartMark("▲", Tok.SystemFillSuccess),
        ChartDown => ChartMark("▼", Tok.SystemFillCritical),
        ChartNew => ChartMark(Loc.Get(Track.LocChartNew), Tok.SystemFillSuccess),
        _ => null,
    };

    static TextEl ChartMark(string text, ColorF ink) => new(text)
    {
        Size = WinUiListRules.ChartMarkSize, LineHeight = WinUiListRules.ChartMarkLine, Weight = 700, Color = ink,
    };

    static string?[] s_rowNumberLabels = new string?[256];

    /// <summary>The row number as text, formatted ONCE per value for the life of the process (a recycle never formats a number).</summary>
    internal static string RowNumberLabel(int n)
    {
        if (n < 0) return "";
        if (n >= s_rowNumberLabels.Length)
        {
            if (n >= 1 << 16) return n.ToString(CultureInfo.InvariantCulture);
            int size = s_rowNumberLabels.Length;
            while (size <= n) size *= 2;
            Array.Resize(ref s_rowNumberLabels, size);
        }
        return s_rowNumberLabels[n] ??= n.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The row's heart: filled in the accent when liked, the outline in the secondary ink when not. <paramref name="pop"/> is the
    /// caller's like EDGE (the glyph mounts with the pop spring once); a null <paramref name="onLike"/> is a static, inert heart.</summary>
    public static Element Heart(bool saved, Action? onLike, bool pop, bool isNow = false)
        => new BoxEl
        {
            Width = RowMetrics.HeartCol, Height = RowMetrics.HeartCol,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = CornerRadius4.All(WinUiListRules.RowRadius),
            Cursor = onLike is null ? null : CursorId.Hand, OnClick = onLike,
            Role = onLike is null ? AutomationRole.None : AutomationRole.Button,
            BlocksDragArm = true,
            Children =
            [
                new BoxEl
                {
                    Key = saved ? "hg:on" : "hg:off",
                    Animate = pop && saved ? WinUiListRules.HeartPop : null,
                    Children = [Icon(saved ? Icons.HeartFill : Icons.Heart, WinUiListRules.HeartGlyph, saved ? WinUiListRules.HeartInk(isNow, R.Ink.AccentAction, Tok.TextPrimary) : Tok.TextPrimary)],
                },
            ],
        }.Interactive(Interaction.Subtle);

    /// <summary>The heart's row reveal: rest opacity 1 while <paramref name="shown"/> holds (a liked row, or the focused one), 0 otherwise;
    /// 1 on ROW hover. The reveal rides a non-interactive wrapper, so it follows the row's hover.</summary>
    internal static Element HeartReveal(Element heart, Func<bool> shown) => new BoxEl
    {
        Grow = 1f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Opacity = Prop.Of(() => shown() ? 1f : 0f), HoverOpacity = 1f,
        Children = [heart],
    };

    static Func<bool> LikedNow(Track t)
    {
        var track = t;
        return () => Track.LikedNow(track);
    }

    /// <summary>The title, then the explicit "E" and a video's film glyph right after it (the title yields first).</summary>
    static Element TitleLine(Element title, bool isNow, bool isExplicit, bool hasVideo)
    {
        if (!isExplicit && !hasVideo) return title;
        var kids = new List<Element>(3) { new BoxEl { Direction = 0, Shrink = 1f, MinWidth = 0f, ClipToBounds = true, Children = [title] } };
        if (isExplicit) kids.Add(ExplicitMark());
        if (hasVideo) kids.Add(Icon(Icons.Movie, WinUiListRules.FilmGlyph, WinUiListRules.RowInk(isNow, Tok.AccentTextPrimary, Tok.TextTertiary)) with { Shrink = 0f });
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = WinUiListRules.ExplicitGap, MinWidth = 0f, ClipToBounds = true,
            Children = kids.ToArray(),
        };
    }

    /// <summary>The subline: [EPISODE] · [E] · the linked artists · the album, one ellipsized run whose artist and album parts are links.</summary>
    public static Element MetadataLine(Track t, bool showArtists, bool showAlbum, ColorF ink, bool onThisPc = false)
    {
        bool episode = t.IsPodcast;
        int artistCount = showArtists ? t.ArtistSlots.Length : 0;
        var container = Track.ContainerOf(t);
        bool album = (showAlbum || episode) && container.Name.Length > 0;
        TextSpan[]? spans = Track.BuildLinkSpans(t, artistCount, album ? container : default);

        var kids = new List<Element>(3);
        if (episode)
            kids.Add(R.Type.Eyebrow(Loc.Get(Strings.Detail.Badge.Episode)) with { Color = Tok.TextTertiary, Shrink = 0f, MaxLines = 1 });
        if (onThisPc) kids.Add(OnThisPcMark(ink));
        if (spans is not null)
        {
            var meta = R.Type.TrackMeta("");
            kids.Add(new SpanTextEl(spans)
            {
                Size = meta.Size, LineHeight = meta.LineHeight, Color = ink,
                Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MaxLines = 1,
                Grow = 1f, Basis = 0f, MinWidth = 0f,
                OnSpanClick = Track.LinkClick(t, artistCount, album),
            });
        }
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = WinUiListRules.MetaGap, MinWidth = 0f, ClipToBounds = true,
            Children = kids.ToArray(),
        };
    }

    /// <summary>The "on this PC" mark : a 14 ring with the small arrow inside, in the subline's ink (the accent on the playing row),
    /// leading the subline. An indicator only (NQ4); its tooltip says "On this PC".</summary>
    internal static Element OnThisPcMark(Prop<ColorF> ink) => ToolTip.Wrap(new BoxEl
    {
        Width = WinUiListRules.OnPcMark, Height = WinUiListRules.OnPcMark, Shrink = 0f, Corners = Radii.Circle(WinUiListRules.OnPcMark),
        BorderWidth = WinUiListRules.OnPcRing, BorderColor = ink, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Margin = new Edges4(0f, 0f, WinUiListRules.OnPcAfter, 0f),
        // The arrow is an ICON (Segoe Fluent's download glyph), not a word: a Unicode "↓" at 8 DIP read as text under the 11 DIP type floor.
        Children = [Icon(Icons.Download, WinUiListRules.OnPcGlyph) with { Color = ink }],
    }, Loc.Get(Strings.Track.OnThisPc));

    /// <summary>The explicit "E" (W-Album): a 16 plate in the secondary ink with 2-px corners, the letter 10 bold in the inverse ink.</summary>
    internal static BoxEl ExplicitMark() => new()
    {
        Width = WinUiListRules.ExplicitBox, Height = WinUiListRules.ExplicitBox, Shrink = 0f,
        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Corners = CornerRadius4.All(WinUiListRules.ExplicitRadius), Fill = Tok.TextSecondary,
        Children = [new TextEl("E") { Size = WinUiListRules.ExplicitType, Weight = 600, Color = Tok.TextInverse }],
    };

    /// <summary>The artist lane: one ellipsized run of per-artist links.</summary>
    static Element ArtistLinks(Track t, ColorF ink)
    {
        var meta = R.Type.DenseTitle("");
        return Track.ArtistLinks(t, meta.Size, meta.LineHeight, ink);
    }

    /// <summary>The album lane as one link; a name-less ref states the absence with the em dash and stays clickable whenever a uri exists.</summary>
    static Element AlbumLink(Track t, ColorF ink)
    {
        var container = Track.ContainerOf(t);
        bool named = container.Name.Length > 0;
        bool link = container.Uri.IsValid;
        var track = t;
        var meta = R.Type.DenseTitle("");
        return new SpanTextEl(new[] { new TextSpan(named ? container.Name : Format.Dash, IsLink: link) })
        {
            Size = meta.Size, LineHeight = meta.LineHeight, Color = named ? ink : Tok.TextTertiary,
            Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MaxLines = 1,
            Grow = 1f, Basis = 0f, MinWidth = 0f,
            OnSpanClick = link ? (Action<int>)(_ => Track.GoToContainer(track)) : null,
        };
    }

    /// <summary>The Added-by name once identity is known, else nothing (never a uri fragment).</summary>
    static string AddedByName(User by) => by.Slot > 0 && by.Knows(UserFields.Identity) ? Track.AddedByLabel(by) : "";

    /// <summary>"101.5 · 8B", empty until the audio facts land: a plain WinUI column, no Classic key swatch.</summary>
    static Element TempoCell(Track t, ColorF? ink)
    {
        if (!t.Knows(TrackFields.Audio) || t.Tempo == 0) return new BoxEl();
        string? key = Format.CamelotLabel(t.Camelot) ?? Format.KeyLabel(t.Key);
        var parts = new List<Element>(3);
        parts.Add(Factual(Format.TempoLabel(t.Tempo), ink ?? Tok.TextSecondary));
        if (key is not null)
        {
            parts.Add(Factual("·", ink ?? Tok.TextTertiary));
            parts.Add(Factual(key, ink ?? Tok.TextTertiary));
        }
        return new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.XS, Children = parts.ToArray() };
    }

    /// <summary>A withheld row's short reason in the time lane.</summary>
    static TextEl Reason(string text) => new(text)
    {
        Size = WinUiListRules.ReasonSize, LineHeight = R.Type.DenseMeta("").LineHeight,
        Color = Tok.TextTertiary, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
    };

    /// <summary>The factual rung (DenseMeta 13/18) for every number, album, adder, date, plays, tempo and time.</summary>
    internal static TextEl Factual(string text, ColorF color, bool fill = false) => R.Type.DenseTitle(text) with
    {
        Color = color, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
        Grow = fill ? 1f : 0f, Basis = fill ? 0f : float.NaN, MinWidth = 0f,
    };

    static Element Keyed(Element cell, string key) => cell.Key == key ? cell : cell with { Key = key };
}
