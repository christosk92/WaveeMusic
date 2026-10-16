// ── Themes/Classic/Structure/Lists/ClassicRowCells.cs — Classic's track-row cells and the eager grid (themes plan 6.1, T8c) ────
//
// From `Entities/Track.UI.cs` (sections 2 and 3, "the grid" and "the cells"): the ONE-SHOT row the eager lists build (Home's top
// tracks, the library reader's blocks, Recents, the album drawer's rows, the chart's # and ♥ cells) and the cells it is made of —
// the # state machine, the heart and its row reveal, the metadata line, the artist and album links, the Added-by name,
// the tempo lane, the Classic title line and its explicit word-mark. `ClassicEagerRow` draws the eager row's container around
// `Grid`; `ClassicArtistChart` reuses `NumberCell`, `Heart` and `ClassicExplicitBadge`; `ClassicTrackTable` reuses `RowNumberLabel`
// and the heart's pop. They were `Track.Grid`, `Track.NumberCell`... and moved here verbatim.
//
// WHAT THE CORE KEEPS. The state a cell reflects (`Track.StateOf`, `LikeEdge`, `IsNowPlaying`, `Invoke`), the data helpers
// (`Track.ContainerOf`, `NowUnix`, `AddedByLabel`, `DeckPlaying`), the navigation (`GoToArtistAt`, `GoToContainer`) and the two
// lane wrappers pages call with no registry (`Track.MoreCell`, `ExpandCell`: Recents passes them in `EagerRowOptions.ActionsCell`,
// with `RowCenterCell`, `RowLeftCell` and `RowEndCell` they are built on) stay on `Track`. Nothing here names `Playback`, `Entities`
// or `Store` (WVTH002), and every number that makes a look is `Rules\ClassicTrackRowLayout.cs` (WVTH003).
//
// ONE CELL, EVERY SURFACE (ch 01 §0.1). `Grid` is the single definition of what an eager track row looks like; its bound twin is
// `ClassicTrackTable.BoundGrid` (same lanes, keys and wrappers, built once per slot). A recycle never reaches this file: only an
// eager host's render does.
//
// HOVER REVEALS. Every reveal (`HoverOpacity`) sits on a NON-interactive wrapper, so it inherits hover progress from the nearest
// interactive ancestor (the row's skin) and survives the pointer crossing onto the button inside it (parity 10-11). A click target
// inside a reveal is its own child box. Every in-row affordance sets `BlocksDragArm` (ch 01 §0.12).

using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Input;
using FluentGpu.Localization;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Classic;

using CellKey = global::Wavee.Track.CellKey;
using ColumnSet = global::Wavee.Track.ColumnSet;
using Format = global::Wavee.Track.Format;
using GridOptions = global::Wavee.Track.GridOptions;
using RowMetrics = global::Wavee.Track.RowMetrics;
using RowState = global::Wavee.Track.RowState;

/// <summary>Classic's eager grid and the track-row cells it is made of.</summary>
internal static class ClassicRowCells
{
    /// <summary>THE row cell (ch 01 §0.1, TrackRow.Grid). Plain and diffable — no <c>Animate</c> — so a host re-render
    /// patches cells in place. Every cell is keyed with <see cref="CellKey"/>; every wrapper takes <c>MinWidth = 0</c> +
    /// <c>ClipToBounds</c> so a squeezed track yields instead of painting over its neighbour (ch 01 §9).
    /// <para><paramref name="title"/> is the CALLER's (<see cref="TitleCell"/>): the plain ellipsis run. A set that reserves the Actions / Expand lane always gets a cell there — an empty one when the
    /// caller supplied none — so the cell count equals the track count by construction.</para></summary>
    public static Element Grid(Track t, int displayIndex, in RowState st, in ColumnSet set, TrackSize[] tracks,
                               float rowH, Element title, in GridOptions o)
    {
        long now = o.NowUnixSeconds > 0 ? o.NowUnixSeconds : Track.NowUnix;
        // The ONE withheld predicate — not out yet, or ruled unavailable with no release instant (a terminal envelope
        // verdict): the dim here, the play gate and the facts strip must never disagree (ch 01 W6). The duration lane
        // states which (`Track.DurationLabel`); the heart stays: it IS liked.
        bool withheld = t.NotYetOut(now) || t.Unplayable();
        // THE DISPLAY ROW (Track.ForDisplay): a relinked id paints its canonical track's title, credits, album, art,
        // duration and explicit mark. Everything that IS this row — the heart, the play target, the verdicts above, the
        // plays and tempo lanes, the video chrome — keeps reading `t`.
        Track d = t.ForDisplay;
        bool classic = set.Classic;
        bool classicNow = classic && st.IsNow;
        ColorF secondaryInk = classicNow ? Tok.AccentTextPrimary : Tok.TextSecondary;
        ColorF tertiaryInk = classicNow ? Tok.AccentTextPrimary : Tok.TextTertiary;
        float art = o.Art > 0f ? o.Art : RowMetrics.ThumbSize;
        // A dedicated Classic artist lane owns the artists; the title line never repeats them.
        bool artistInTitle = o.ShowTrackArtist && !set.Artist;
        bool badge = o.ShowListBadges && d.IsExplicit;

        var cells = new Element[Track.CellCount(in set)];
        int i = 0;

        // Every cell is born with its CellKey (the factories take it) — a `with { Key = … }` afterwards would clone the
        // 123-member BoxEl a second time per cell, per render (rule 13's recycle-frame budget).
        // # — number / equalizer / star / spinner at rest, the transport on ROW hover; no hover play for a row that is
        // not out or ruled unavailable (the play would be a button that does nothing, or one that can only fail).
        cells[i++] = NumberCell(displayIndex, in st, withheld ? null : o.OnPlay, o.HoverPaused, o.ChartStatus, classic,
                                o.Accent, CellKey.Num);

        if (set.Heart)
            cells[i++] = Track.RowCenterCell(HeartReveal(t, Heart(st.Saved, o.OnLike, o.LikePop, classic)), CellKey.Heart);

        if (set.Thumb)
            cells[i++] = Track.RowCenterCell(Controls.Artwork(Track.RowArtUrl(d), art, art, Radii.Control,
                                                        decodePx: Track.RowArtDecodePx), CellKey.Art);

        bool showMeta = !classic && (artistInTitle || o.ShowAlbumInMeta || badge);
        // A video is the small film glyph right after the title in both row styles, never a trailing lane (tuned Classic L3).
        Element titleLine = classic
            ? ClassicTitleLine(d, title, artistInTitle, t.HasVideo, badge, classicNow)
            : t.HasVideo ? ModernTitleWithVideo(title, st.IsNow) : title;
        var titleCol = new BoxEl
        {
            // MinWidth 0: the STAR track collapses first under pressure; without the floor override the stack keeps its
            // natural width and the runs paint across the row.
            Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = Spacing.XXS,
            Opacity = withheld ? Track.RowNotYetOutOpacity : 1f,
            Children = showMeta
                ? [titleLine, MetadataLine(d, artistInTitle, o.ShowAlbumInMeta, badge, classicNow ? Tok.AccentTextPrimary : null)]
                : [titleLine],
        };
        cells[i++] = new BoxEl
        {
            Key = CellKey.Title, Direction = 0, AlignItems = FlexAlign.Center, MinWidth = 0f, ClipToBounds = true,
            Children = [titleCol],
        };

        float factualSize = classic ? 14f : 12f, factualLine = classic ? 20f : 16f;
        if (set.Artist)
            cells[i++] = Track.RowLeftCell(Track.ArtistLinks(d, factualSize, factualLine, secondaryInk), CellKey.Artist);
        if (set.Album)
            cells[i++] = Track.RowLeftCell(AlbumLink(d, secondaryInk, factualSize, factualLine), CellKey.Album);
        if (set.By)
            cells[i++] = AddedByCell(o.AddedBy, secondaryInk, classic, CellKey.By);
        if (set.Date)
            cells[i++] = Track.RowLeftCell(RowFactualFillText(Format.DateAddedLabel(o.AddedAt, now), classic, secondaryInk), CellKey.Date);
        if (set.Plays)
            // A count of 0 is "not known yet" (kind 185 refuses to invent one), never "nobody played it" — and a pending
            // track states the absence rather than a dismal real-looking number.
            cells[i++] = Track.RowEndCell(RowFactualText(withheld || t.PlayCount == 0 ? Format.Dash : Format.PlaysLabel(t.PlayCount),
                                                   classic, tertiaryInk), CellKey.Plays);
        if (RowMetrics.ShowTempo(in set))
            cells[i++] = Track.RowEndCell(TempoCell(t, classicNow ? Tok.AccentTextPrimary : null, classic), CellKey.Tempo);

        // The TRAILING ♥ — the same heart, on the right, immediately before the duration (the library reader's
        // 28 | 1fr | 32 | 52). Mutually exclusive with the leading lane; the gate is shared with the width track.
        if (RowMetrics.ShowHeartTrailing(in set))
            cells[i++] = Track.RowCenterCell(HeartReveal(t, Heart(st.Saved, o.OnLike, o.LikePop, classic)), CellKey.HeartTrailing);

        // The clock, or a withheld row's SHORT reason ("Unavailable", the release date) at the reason rung with the LONG
        // reason as its tooltip (tuned Classic L4) — the bound table's lane, word for word (`Track.DurationLabel`).
        string durationText = Track.DurationLabel(t, now);
        if (RowMetrics.ShowDuration(in set)) cells[i++] = Track.RowEndCell(withheld
            ? ToolTip.Wrap(new TextEl(durationText)
              {
                  Size = ClassicTrackRowLayout.ReasonSize, LineHeight = classic ? ClassicTrackRowLayout.BodyLine : ClassicTrackRowLayout.CaptionLine,
                  Color = Tok.TextTertiary, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
              }, Track.WithheldReason(t, now) ?? "")
            : RowFactualText(durationText, classic, classicNow ? Tok.AccentTextPrimary : Tok.TextSecondary), CellKey.Duration);

        // Trailing chrome AFTER Duration so the "…" never wedges between Album and Tempo.
        // A caller-supplied cell that already carries the lane's key (the table row builds it once with `CellKey.More` /
        // `CellKey.Expand`) is used as-is; any other is keyed here so the contract "every cell is keyed" still holds.
        if (set.Actions)
            cells[i++] = o.ActionsCell is { } actions ? Keyed(actions, CellKey.More) : Track.RowCenterCell(new BoxEl(), CellKey.More);
        if (set.Expand)
            cells[i++] = o.ExpandCell is { } expand ? Keyed(expand, CellKey.Expand) : Track.RowCenterCell(new BoxEl(), CellKey.Expand);

        float padX = RowMetrics.PadXFor(set.Tier);
        // Modern pads PadX − RowInset: with the skin's RowInset margin the first column still starts at PadX, aligned
        // under the header's "#". Classic has no inset (margin 0), so it pads the full PadX.
        float inner = classic ? padX : padX - RowMetrics.RowInset;
        return new GridEl
        {
            Columns = tracks, ColGap = RowMetrics.ColGapFor(set.Tier), RowHeight = rowH, Grow = 1f,
            Padding = new Edges4(inner, 0f, inner, 0f),
            Children = cells,
        };
    }

    /// <summary>The eager row's title element (ch 01 W3): ONE plain ellipsis <c>TextEl</c> (a marquee per row was ~24 of ~60
    /// components on a cold mount; the marquee is the virtualized table's now-playing row alone, <c>ClassicTrackTable</c>).
    /// Classic uses the same 14/600 run; its artist fold is <see cref="Grid"/>'s.
    /// <para>NAMED <c>TitleCell</c>, not the contract's <c>Title</c>: the struct already has a <c>Title</c> property and
    /// C# forbids a method of the same name (owner M's correction).</para></summary>
    public static Element TitleCell(Track t, bool isNow)
        => R.Type.TrackTitle(t.ForDisplay.Title) with
        {
            Color = isNow ? Tok.AccentTextPrimary : Tok.TextPrimary,
            Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
        };

    static readonly Func<ColorF> s_rowAccentInk = static () => Tok.AccentTextPrimary;

    /// <summary>The # cell — a state machine over THIS track's playback (ch 01 §0.2, W3-W5):
    /// buffering → a 16-DIP ring (hovered or not); now-playing → the live equalizer (Classic: the Volume mark); the
    /// album's top track → the star; otherwise the number, with the chart glyph beside it. On ROW hover the rest layer
    /// fades out and a 24-DIP play/pause transport fades in.
    /// <para>The equalizer is the persistent <c>Controls.Equalizer</c> host reading <c>Playback.IsPlaying</c> — a
    /// play↔pause flip patches it in place, never a Key flip (ch 01 §9 trap 2) — with <paramref name="hoverPaused"/> (the
    /// ROW's hover signal) as its pause gate, so invisible bars stop ticking. <paramref name="accent"/> tints the bars
    /// (a page's ambient accent); null is <c>Tok.AccentTextPrimary</c>.</para>
    /// <para>A null <paramref name="onPlay"/> (a not-yet-out row, a skeleton) reveals NOTHING on hover and keeps the
    /// rest layer visible — ch 01 W6: no hover play button.</para></summary>
    public static Element NumberCell(int displayIndex, in RowState st, Action? onPlay, IReadSignal<bool>? hoverPaused,
                                     byte chartStatus, bool classic, Func<ColorF>? accent = null, string? key = null)
    {
        ColorF ink = Tok.AccentTextPrimary;
        Element rest;
        if (st.IsBuffering) rest = Track.Spinner();
        // Classic states now-playing with the Volume mark and leaves the other rest states empty (TrackRow.cs:1074-1077).
        else if (classic) rest = st.IsNow ? Icon(Icons.Volume, 13f, ink) : new BoxEl();
        else if (st.IsNow) rest = Controls.Equalizer(Track.DeckPlaying, accent ?? s_rowAccentInk, Track.RowEqualizerHeight, hoverPaused);
        else if (st.IsTop) rest = Icon(Icons.FavoriteStarFill, 11f, ink);
        else
        {
            Element number = Caption(RowNumberLabel(displayIndex + 1)) with { Color = Tok.TextTertiary };
            rest = ChartGlyph(chartStatus) is { } glyph
                ? new BoxEl { Direction = 0, Gap = ClassicTrackRowLayout.NumberGap, AlignItems = FlexAlign.Center, Children = [number, glyph] }
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
                PressScale = R.Motion.ScaleEmphatic.Press,   // a real button push; the row-driven reveal is the hover cue
                Children = [Icon(st.IsNow && st.IsPlaying ? Icons.Pause : Icons.Play, 12f, st.IsNow ? ink : Tok.TextPrimary)],
            };
        Element revealed = onPlay is null
            ? transport
            : new BoxEl
            {
                // The click target fills the lane; the reveal sits on the non-interactive wrapper around it.
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

    /// <summary>The chart movement beside the plain number: green up / red down / "NEW" / nothing for Equal and Unknown —
    /// never a guessed arrow (ch 01 W5).</summary>
    static Element? ChartGlyph(byte status) => status switch
    {
        ChartUp => new TextEl("▲") { Size = ClassicTrackRowLayout.ChartMarkSize, LineHeight = ClassicTrackRowLayout.ChartMarkLine, Weight = 700, Color = Tok.SystemFillSuccess },
        ChartDown => new TextEl("▼") { Size = ClassicTrackRowLayout.ChartMarkSize, LineHeight = ClassicTrackRowLayout.ChartMarkLine, Weight = 700, Color = Tok.SystemFillCritical },
        ChartNew => new TextEl(Loc.Get(Track.LocChartNew)) { Size = ClassicTrackRowLayout.ChartMarkSize, LineHeight = ClassicTrackRowLayout.ChartMarkLine, Weight = 700, Color = Tok.SystemFillSuccess },
        _ => null,
    };

    static string?[] s_rowNumberLabels = new string?[256];

    /// <summary>The row number as text, formatted ONCE per value for the life of the process — a recycle never formats a
    /// number (ch 01 §9 trap 5).</summary>
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

    /// <summary>The heart's pop: an Enter-only overshoot spring (response 0.30, damping 0.55) from 0.25 scale, 0 opacity
    /// and a 2-DIP blur. A SPRING, so the reduced-motion policy keeps it where it would drop a tween Enter. No Exit leg: a
    /// scrolling list must never spawn exit orphans for recycled glyphs.</summary>
    internal static readonly LayoutTransition s_heartPopIn = new(
        TransitionChannels.Opacity,
        TransitionDynamics.Spring(0.30f, 0.55f),
        Enter: new EnterExit(Sx: 0.25f, Sy: 0.25f, Opacity: 0f, Active: true, Blur: Expressive.BlurSmall));

    /// <summary>The per-row heart (ch 01 §0.3) — PAINTED AT REST on every row: filled accent when saved (Classic: primary),
    /// outline tertiary when not (Classic: secondary). <paramref name="pop"/> is a caller-detected like EDGE
    /// (<c>Track.LikeEdge</c>); every other render mounts the keyed glyph with no animation, so a recycle never replays
    /// it. A null <paramref name="onLike"/> is a static, non-interactive heart (no cursor, no click).</summary>
    public static Element Heart(bool saved, Action? onLike, bool pop, bool classic)
        => new BoxEl
        {
            Width = RowMetrics.HeartCol, Height = RowMetrics.HeartCol,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = Radii.Circle(RowMetrics.HeartCol),
            Cursor = onLike is null ? null : CursorId.Hand, OnClick = onLike,
            Role = onLike is null ? AutomationRole.None : AutomationRole.Button,
            // Its own affordance: without this a press on the heart arms the row drag and the like never fires.
            BlocksDragArm = true,
            Children =
            [
                new BoxEl
                {
                    Key = saved ? "hg:on" : "hg:off",   // keyed CHILD of the stable circle — keys live in child arrays
                    Animate = pop && saved ? s_heartPopIn : null,
                    Children = [Icon(saved ? Icons.HeartFill : Icons.Heart, 14f,
                        saved ? (classic ? Tok.TextPrimary : Tok.AccentTextPrimary)
                              : (classic ? Tok.TextSecondary : Tok.TextTertiary))],
                },
            ],
        }.Interactive(Interaction.Subtle);

    /// <summary>The heart lanes' row reveal (tuned Classic L2), both the leading and the TRAILING lane: a LIKED row keeps
    /// its filled heart at rest, an UNLIKED one is invisible until the ROW is hovered and then fades its outline in.
    /// <para>The reveal rides a NON-interactive wrapper around the heart's own click target, so the hover progress it
    /// reads is the ROW skin's (the PointerBit every <c>HoverOpacity</c> descendant inherits — the same idiom
    /// <see cref="NumberCell"/>'s rest/reveal layers use, and the reason the reveal survives the pointer crossing onto
    /// the heart itself). <c>HoverOpacity</c> is 1 in BOTH states, so the one thing that differs is the REST opacity:
    /// 1 → 1 is a liked heart that does not move, 0 → 1 is the reveal.</para>
    /// <para>That rest opacity is a BOUND <c>Prop</c> over <c>Track.LikedNow</c> — the same subscribing read
    /// <c>Track.StateOf</c> makes — and not the row state's <c>Saved</c>, which is a value frozen when this element record
    /// was built. An optimistic like writes the Liked edge and the bound channel re-resolves in the same frame, with no
    /// render and no dependence on whoever owns this row re-running.</para></summary>
    static Element HeartReveal(Track t, Element heart)
    {
        var track = t;
        return HeartReveal(heart, () => Track.LikedNow(track));
    }

    /// <summary>The ONE heart reveal every row shares (the eager lanes above, the artist chart): rest opacity 1 while
    /// <paramref name="shown"/> holds (a liked row, or a row that holds the list's focus), 0 otherwise; 1 on ROW hover.</summary>
    internal static Element HeartReveal(Element heart, Func<bool> shown) => new BoxEl
    {
        Grow = 1f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Opacity = Prop.Of(() => shown() ? 1f : 0f), HoverOpacity = 1f,
        Children = [heart],
    };

    /// <summary>A Modern title with a video: the caller's title, then the small film glyph right after it (tuned Classic
    /// L3). The title yields first under pressure; the glyph keeps its size.</summary>
    static Element ModernTitleWithVideo(Element title, bool nowPlaying) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.XS, MinWidth = 0f, ClipToBounds = true,
        Children =
        [
            new BoxEl { Direction = 0, Shrink = 1f, MinWidth = 0f, ClipToBounds = true, Children = [title] },
            Icon(Icons.Movie, ClassicTrackRowLayout.FilmGlyphSize, nowPlaying ? Tok.AccentTextPrimary : Tok.TextTertiary) with { Shrink = 0f },
        ],
    };

    /// <summary>The responsive metadata subline (ch 01 §1.1): [EPISODE eyebrow] · [E] · artists · album, one ellipsized
    /// run whose artist and album parts are separate LINKS. Artists come from <c>Edges.TrackArtists</c> — one span per
    /// artist, ", " joined — never the flat <c>ArtistLineId</c> (ch 01 §9 "Where the plan is wrong" 1). An EPISODE row
    /// always carries its show. The press lands on the text leaf, so a link navigates without playing or selecting.</summary>
    public static Element MetadataLine(Track t, bool showArtists, bool showAlbum, bool explicitBadge, ColorF? ink)
    {
        bool episode = t.IsPodcast;
        int artistCount = showArtists ? t.ArtistSlots.Length : 0;
        var container = Track.ContainerOf(t);
        bool album = (showAlbum || episode) && container.Name.Length > 0;
        TextSpan[]? spans = Track.BuildLinkSpans(t, artistCount, album ? container : default);

        int kidCount = (episode ? 1 : 0) + (explicitBadge ? 1 : 0) + (spans is null ? 0 : 1);
        var kids = new Element[kidCount];
        int k = 0;
        if (episode)
            kids[k++] = R.Type.Eyebrow(Loc.Get(Strings.Detail.Badge.Episode)) with
            {
                Color = Tok.TextTertiary, Shrink = 0f, MaxLines = 1,
            };
        if (explicitBadge) kids[k++] = Controls.ExplicitBadge(14f);
        if (spans is not null)
            kids[k++] = new SpanTextEl(spans)
            {
                Size = ClassicTrackRowLayout.CaptionSize, LineHeight = ClassicTrackRowLayout.CaptionLine, Color = ink ?? Tok.TextSecondary,
                Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MaxLines = 1,
                Grow = 1f, Basis = 0f, MinWidth = 0f,
                OnSpanClick = Track.LinkClick(t, artistCount, album),
            };
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = ClassicTrackRowLayout.MetaGap, MinWidth = 0f, ClipToBounds = true,
            Children = kids,
        };
    }

    /// <summary>The album lane as one link (an episode row's SHOW, routed by the ref's own kind). A name-less ref states
    /// the absence with the em dash — in <c>TextTertiary</c> when the lane's ink is the ordinary secondary — and stays
    /// CLICKABLE whenever a uri exists, because opening it is what hydrates its name (ch 01 W26, parity 30).</summary>
    public static Element AlbumLink(Track t, ColorF color, float size, float lineHeight)
    {
        var container = Track.ContainerOf(t);
        bool named = container.Name.Length > 0;
        ColorF ink = named || color != Tok.TextSecondary ? color : Tok.TextTertiary;
        bool link = container.Uri.IsValid;
        var track = t;
        return new SpanTextEl(new[] { new TextSpan(named ? container.Name : Format.Dash, IsLink: link) })
        {
            Size = size, LineHeight = lineHeight, Color = ink,
            Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MaxLines = 1,
            Grow = 1f, Basis = 0f, MinWidth = 0f,   // yield to a squeezed Album track instead of flooring at the name
            OnSpanClick = link ? (Action<int>)(_ => Track.GoToContainer(track)) : null,
        };
    }

    /// <summary>The Added-by lane (ch 01 §3): a 24-DIP <c>PersonPicture</c> + an 8-DIP gap + a Caption name (Modern);
    /// the bare name with no avatar (Classic, parity 79). The profile name when <c>UserFields.Identity</c> is known, else
    /// and NOTHING until it lands: a uri fragment reads as a real name when it is only a guess, which is the rule
    /// `Playlist.NameOf` follows for the owner and the sidebar follows for a pin. An empty cell for no adder.</summary>
    public static Element AddedByCell(User by, ColorF ink, bool classic, string? key = null)
    {
        if (by.Slot <= 0) return Track.RowLeftCell(new BoxEl(), key);
        string name = by.Knows(UserFields.Identity) ? Track.AddedByLabel(by) : "";
        if (name.Length == 0) return Track.RowLeftCell(new BoxEl(), key);   // unresolved: blank, never the uri fragment
        string label = name;
        if (classic)
            return Track.RowLeftCell(RowFactualFillText(label, classic: true, ink), key);
        return new BoxEl
        {
            Key = key, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Start, Gap = Spacing.S,
            MinWidth = 0f, ClipToBounds = true,
            Children =
            [
                PersonPicture.Create("", Spacing.XXL, displayName: label, imageSourcePath: Controls.ArtUrl(by.ImageId)),
                Caption(label) with
                {
                    Color = ink, Grow = 1f, Basis = 0f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                },
            ],
        };
    }

    /// <summary>"[swatch] 101.5 · 8B" — the Camelot colour as a 6-DIP swatch at 0.85 through
    /// <c>ColorScience.DataDotInk</c> (a passthrough in dark, a hue-aware darkening in light), the BPM, then ONE key
    /// token (Camelot preferred, else the tonic). EMPTY — not "0 BPM", not a dash — until kind 222 lands, because a
    /// placeholder would flicker to a value a moment later (ch 01 §0.7, parity 28).</summary>
    public static Element TempoCell(Track t, ColorF? ink, bool classic)
    {
        if (!t.Knows(TrackFields.Audio) || t.Tempo == 0) return new BoxEl();
        string? key = Format.CamelotLabel(t.Camelot) ?? Format.KeyLabel(t.Key);
        bool swatch = t.CamelotColor != 0;
        var parts = new Element[(swatch ? 1 : 0) + 1 + (key is null ? 0 : 2)];
        int i = 0;
        if (swatch)
            parts[i++] = new BoxEl
            {
                // Both the 6-DIP box and its 1.5 corner sit below their ramps on purpose: a colour SWATCH, not a surface.
                Width = ClassicTrackRowLayout.TempoDot, Height = ClassicTrackRowLayout.TempoDot, Corners = CornerRadius4.All(1.5f), Opacity = ClassicTrackRowLayout.TempoDotOpacity,
                Fill = R.Ink.DataDot(t.CamelotColor), AlignSelf = FlexAlign.Center,
            };
        parts[i++] = RowFactualText(Format.TempoLabel(t.Tempo), classic, ink ?? Tok.TextSecondary);
        if (key is not null)
        {
            // Two bare numeric-ish tokens ("110 7B") read as one mangled value; the middot is the metadata joiner.
            parts[i++] = RowFactualText("·", classic, ink ?? Tok.TextTertiary);
            parts[i++] = RowFactualText(key, classic, ink ?? Tok.TextTertiary);
        }
        return new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.XS, Children = parts };
    }

    /// <summary>The Classic word-mark: the full localized "EXPLICIT" in a 14-high outlined box, pinned to the Title
    /// lane's trailing edge (ch 01 W8, parity 24). Accent ink at full opacity on a now-playing row, else tertiary at
    /// 0.6. Also the queue's Classic run and the artist chart's Classic rows (ch 21 §3, ch 08 W10).</summary>
    public static Element ClassicExplicitBadge(ColorF? ink = null) => new BoxEl
    {
        Height = ClassicTrackRowLayout.ClassicBadgeHeight, Padding = new Edges4(Spacing.XXS, 0f, Spacing.XXS, 0f), Shrink = 0f,
        Corners = CornerRadius4.All(2f), BorderWidth = 1f, BorderColor = ink ?? Tok.TextTertiary,
        Opacity = ink is null ? 0.6f : 1f,
        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Children =
        [
            new TextEl(Loc.Get(Strings.Detail.Badge.Explicit)) { Size = ClassicTrackRowLayout.ClassicBadgeSize, LineHeight = ClassicTrackRowLayout.ClassicBadgeLine, Weight = 600, Color = ink ?? Tok.TextTertiary },
        ],
    };

    /// <summary>Classic's one-line Title cell (ch 01 W8). With the artists folded in: ONE span run — title 600 · [film
    /// glyph] · "  ·  " · linked artists — ellipsized as a whole. With a dedicated Artist lane: the caller's title plus a
    /// 12-DIP film glyph. The EXPLICIT word-mark is pinned to the trailing edge behind a gap of 8 either way.</summary>
    static Element ClassicTitleLine(Track t, Element title, bool showArtists, bool showVideo, bool showExplicit,
                                    bool nowPlaying)
    {
        ColorF primary = nowPlaying ? Tok.AccentTextPrimary : Tok.TextPrimary;
        ColorF secondary = nowPlaying ? Tok.AccentTextPrimary : Tok.TextSecondary;
        ColorF tertiary = nowPlaying ? Tok.AccentTextPrimary : Tok.TextTertiary;
        Element leading;

        if (showArtists)
        {
            int artistCount = t.ArtistSlots.Length;
            leading = new SpanTextEl(BuildClassicSpans(t, artistCount, showVideo, primary, secondary, tertiary))
            {
                Size = ClassicTrackRowLayout.BodySize, LineHeight = ClassicTrackRowLayout.BodyLine, Color = primary,
                Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MaxLines = 1,
                Shrink = 1f, MinWidth = 0f,
                OnSpanClick = ClassicLinkClick(t, artistCount, showVideo),
            };
        }
        else
        {
            var clip = new BoxEl { Grow = 1f, Basis = 0f, MinWidth = 0f, ClipToBounds = true, Children = [title] };
            leading = new BoxEl
            {
                // It must claim its wrapper's solved width before its own Basis-0 title can grow; Shrink-only collapsed
                // the intrinsic width to the glyph and left every plain Classic title at zero width.
                Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.XS,
                Grow = 1f, Basis = 0f, MinWidth = 0f, ClipToBounds = true,
                Children = showVideo ? [clip, Icon(Icons.Movie, 12f, tertiary) with { Shrink = 0f }] : [clip],
            };
        }

        var lead = new BoxEl
        {
            Direction = 0, Grow = 1f, Basis = 0f, MinWidth = 0f, AlignItems = FlexAlign.Center, ClipToBounds = true,
            Children = [leading],
        };
        return new BoxEl
        {
            Direction = 0, Grow = 1f, Basis = 0f, MinWidth = 0f, AlignItems = FlexAlign.Center, Gap = Spacing.S,
            ClipToBounds = true,
            Children = showExplicit ? [lead, ClassicExplicitBadge(nowPlaying ? Tok.AccentTextPrimary : null)] : [lead],
        };
    }

    static TextSpan[] BuildClassicSpans(Track t, int artistCount, bool showVideo, ColorF primary, ColorF secondary,
                                        ColorF tertiary)
    {
        int prefix = 1 + (showVideo ? 2 : 0) + (artistCount > 0 ? 1 : 0);
        var spans = new TextSpan[prefix + (artistCount == 0 ? 0 : artistCount * 2 - 1)];
        int n = 0;
        spans[n++] = new TextSpan(t.Title, Weight: 600, Color: primary);
        if (showVideo)
        {
            spans[n++] = new TextSpan(" ");
            spans[n++] = new TextSpan(Icons.Movie, Color: tertiary, FontFamily: Tok.Definition.Fonts.Icon);
        }
        if (artistCount > 0) spans[n++] = new TextSpan("  ·  ", Color: tertiary);
        var slots = t.ArtistSlots;
        for (int i = 0; i < artistCount; i++)
        {
            if (i > 0) spans[n++] = new TextSpan(", ", Color: secondary);
            var a = new Artist(i < slots.Length ? slots[i] : 0);
            spans[n++] = new TextSpan(a.Slot > 0 ? a.Name : "", Color: secondary, IsLink: a.Slot > 0 && a.Uri.IsValid);
        }
        return spans;
    }

    static Action<int> ClassicLinkClick(Track t, int artistCount, bool showVideo)
    {
        int first = 1 + (showVideo ? 2 : 0) + 1;   // head, [" ", film], the separator
        return index =>
        {
            int rel = index - first;
            if (artistCount > 0 && rel >= 0 && (rel & 1) == 0) Track.GoToArtistAt(t, rel / 2);
        };
    }

    /// <summary>A caller-built cell keyed for its lane: returned as-is when it already carries <paramref name="key"/>
    /// (the table row builds its "…" and chevron cells once, keyed), cloned with the key only otherwise.</summary>
    static Element Keyed(Element cell, string key) => cell.Key == key ? cell : cell with { Key = key };

    /// <summary>The ONE factual rung (ch 01 §0.6): Caption 12/16 Modern, 14/20 Classic, for every number, album, adder,
    /// date, count, tempo and duration.</summary>
    static TextEl RowFactualText(string text, bool classic, ColorF color) => new(text)
    {
        Size = classic ? 14f : 12f, LineHeight = classic ? 20f : 16f, Color = color,
        Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
    };

    /// <summary><see cref="RowFactualText"/> that FILLS its lane (Grow 1 · Basis 0 · MinWidth 0 — the Date and Classic
    /// Added-by lanes), built in one construction rather than a rung plus a `with` clone.</summary>
    static TextEl RowFactualFillText(string text, bool classic, ColorF color) => new(text)
    {
        Size = classic ? 14f : 12f, LineHeight = classic ? 20f : 16f, Color = color,
        Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
        Grow = 1f, Basis = 0f, MinWidth = 0f,
    };
}
