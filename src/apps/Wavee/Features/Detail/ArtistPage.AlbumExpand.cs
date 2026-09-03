using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Render;
using FluentGpu.Signals;
using Wavee.Core;
using Wavee.Features.Detail;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// iTunes-style inline album expansion for the artist discography grids: DiscographySection → DiscoGrid →
// AlbumDrawerPanel. Clicking an album card opens a FULL-WIDTH track drawer directly after that album's ROW (so the
// row's neighbours stay put and the rows below slide down), revealing the album's tracks in place — no navigation.
// Clicking the album again collapses it; clicking another moves the drawer. The grid is the virtualized LazyGrid
// (DiscoGrid owns the expanded uri + the one full-album fetch); the drawer body is AlbumDrawerPanel, fed LIVE via
// re-pushed props. (The earlier non-virtualized ExpandableAlbumGrid/AlbumDrawer pair is deleted — DiscoGrid subsumed it.)
//
// Every layout decision (rows, columns, loading/empty state, the reserved slot height) is ONE pure verdict —
// AlbumDrawerVerdict.For, docs/plans/wavee/artist-album-expander-implementation.md — computed from plain inputs so the
// panel, the reserved height and the bring-into-view scroll can never disagree, and a loaded album that is not the
// SELECTED one (C1) is simply "not loaded" rather than a stale tracklist under the wrong cover.

// Task<T> is invariant, so the Task<Album> the catalog returns is not a Task<Album?> — and the UseResource seed pins
// T = Album?. Awaiting and re-wrapping is the conversion; the loaders below all go through it.
static class AlbumLoader
{
    internal static async System.Threading.Tasks.Task<Album?> LoadAlbumAsync(Services svc, string uri, System.Threading.CancellationToken ct)
        => await svc.Library.GetAlbumAsync(uri, ct: ct).ConfigureAwait(false);

    /// <summary>C3: a SYNCHRONOUS store peek, passed as the UseResource seed so a re-opened (or already-warm) album is
    /// Ready on the very click frame — no shimmer for a fetch that would only re-confirm what the store already holds.
    /// Null for a cold/Identity-only album (TryPeekAlbum's own Open-or-better gate) or a closed drawer.</summary>
    internal static Album? Peek(Services svc, string uri)
        => uri.Length > 0 && svc.Library.TryPeekAlbum(uri, out var album) ? album : null;
}

// ── Discography facets (Albums / Singles / Compilations) ───────────────────────────────────────────────
// The artist page keeps each complete facet inline, virtualized over a VirtualCollection<Album> that pages as the outer
// page scrolls. The legacy facet route remains deep-link compatible, but ordinary catalogue browsing never leaves the
// artist page. Both surfaces share the same iTunes-style inline track drawer.

static class AlbumNavAction
{
    public static Element Create(Action onClick, float size = 34f) => ToolTip.Wrap(new BoxEl
    {
        Width = size, Height = size, Shrink = 0f,
        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Corners = CornerRadius4.All(size / 2f),
        BorderWidth = 1f, BorderColor = Tok.StrokeControlDefault,
        OnClick = onClick, Cursor = CursorId.Hand, Role = AutomationRole.Button, Focusable = true,
        Children = [ Icon(Icons.OpenInNewWindow, size * 0.42f, Tok.TextSecondary) ],
    }.Interactive(Interaction.Subtle), "Go to album");
}

// Builds the paged data source for one (artist, facet): pages of 60; the source reports the facet total from page 0.
static class DiscoVc
{
    public static VirtualCollection<Album> Make(Services svc, string artistUri, DiscographyKind kind, Action<Action> post, System.Threading.CancellationToken ct)
        => new(async (off, cnt, c) =>
        {
            var p = await svc.Library.GetDiscographyAsync(artistUri, kind, off, cnt, c);
            var arr = p.Items as Album[] ?? p.Items.ToArray();
            return new PageResult<Album>(p.Total, arr);
        }, pageSize: 60, post: post, ct: ct);

    /// <summary>The cheapest correct index lookup over a loaded window (C2): a linear scan of the resident items —
    /// there is no reverse uri→index map to keep in sync, and a discography facet is at most a few hundred albums.
    /// -1 when the album is not resident (its page has not landed yet, or it left the snapshot).</summary>
    public static int IndexOf(VirtualCollection<Album> vc, string uri)
    {
        int n = vc.CountOr0;
        for (int i = 0; i < n; i++)
            if (vc[i] is { } a && a.Uri == uri) return i;
        return -1;
    }
}

// The DiscoGrid expand drawer body. The discography album is THIN (no tracklist); the one full-album fetch lives in
// DiscoGrid (where the drawer SLOT is sized), and this panel receives the album + tracks + the ONE verdict as
// RE-PUSHED props — so the reserved height and the rendered rows derive from the SAME AlbumDrawerVerdict and cannot
// disagree.
sealed class AlbumDrawerPanel : Component
{
    readonly Services _svc;
    readonly Action<string> _play; readonly Action<string, string?> _go;
    readonly Func<ColorF> _accent;
    readonly SelectionModel _sel = new() { Mode = ItemsSelectionMode.Extended };
    readonly SwipeGroup _swipeGroup = new();
    // Non-reactive fields written from Render (the existing `_rows` pattern): the frozen selection-bar / context-menu
    // lambdas below, and the per-row DrawerTrackRow children (whose OWN ctor args freeze at THEIR mount — component-
    // props-contract), all read THESE live instead of a captured render-local, so they never see a stale value.
    Album _thin = null!;
    IReadOnlyList<Track> _rows = Array.Empty<Track>();
    DrawerVerdict _verdict;
    PlaybackBridge? _bridge;
    LibraryBridge? _lib;

    /// <summary>LIVE drawer slots re-pushed from DiscoGrid on every render (the SelectorBar/ToolTip props idiom). The
    /// panel's rows and loading/empty state all change AFTER mount (Pending → Ready on the same uri), and ctor args
    /// freeze — which is exactly how the old frozen <c>panelH</c> ctor argument locked the panel to the height computed
    /// on the expand frame. An immutable record so an unchanged re-push coalesces (no child re-render). The
    /// <c>"drawer:"+uri</c> Key still remounts per ALBUM, so per-album SelectionModel / SwipeGroup state stays scoped.
    /// <c>Verdict</c> is the ONE derivation (AlbumDrawerVerdict.For) — Rows/Columns/Loading/ReadyEmpty/ShowAllRow all
    /// come from it, so the panel body can never disagree with the reserved slot height DiscoGrid sized it from.</summary>
    internal sealed record Props(Album Thin, IReadOnlyList<Track> Tracks, DrawerVerdict Verdict, Action Retry);

    public AlbumDrawerPanel(Services svc, Action<string> play, Action<string, string?> go, Func<ColorF> accent)
    {
        _svc = svc; _play = play; _go = go; _accent = accent;
    }

    // # 26 · ♥ · title ★ · time 44 · trailing "…" 32 — the 32-DIP AlbumDrawerVerdict.RowPitch geometry (§3).
    static readonly ColumnSet DrawerCols = new(Album: false, By: false, Date: false, Video: false, Plays: false, Heart: true, Thumb: false);
    static readonly TrackSize[] DrawerColumns =
        [TrackSize.Px(26f), TrackSize.Px(TrackRow.HeartCol), TrackSize.Star(), TrackSize.Px(44f), TrackSize.Px(32f)];
    const float DrawerRowContentH = 28f;   // TrackRow.Grid's own content height; AlbumDrawerVerdict.RowPitch (32) is the slot around it

    public override Element Render()
    {
        var bridge = UseContext(PlaybackBridge.Slot);
        var lib = UseContext(LibraryBridge.Slot);
        var acts = UseContext(ActionServices.Slot);
        var menuOverlay = UseContext(Overlay.Service);   // row context menus (right-click / long-press / the "…" cell)
        var p = UsePropsOrDefault<Props>();              // subscribes → a re-push (Pending → Ready) re-renders here
        if (p is null) return new BoxEl();               // mounted without props (never happens from DiscoGrid)
        _thin = p.Thin;
        _rows = p.Tracks;
        _bridge = bridge;
        _lib = lib;
        _verdict = p.Verdict;
        var v = _verdict;
        _sel.ItemCount = v.Shown;                        // only the shown rows are interactive; the rest sit behind "Show all"

        Element body = v.ReadyEmpty ? EmptyNote(p.Retry)
                     : v.Loading ? ShimmerRows(v)
                     : Rows(p.Tracks, v, acts, menuOverlay);

        // No Height here: the panel HUGS its content — the OUTER drawer slot (DiscoGrid.DrawerHeight) owns the reserved
        // number, and both read the SAME verdict, so the slot never clips a row mid-row. ClipToBounds stays as the
        // belt-and-braces card clip.
        return new BoxEl
        {
            Direction = 1, ClipToBounds = true,
            Padding = new Edges4(12f, 6f, 12f, 6f),   // §3: compact chrome sized for the 32-DIP row pitch (was 16/6/16/6 for 44-DIP rows)
            Corners = CornerRadius4.All(Radii.Card), Fill = Tok.FillCardSecondary,
            BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            Children =
            [
                Head(),
                ZStack(body, Embed.Comp(() => new SelectionCommandBar(_sel,
                    // Live _rows/_verdict read (NOT a captured local): this factory freezes at mount — while the panel
                    // is still loading, a frozen row count would index past the empty track list.
                    i => (uint)i < (uint)Math.Min(_rows.Count, _verdict.Shown) ? _rows[i] : null,
                    bottomPadding: Spacing.S))),
            ],
        };
    }

    // "which album": the play glyph alone doesn't say WHAT is about to play once the drawer holds a full tracklist —
    // the cover (28×28, the same Surfaces.Artwork/seed as the discography card itself, so it's never a fresh decode)
    // plus the title/meta line (AlbumMeta — the card's OWN subtitle rule, so header and card can never disagree)
    // answer that at a glance. Row stays 28 tall so the panel's chrome doesn't grow.
    Element Head() => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, Height = 28f,
        Children =
        [
            new BoxEl { Width = 26f, Height = 26f, Shrink = 0f, Corners = CornerRadius4.All(13f), Fill = _accent(),
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, OnClick = () => _play(_thin.Uri),
                // Artwork-derived fill ⇒ ink from the FILL's luminance (the WaveeCta.Palette rule), never the theme's
                // on-accent token: the lifted cover accent is often pale, where TextOnAccentPrimary's glyph vanished.
                Children = [ Icon(Icons.Play, 11f, ColorContrast.PickContrast(_accent())) ] },
            Surfaces.Artwork(_thin.Cover, _thin.Uri.GetHashCode() & 0x7fffffff, 28f, 28f, Radii.Control),
            new BoxEl { Grow = 1f, Basis = 0f, MinWidth = 0f, OnClick = () => _go("album:" + _thin.Uri, _thin.Name),
                Children =
                [
                    // One paragraph, title + meta sharing a baseline (WaveeType.RailHeader's SpanTextEl idiom) — the
                    // meta run is DiscoGrid.AlbumMeta, the card's OWN subtitle text, so the two can never disagree.
                    new SpanTextEl(
                    [
                        new TextSpan(_thin.Name),
                        new TextSpan(" · " + DiscoGrid.AlbumMeta(_thin), Weight: 400, Color: Tok.TextSecondary, Size: 12f),
                    ])
                    {
                        Size = 13f, Weight = 600, Color = Tok.TextPrimary,
                        Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MaxLines = 1, MinWidth = 0f, Shrink = 1f,
                    },
                ] },
            AlbumNavAction.Create(() => _go("album:" + _thin.Uri, _thin.Name), 28f),
        ],
    };

    // Plain, keyed, EAGER rows (§3) — at most CapPerColumn*Columns of them, so there is nothing to virtualize.
    // Two columns (verdict.Columns == 2, ≥ 5 album grid columns) split the flat cell sequence column-major: the FIRST
    // ⌈n/2⌉ cells (numbered tracks read down, then across) go left, the rest right — the "Show all" row (when present)
    // is simply the last cell in that same sequence, so it lands wherever the split puts it.
    Element Rows(IReadOnlyList<Track> tracks, DrawerVerdict v, ActionServices? acts, IOverlayService? menuOverlay)
    {
        int shown = v.Shown;
        int cellCount = shown + (v.ShowAllRow ? 1 : 0);
        return BuildColumns(cellCount, v.Columns, i => i < shown
            ? TrackCell(i, tracks, shown, acts, menuOverlay)
            : ShowAllRow(v.Total));
    }

    Element TrackCell(int i, IReadOnlyList<Track> tracks, int n, ActionServices? acts, IOverlayService? menuOverlay)
    {
        if ((uint)i >= (uint)tracks.Count) return new BoxEl { Key = "row:" + i };
        var t = tracks[i];
        string key = "row:" + (t.Uri.Length > 0 ? t.Uri : "#" + i);

        BoxEl row = new BoxEl
        {
            // Height, not MinHeight: the wrapper IS the 32-DIP AlbumDrawerVerdict.RowPitch slot (content is 28), so a
            // column's stacked rows total EXACTLY rows*RowPitch — the same number the reserved slot was sized from.
            Key = key, ZStack = true, Height = AlbumDrawerVerdict.RowPitch, ClipToBounds = true,
            Corners = Radii.ControlAll,
            Fill = ColorF.Transparent, HoverFill = WaveeColors.RowHover, PressedFill = WaveeColors.RowPressed,
            Role = AutomationRole.Button, Cursor = CursorId.Hand,
            // A COPY source — an album drawer has no membership rows to move. The index is captured directly: rows
            // are eager/plain now (no recycling), so there is no promotion-time indirection to guard against.
            Draggable = Drag.Source(WaveeDragKinds.Resource, () => DrawerPayload(tracks, n, i)),
            // Single click SELECTS (Ctrl toggles, Shift extends from the anchor), DOUBLE click PLAYS — the same
            // contract every other track list in the app answers a click with (DetailTracks / ArtistPopular).
            OnPointerReleased = args =>
            {
                if (args.ClickCount >= 2) TrackRow.Invoke(_bridge, t, () => _ = _svc.Player.PlayAsync(_thin.Uri, i));
                else
                {
                    _sel.OnInteractedAction(i, (args.Mods & KeyModifiers.Ctrl) != 0, (args.Mods & KeyModifiers.Shift) != 0);
                    if ((args.Mods & KeyModifiers.Shift) == 0) _sel.AnchorIndex = i;
                }
            },
            Children = [ Embed.Comp(() => new DrawerTrackRow(this, i)) with { Key = "content" }, SelectionPill(i) ],
        };
        // Right-click / long-press / the row's "…" cell: the selection-aware track menu (Explorer semantics —
        // inside a multi-selection acts on all of it).
        if (acts is { } a && menuOverlay is { } ov)
            row = row.WithContextMenu(ov, () => TrackContextMenu.Build(
                a, _sel, j => (uint)j < (uint)n ? tracks[j] : null, i, static () => null));
        if (acts is null) return row;
        return RowSwipe.Wrap(row, new ActionContext(ActionTarget.ForTracks(new[] { t }), acts),
            _swipeGroup, TrackActions.ToggleLike, TrackActions.AddToQueue);
    }

    /// <summary>The WinUI ListView left accent selection bar (the app's one selection cue for a track row — see
    /// ArtistPopular.SelectionPill, the same idiom): ALWAYS mounted and revealed by a BOUND opacity over the model's
    /// Version, so selecting a row is a compositor-only re-skin, never a rebuild of this cell.</summary>
    Element SelectionPill(int index)
    {
        var sel = _sel;
        return new BoxEl
        {
            Key = "pill", Width = 3f, Height = 16f, Margin = new Edges4(2f, 0f, 0f, 0f),
            Corners = CornerRadius4.All(1.5f), AlignSelf = FlexAlign.Center,
            Fill = _accent(), HitTestVisible = false,
            Opacity = Prop.Of(() => { _ = sel.Version.Value; return sel.IsSelected(index) ? 1f : 0f; }),
        };
    }

    /// <summary>Past the cap (verdict.ShowAllRow), the last slot links to the full album page instead of growing the
    /// drawer — deterministic, counted in AlbumDrawerVerdict.Rows so the reserved height never has to guess.</summary>
    Element ShowAllRow(int total) => new BoxEl
    {
        Key = "row:show-all", Height = AlbumDrawerVerdict.RowPitch, AlignItems = FlexAlign.Center,
        Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f),
        Cursor = CursorId.Hand, Role = AutomationRole.Button,
        OnClick = () => _go("album:" + _thin.Uri, _thin.Name),
        Children =
        [
            new TextEl(Strings.Detail.Discography.ShowAllTracks(total))
                { Size = 12f, Weight = 600, Color = Tok.AccentTextPrimary },
        ],
    };

    /// <summary>The drawer row's drag payload: the whole SELECTION when the gesture starts on a selected row (Explorer
    /// semantics — the same rule the row's context menu follows), else that one track.</summary>
    WaveeResourceDragPayload? DrawerPayload(IReadOnlyList<Track> tracks, int n, int index)
    {
        if ((uint)index >= (uint)n) return null;
        if (!_sel.IsSelected(index)) return WaveeResourceDragPayload.ForTrack(tracks[index]);
        var picked = new List<Track>();
        for (int i = 0; i < _sel.ItemCount && i < n; i++)
            if (_sel.IsSelected(i)) picked.Add(tracks[i]);
        return WaveeResourceDragPayload.ForTracks(picked);
    }

    /// <summary>One track cell — a Component so the now-playing/buffering/selected re-skin a play/pause/skip anywhere
    /// in the app drives (TrackRow.StateOf reads the bridge's signals) re-renders only THIS row, not the whole drawer.
    /// Ctor args (the panel + this row's fixed index) are the only things that freeze at mount; the track/bridge/lib
    /// values themselves are read LIVE off the panel (<see cref="AlbumDrawerPanel._rows"/> etc.) every render, exactly
    /// like ArtistPopular.ChartRow — a plain field is not a signal, so this is sound only because the ENCLOSING panel
    /// (keyed per album) re-renders on every props push and walks back into this same-keyed child.</summary>
    sealed class DrawerTrackRow : Component
    {
        readonly AlbumDrawerPanel _o;
        readonly int _index;
        public DrawerTrackRow(AlbumDrawerPanel o, int index) { _o = o; _index = index; }

        readonly record struct Presentation(Track? Track, TrackRow.State State);

        public override Element Render()
        {
            var presentation = UseComputed(() =>
            {
                var tracks = _o._rows;
                if ((uint)_index >= (uint)tracks.Count) return default(Presentation);
                var t = tracks[_index];
                return new Presentation(t, TrackRow.StateOf(_o._bridge, _o._lib, t));
            });
            if (presentation.Value.Track is not { } t) return new BoxEl();
            var st = presentation.Value.State;
            Element title = new TextEl(t.Title)
            {
                Size = 13f,
                Weight = 600,
                Color = st.IsNow ? Tok.AccentTextPrimary : Tok.TextPrimary,
                MaxLines = 1,
                Trim = TextTrim.CharacterEllipsis,
                MinWidth = 0f,
            };
            return TrackRow.Grid(t, _index, st, DrawerCols, DrawerColumns, DrawerRowContentH, title, showTrackArtist: false, _o._go,
                onPlay: () => TrackRow.Invoke(_o._bridge, t, () => _ = _o._svc.Player.PlayAsync(_o._thin.Uri, _index)),
                onLike: t.Uri.Length > 0 ? () => _o._lib?.ToggleSaved(t.Uri, t.Title) : null,
                actionsCell: TrackRow.MoreButton(true));   // "…" raises the row's context request (ClickRequestsContext)
        }
    }

    // READY BUT EMPTY. GetAlbumAsync swallows its fetch failure (StoreLibrarySource.EnsureFetchedAsync's `catch { }`)
    // and returns whatever the store holds, so the resource legitimately settles Ready on a trackless album — offline,
    // a failed envelope, or a cold-restored stub. That used to be a 0-row slot with nothing in it and no way out:
    // VirtualCollection has no invalidation, so the stale snapshot never healed. Retry re-runs the loader keeping the
    // current value visible (Resource.Refresh — stale-while-revalidate). Sized by AlbumDrawerVerdict's ReadyEmpty
    // branch (2 rows) in the slot.
    static Element EmptyNote(Action retry) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M,
        Padding = new Edges4(Spacing.S, Spacing.M, Spacing.S, Spacing.M),
        Children =
        [
            new TextEl(Loc.Get(Strings.Detail.Empty.NoTracks))
                { Grow = 1f, Basis = 0f, MinWidth = 0f, Size = 13f, Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
            Button.Standard(Loc.Get(Strings.Common.Retry), retry),
        ],
    };

    // Shimmer at the SAME pitch and SAME column split as the real rows (verdict.Shown of them) — so the placeholder
    // never has to reflow into the real content's shape once it lands.
    static Element ShimmerRows(DrawerVerdict v) => BuildColumns(v.Shown, v.Columns, ShimmerRow);

    static Element ShimmerRow(int i) => new BoxEl
    {
        Key = "shimmer:" + i,
        Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, Height = AlbumDrawerVerdict.RowPitch, Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f),
        Children =
        [
            new BoxEl { Width = 16f, Height = 11f, Corners = CornerRadius4.All(4f), Fill = Tok.FillSubtleSecondary },
            new BoxEl { Grow = 1f, Basis = 0f, Height = 11f, MaxWidth = 240f, Corners = CornerRadius4.All(4f), Fill = Tok.FillSubtleSecondary },
            new BoxEl { Width = 30f, Height = 11f, Corners = CornerRadius4.All(4f), Fill = Tok.FillSubtleSecondary },
            new BoxEl { Width = 32f },   // the reserved "…" lane (matches DrawerColumns' trailing 32px)
        ],
    };

    /// <summary>Lay out <paramref name="cellCount"/> cells either as one stack or, at <paramref name="columns"/> == 2,
    /// as two parallel column-major stacks (§3) — the ONE splitter shared by the real rows and the shimmer so they can
    /// never disagree about how tall a column runs (both derive their height from the SAME AlbumDrawerVerdict.Rows,
    /// which is exactly ⌈cellCount / columns⌉).</summary>
    static Element BuildColumns(int cellCount, int columns, Func<int, Element> cell)
    {
        if (cellCount <= 0) return new BoxEl();
        if (columns <= 1)
        {
            var kids = new Element[cellCount];
            for (int i = 0; i < cellCount; i++) kids[i] = cell(i);
            return new BoxEl { Direction = 1, Children = kids };
        }
        int perCol = (int)Math.Ceiling(cellCount / (float)columns);
        var cols = new Element[columns];
        for (int c = 0; c < columns; c++)
        {
            int start = c * perCol, end = Math.Min(cellCount, start + perCol);
            var kids = end > start ? new Element[end - start] : Array.Empty<Element>();
            for (int i = start; i < end; i++) kids[i - start] = cell(i);
            cols[c] = new BoxEl { Key = "col:" + c, Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Children = kids };
        }
        return new BoxEl { Direction = 0, Gap = Spacing.XL, Children = cols };
    }
}

sealed class DiscoGrid : Component
{
    readonly VirtualCollection<Album> _vc;
    readonly Services _svc;
    readonly Action<string, string?> _go;
    readonly Action<string> _play;
    readonly int _initialIndex;
    readonly Action<LazyGridVisibleRange>? _visibleRangeChanged;
    readonly float _expandedTopInset;
    readonly float _expandedRevealPeek;
    static readonly Func<ColorF> ThemeAccent = static () => Tok.AccentDefault;
    readonly Func<ColorF> _accent;
    // C2: identity, not an ordinal — the index LazyGrid still needs (its own contract is untouched, engine-owned) is
    // DERIVED from this uri every render (below) and written into `_expandedIndex`, so a late-arriving page or a facet
    // replace re-points (or closes) the drawer instead of leaving a stale index pointed at whatever now sits there.
    readonly Signal<string> _expandedUri = new("");
    readonly Signal<int> _expandedIndex = new(-1);
    ActionServices? _acts;            // card context menus (Menus.CardAttach) — resolved in Render, read by Cell
    IOverlayService? _menuOverlay;

    const float MinCol = 180f;
    static readonly float Gap = Spacing.L;          // column gap (the vertical row gap is RowGap, folded into rowExtra)

    // Uniform-card geometry → predictable drawer spacing. GridCard's cover is cardW-16; adding 20px vertical padding,
    // an 8px card gap, and 38px for one title + one metadata line yields an exact cardW+50 card. Keeping this separate
    // from RowGap leaves an actual gutter instead of flex-growing the card through it.
    const float CardChrome = 50f;
    const float RowGap     = 20f;       // vertical gap between card rows  (rowExtra = CardChrome + RowGap)

    // The drawer caret ("this card opened") — a 16-wide, 8-tall wedge, exactly AlbumDrawerVerdict.TopGap tall so it
    // fills the reserved band above the panel. CaretGeometry is built ONCE (cold, at class init): the shape never
    // changes, only its OffsetX/OffsetY per open card, so every render reuses the same PathData with no re-tessellation.
    const float CaretW = 16f, CaretH = 8f, CaretOverlap = 1f;
    static readonly PathData CaretGeometry = BuildCaretGeometry();

    static PathData BuildCaretGeometry()
    {
        var b = new PathBuilder();
        b.MoveTo(0f, CaretH);
        b.LineTo(CaretW * 0.5f, 0f);
        b.LineTo(CaretW, CaretH);
        b.Close();
        return b.Finish(PathContentEpoch.Mint(), FillRule.NonZero);
    }

    Resource<Album?> _full;             // the ONE full-album fetch for the open drawer (re-assigned every render)
    // C4: the LAST verdict DrawerFor computed. DrawerFor recomputes it fresh on every call — LazyGrid re-renders
    // whenever `_expandedIndex`/`_full.Loadable` change (reading them HERE, inside a delegate LazyGrid invokes from
    // its own Render, subscribes LazyGrid transitively — the same trick the old `_drawer` UseComputed memo relied on).
    // DrawerHeight, which LazyGrid calls BEFORE DrawerFor on every render pass, reads this CACHED value rather than
    // recomputing one that does not exist yet: the reserved slot lags DrawerFor by at most one LazyGrid render, which
    // self-heals on the very next signal-driven re-render (there is no engine ordering that lets it read "this frame's"
    // GridDrawerInfo.Columns before the frame that produces it).
    DrawerVerdict _verdict;
    int _lastGridCols = 1;              // last GridDrawerInfo.Columns DrawerFor saw; refreshed there before the verdict recompute
    Action? _retryFull;                 // stable delegate so the re-pushed Props coalesce (a fresh lambda would defeat record equality)

    // The inline drawer's open/close + switch motion (§4). Outer = SIZE ONLY (Reflow, so the card rows below travel
    // with it), 200ms in / 150ms out, SuppressDescendantTransitions so content landing mid-open cannot start a second
    // wave (C5). Inner = OPACITY ONLY, no Dy — the drawer's own height reveal IS the movement; a same-row album switch
    // is a 150ms cross-fade of the per-album panel under the same slot.
    static readonly LayoutTransition DrawerResize = new(
        TransitionChannels.Size, TransitionDynamics.Tween(200f, Easing.SmoothOut),
        Enter: new EnterExit(Active: true), Exit: new EnterExit(Active: true),
        ExitDynamics: TransitionDynamics.Tween(150f, Easing.SmoothOut),
        Size: SizeMode.Reflow, Anchor: SizeAnchor.Leading, SuppressDescendantTransitions: true);

    static readonly LayoutTransition DrawerPresence = new(
        TransitionChannels.Opacity, TransitionDynamics.Tween(150f, Easing.EaseInOut),
        Enter: new EnterExit(Opacity: 0f, Active: true), Exit: new EnterExit(Opacity: 0f, Active: true),
        ExitDynamics: TransitionDynamics.Tween(100f, Easing.EaseInOut));

    public DiscoGrid(VirtualCollection<Album> vc, Services svc, Action<string, string?> go, Action<string> play,
                     int initialIndex = 0, Func<ColorF>? accent = null,
                     Action<LazyGridVisibleRange>? onVisibleRangeChanged = null, float expandedTopInset = 28f,
                     float expandedRevealPeek = AlbumDrawerVerdict.HeaderH + 2f * AlbumDrawerVerdict.RowPitch)
    {
        _vc = vc; _svc = svc; _go = go; _play = play; _initialIndex = initialIndex;
        _accent = accent ?? ThemeAccent;
        _visibleRangeChanged = onVisibleRangeChanged;
        _expandedTopInset = expandedTopInset;
        _expandedRevealPeek = expandedRevealPeek;
    }

    public override Element Render()
    {
        _acts = UseContext(ActionServices.Slot);
        _menuOverlay = UseContext(Overlay.Service);

        _ = _vc.Version.Value;                        // subscribe → the expanded uri resolves once its page lands
        string expandedUri = _expandedUri.Value;       // C2: subscribe → the URI is the source of truth, never an index

        // Re-derive LazyGrid's index contract from the uri every render — a late page landing or a facet replace
        // re-points it (or closes it, -1) instead of leaving a stale ordinal pointed at whatever now sits at that slot.
        int expandedIndex = expandedUri.Length == 0 ? -1 : DiscoVc.IndexOf(_vc, expandedUri);
        if (_expandedIndex.Peek() != expandedIndex) _expandedIndex.Value = expandedIndex;

        // ONE fetch for the open drawer, keyed by uri (collapsed ⇒ "" ⇒ a completed no-op task, never a request).
        // Lifted out of AlbumDrawerPanel so the row count is known where the drawer's SLOT is sized. GetAlbumAsync is
        // cached, so re-expanding the same album costs nothing — and C3's warm seed means it costs no SHIMMER either.
        _full = UseResource(ct => expandedUri.Length == 0
                ? System.Threading.Tasks.Task.FromResult<Album?>(null)
                : AlbumLoader.LoadAlbumAsync(_svc, expandedUri, ct),
            AlbumLoader.Peek(_svc, expandedUri), expandedUri);

        return Embed.Comp(() => new LazyGrid(
        // The count is the whole facet. LazyGrid realizes only the viewport window plus overscan.
        count: Count,
        cell: Cell,
        ensureRange: (f, l) => _vc.EnsureRange(f, l - 1),
        minColWidth: MinCol, gap: Gap, rowExtra: CardChrome + RowGap, overscanRows: 4,
        expanded: _expandedIndex,
        drawer: DrawerFor,
        drawerHeight: DrawerHeight,
        initialIndex: _initialIndex,
        onVisibleRangeChanged: _visibleRangeChanged,
        expandedTopInset: _expandedTopInset,
        expandedRevealPeek: _expandedRevealPeek,
        // AlignTop, not Minimal: the user's verdict was "unclear where it opens, still jumpy" — a minimal reveal
        // scrolls just enough to peek the drawer, which lands the clicked row at a DIFFERENT offset every time
        // depending on where it started. AlignTop always parks the clicked row directly under the sticky facet
        // band, so the drawer opens in the SAME place on every click.
        reveal: ExpandedReveal.AlignTop));
    }

    int Count()
    {
        _ = _vc.Version.Value;
        return _vc.CountOr0;
    }

    Element Cell(int idx, float cardW)
    {
        var al = _vc![idx];
        if (al is null) return Placeholder(cardW);
        string subtitle = AlbumMeta(al);
        Element card = MediaCard.GridCard(al.Cover, al.Name, subtitle, al.Uri,
            onClick: () => _expandedUri.Value = _expandedUri.Peek() == al.Uri ? "" : al.Uri,
            onPlay: () => _play(al.Uri),
            onNavigate: () => _go("album:" + al.Uri, al.Name),
            accent: Surfaces.SchemeFor(al.Cover?.Url) is { } p ? WaveePalette.Lift(WaveePalette.Accent(p)) : null,
            menu: _menuOverlay is { } ov ? Menus.CardAttach(_acts, ov, al.Uri, al.Name, al.Cover, subtitle) : null,
            drag: Drag.Source(WaveeDragKinds.Resource,
                () => WaveeResourceDragPayload.ForEntity(WaveeResourceKind.Album, al.Uri, al.Name, al.Cover, _acts)));
        if (card is BoxEl b)
        {
            // Force ONE height (square cover + chrome) so every card is uniform → the drawer's hug spacing is exact.
            b = b with { Key = "album:" + al.Uri, Height = cardW + CardChrome };
            // Highlight the expanded card (accent border + brighter fill) so it's unmistakably the drawer's owner —
            // pairs with the caret at the drawer's top edge. Compared by URI (C2), not by index.
            if (_expandedUri.Peek() == al.Uri)
                b = b with { BorderColor = _accent(), BorderWidth = 2f, Fill = Tok.FillCardDefault };
            card = b;
        }
        return card;
    }

    /// <summary>The card's subtitle line ("date · N tracks") — the ONE rule, shared by the discography card (<see cref="Cell"/>)
    /// and the drawer header (<see cref="AlbumDrawerPanel.Head"/>), so the two can never read different metadata for
    /// the same album.</summary>
    internal static string AlbumMeta(Album album)
    {
        string date = ReleaseDateLabel(album);
        return album.TrackCount > 0
            ? date.Length > 0
                ? Strings.Artist.ReleaseMeta(date, Strings.Artist.TrackCount(album.TrackCount))
                : Strings.Artist.TrackCount(album.TrackCount)
            : date;
    }

    static string ReleaseDateLabel(Album album)
    {
        if (album.ReleaseDate is not { Length: > 0 } iso ||
            !DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
            return album.Year > 0 ? album.Year.ToString(CultureInfo.InvariantCulture) : "";
        CultureInfo culture;
        try { culture = CultureInfo.GetCultureInfo(Loc.CurrentCulture); }
        catch (CultureNotFoundException) { culture = CultureInfo.InvariantCulture; }
        return (album.ReleaseDatePrecision ?? "").ToUpperInvariant() switch
        {
            "YEAR" => date.ToString("yyyy", culture),
            "MONTH" => date.ToString("MMM yyyy", culture),
            _ => date.ToString("MMM d, yyyy", culture),
        };
    }

    internal static string ReleaseYearLabel(Album album)
    {
        if (album.Year > 0) return album.Year.ToString(CultureInfo.InvariantCulture);
        return album.ReleaseDate is { Length: >= 4 } date ? date[..4] : "";
    }

    // A self-sizing shimmer cell, SAME height as a real card: the cover fills the (engine-laid-out) cell width and squares
    // itself via AspectRatio — no hardcoded width, so it tracks the real card exactly. The bars stretch to the cell width.
    static Element Placeholder(float cardW) => new BoxEl
    {
        Key = "album:placeholder",
        Direction = 1, Gap = Spacing.S, Height = cardW + CardChrome,
        Padding = new Edges4(Spacing.S, Spacing.S, Spacing.S, Spacing.M),
        // Borderless like the restyled GridCard's resting state (the plate is hover-only now) — a plated skeleton
        // would flash a different silhouette than the card it becomes.
        Corners = CornerRadius4.All(Radii.Card),
        Children =
        [
            // Fluid square cover: Width left NaN + AspectRatio 1f → fills the engine-laid-out cell width and derives its
            // height (the same self-sizing the real ArtworkFill cover uses) — no hardcoded dimensions.
            new ImageEl { Source = "", AspectRatio = 1f, AlignSelf = FlexAlign.Stretch, Corners = CornerRadius4.All(Radii.Card), Placeholder = Tok.FillSubtleSecondary },
            new BoxEl { Height = 13f, AlignSelf = FlexAlign.Stretch, MaxWidth = 150f, Corners = CornerRadius4.All(4f), Fill = Tok.FillSubtleSecondary },
            new BoxEl { Height = 11f, Width = 92f, Corners = CornerRadius4.All(4f), Fill = Tok.FillSubtleSecondary },
        ],
    };

    // DrawerHeight is called by LazyGrid.Render() BEFORE DrawerFor on every pass (see the `_verdict` field doc) — it
    // reads the cached verdict rather than recomputing one that does not exist yet for this frame.
    float DrawerHeight(int idx) { _ = idx; return _verdict.SlotHeight; }

    Element DrawerFor(int idx, GridDrawerInfo info)
    {
        var al = _vc?[idx];
        if (al is null) return new BoxEl();
        _retryFull ??= () => _full.Refresh();
        _lastGridCols = info.Columns;

        // THE verdict, recomputed fresh on every call (see the `_verdict` field doc for why LazyGrid keeps calling
        // this whenever it needs to and why caching here is still correct).
        var loaded = _full.Loadable.Value.Value;
        bool pending = _full.Loadable.State.Value == (byte)LoadState.Pending;
        _verdict = AlbumDrawerVerdict.For(al.Uri, loaded?.Uri, loaded?.Tracks?.Count ?? 0,
            al.Tracks?.Count ?? 0, al.TrackCount, pending, _lastGridCols);

        // Mirrors the verdict's own identity rule (C1) to hand the PANEL the real list, not just its count: a loaded
        // album that is not the selected one is exactly as stale as the verdict already says it is.
        bool match = loaded is not null && loaded.Uri == al.Uri;
        IReadOnlyList<Track> tracks = match ? (loaded!.Tracks ?? Array.Empty<Track>()) : (al.Tracks ?? Array.Empty<Track>());

        // Re-pushed PROPS, not ctor args. The panel's rows and loading/empty state all change AFTER mount
        // (Pending → Ready on the same uri), and ctor args freeze — which is exactly how the old frozen `panelH`
        // ctor argument locked the panel to the height computed on the expand frame. The Key still remounts per
        // ALBUM, so per-album SelectionModel / SwipeGroup state stays scoped.
        var panel = Embed.Comp(
            new AlbumDrawerPanel.Props(al, tracks, _verdict, _retryFull),
            () => new AlbumDrawerPanel(_svc, _play, _go, _accent))
            with { Key = "drawer:" + al.Uri };

        // Caret: "this card opened" — a 16×8 wedge apexed on the clicked card's own column, in the slot's TopGap band
        // (pushed the panel down by TopGap below), pairing with the card's accent border so the drawer visibly belongs
        // to that one card rather than floating below the row. Clamped so the wedge never overhangs the panel's own
        // rounded corners (Radii.Card on each side).
        float panelWidth = info.Columns * info.CellWidth + MathF.Max(0f, info.Columns - 1) * info.Gap;
        float apexX = Math.Clamp(info.Left + info.CellWidth / 2f, Radii.Card, MathF.Max(Radii.Card, panelWidth - Radii.Card));
        float caretX = apexX - CaretW / 2f;
        // Overlaps the panel by CaretOverlap (1 DIP): the caret is painted AFTER the panel (last in the ZStack), so
        // its fill sinks 1 DIP into the panel and hides the panel's top border stroke across the caret's base —
        // otherwise a hairline would cut straight across the wedge where it meets the panel.
        float caretY = AlbumDrawerVerdict.TopGap - CaretH + CaretOverlap;
        Element caretFill = new PathEl
        {
            OffsetX = caretX, OffsetY = caretY, Width = CaretW, Height = CaretH,
            Geometry = CaretGeometry, Fill = Tok.FillCardSecondary, Rule = FillRule.NonZero,   // a PathEl never owns a click (engine contract) — no HitTestVisible needed
        };
        Element caretStroke = new PolylineStrokeEl
        {
            OffsetX = caretX, OffsetY = caretY, Width = CaretW, Height = CaretH,
            P0 = new Point2(0f, CaretH), P1 = new Point2(CaretW * 0.5f, 0f), P2 = new Point2(CaretW, CaretH),
            PointCount = 3, Color = Tok.StrokeCardDefault, Thickness = 1f, RoundCaps = false,
        };

        // The OUTER slot owns the transition because its height participates in parent layout. The stable key keeps ONE
        // node across A→B switches (Position/Size-reflow); a true open/close runs the enter/exit channels. The panel is
        // pushed down by TopGap (an empty spacer) so the caret has room to sit above it; caret is painted LAST so it
        // draws over the panel's top edge (see the overlap note above).
        var inner = new BoxEl
        {
            ZStack = true, Animate = DrawerPresence,
            Children =
            [
                new BoxEl { Direction = 1, Children = [ new BoxEl { Height = AlbumDrawerVerdict.TopGap, HitTestVisible = false }, panel ] },
                caretFill,
                caretStroke,
            ],
        };
        return new BoxEl
        {
            Key = "disco-drawer", Direction = 1,
            Height = _verdict.SlotHeight,
            ClipToBounds = true, Animate = DrawerResize,
            Children = [ inner ],
        };
    }
}

// One artist-page discography facet. The complete resident collection stays inline and UI-virtualized; the stock Expander
// owns disclosure semantics and motion. Era ranges are display-only metadata on the one sticky heading: grid rows never
// restart and no late-arriving structure can change the section's measured extent while the user is scrolling.
sealed class DiscographySection : Component
{
    internal sealed record Props(Album[] Items);

    readonly string _title;
    readonly DiscographyKind _kind;
    readonly Services _svc;
    readonly Action<string, string?> _go;
    readonly Action<string> _play;
    readonly Func<ColorF> _accent;
    readonly Signal<LazyGridVisibleRange> _visible = new(default);
    readonly Signal<bool> _gridClipped = new(false);
    VirtualCollection<Album>? _vc;
    int _snapshotKey, _snapshotCount = -1;
    TemplateParts? _parts;

    const float HeaderRowH = 40f;
    const float StickyInset = ArtistHeroLayout.CompactIdentityHeight + HeaderRowH;

    public DiscographySection(DiscographyKind kind, string title, Services svc,
                              Action<string, string?> go, Action<string> play, Func<ColorF> accent)
    {
        _kind = kind; _title = title; _svc = svc;
        _go = go; _play = play; _accent = accent;
    }

    public override Element Render()
    {
        var props = UsePropsOrDefault<Props>();
        Album[] items = props?.Items ?? Array.Empty<Album>();
        int snapshotKey = SnapshotKey(items);
        _vc ??= VirtualCollection<Album>.FromSnapshot(items);
        if (_snapshotCount < 0) { _snapshotKey = snapshotKey; _snapshotCount = items.Length; }
        UseEffect(() =>
        {
            if (_snapshotKey == snapshotKey && _snapshotCount == items.Length) return;
            _snapshotKey = snapshotKey;
            _snapshotCount = items.Length;
            _vc!.ReplaceSnapshot(items);
            if (_visible.Peek() != default) _visible.Value = default;
        }, DepKey.From(snapshotKey, items.Length));
        var eras = DiscographyEraBands.PlanAlbums(items);

        // The real section heading pins directly below the compact artist bar. The grid owns one subtree clip at the
        // combined inset, so cards pass behind neither painted row and no signal-driven surrogate header is needed.
        Element header = Header(items.Length, eras);
        Element grid = new BoxEl
        {
            Direction = 1,
            EdgeFade = _gridClipped.Value
                ? new EdgeFadeSpec(EdgeMask.Top, DetailVerticalLayout.StickyFadeBand)
                : null,
            Children =
            [
                Embed.Comp(() => new DiscoGrid(_vc!, _svc, _go, _play,
                    accent: _accent,
                    onVisibleRangeChanged: OnVisibleRangeChanged,
                    expandedTopInset: StickyInset)),
            ],
        }.ClipBelow(StickyInset, clipped => { if (_gridClipped.Peek() != clipped) _gridClipped.Value = clipped; });

        var parts = Parts();

        return new BoxEl
        {
            Direction = 1,
            Children =
            [
                // AnimateContentResize = false: without it the Expander's own disclosure Reflow (333ms) replays on
                // EVERY height change of its content — including a drawer opening/closing inside the grid — so one
                // click played TWO height tweens (the drawer's own DrawerResize plus the section reflowing under it).
                // Off ⇒ the section only tweens on an actual expand/collapse toggle: one motion per click.
                Embed.Comp(new Expander.ExpanderSlots(header, grid, parts),
                    () => new Expander { InitiallyExpanded = true, Options = new ExpanderOptions { AnimateContentResize = false } }),
                new BoxEl { Height = Spacing.XXL, HitTestVisible = false },
            ],
        };
    }

    TemplateParts Parts()
    {
        if (_parts is not null) return _parts;
        return _parts = new TemplateParts
        {
            [Expander.PartHeader] = element => element with
            {
                MinHeight = HeaderRowH,
                Padding = new Edges4(0f, Spacing.XS, Spacing.S, Spacing.XS),
                Fill = ColorF.Transparent,
                HoverFill = ColorF.Transparent,
                PressedFill = ColorF.Transparent,
                BorderWidth = 0f,
                Corners = CornerRadius4.All(0f),
                BrushTransitionMs = 0f,
                ScrollBinds = [new() { PinTop = ArtistHeroLayout.CompactIdentityHeight }],
            },
            [Expander.PartChevron] = element => element with
            {
                Width = 28f, Height = 28f,
                Margin = new Edges4(Spacing.S, 0f, 0f, 0f),
            },
            [Expander.PartContent] = element => element with
            {
                Padding = Edges4.All(0f), MinHeight = 0f, Margin = Edges4.All(0f),
                Fill = ColorF.Transparent, BorderWidth = 0f,
                Corners = CornerRadius4.All(0f),
            },
        };
    }

    Element Header(int total, DiscographyEraBand[]? eras)
    {
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, MinWidth = 0f,
            Children =
            [
                new BoxEl
                {
                    Width = 3f, MinHeight = 22f, AlignSelf = FlexAlign.Stretch,
                    Corners = CornerRadius4.All(Radii.Pill), Fill = _accent(), HitTestVisible = false,
                },
                Embed.Comp(new DiscographyFacetHeaderLabel.Props(_title, total, eras),
                    () => new DiscographyFacetHeaderLabel(_visible)) with
                {
                    Key = "facet-label:" + (int)_kind,
                },
                new BoxEl { Grow = 1f, Basis = 0f, MinWidth = 0f },
            ],
        };
    }

    void OnVisibleRangeChanged(LazyGridVisibleRange range)
    {
        if (_visible.Peek() != range) _visible.Value = range;
    }

    static int SnapshotKey(Album[] items)
    {
        var hash = new HashCode();
        hash.Add(items.Length);
        for (int i = 0; i < items.Length; i++)
        {
            var item = items[i];
            hash.Add(item.Uri, StringComparer.Ordinal);
            hash.Add(item.Name, StringComparer.Ordinal);
            hash.Add(item.Year);
            hash.Add(item.ReleaseDate, StringComparer.Ordinal);
            hash.Add(item.TrackCount);
            hash.Add(item.Cover?.Url, StringComparer.Ordinal);
        }
        return hash.ToHashCode();
    }
}

/// <summary>The only reactive leaf in the pinned facet heading. Visible-window changes replace one fixed-line text run;
/// they never rerender the Expander, reflow the grid, or write the page scroll offset.</summary>
sealed class DiscographyFacetHeaderLabel : Component
{
    internal sealed record Props(string Title, int Total, DiscographyEraBand[]? Eras);

    readonly IReadSignal<LazyGridVisibleRange> _visible;

    public DiscographyFacetHeaderLabel(IReadSignal<LazyGridVisibleRange> visible) => _visible = visible;

    public override Element Render()
    {
        var props = UsePropsOrDefault<Props>() ?? new Props("", 0, null);
        var visible = _visible.Value;
        string meta = props.Total > 0 ? Strings.Artist.ReleaseCount(props.Total) : "";
        var eras = props.Eras;
        if (eras is not null && eras.Length > 0)
        {
            int index = visible.LastIndexExclusive > visible.FirstIndex ? visible.FirstIndex : 0;
            if (DiscographyEraBands.AtIndex(eras, index) is { } era && era.Label.Length > 0)
            {
                meta = era.Label + " · " + Strings.Artist.ReleaseCount(era.Count);
            }
        }

        return meta.Length > 0
            ? WaveeType.RailHeader(props.Title, meta)
            : WaveeType.RailHeader(props.Title) with
            {
                MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            };
    }
}
