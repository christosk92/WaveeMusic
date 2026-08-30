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
using FluentGpu.Signals;
using Wavee.Core;
using Wavee.Features.Detail;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// iTunes-style inline album expansion for the artist discography grids: DiscographySection → DiscoGrid →
// AlbumDrawerPanel. Clicking an album card opens a FULL-WIDTH track drawer directly after that album's ROW (so the
// row's neighbours stay put and the rows below slide down), revealing the album's tracks in place — no navigation.
// Clicking the album again collapses it; clicking another moves the drawer. The grid is the virtualized LazyGrid
// (DiscoGrid owns the expanded index + the one full-album fetch); the drawer body is AlbumDrawerPanel, fed LIVE via
// re-pushed props. (The earlier non-virtualized ExpandableAlbumGrid/AlbumDrawer pair is deleted — DiscoGrid subsumed it.)

// Task<T> is invariant, so the Task<Album> the catalog returns is not a Task<Album?> — and the UseResource seed pins
// T = Album?. Awaiting and re-wrapping is the conversion; the loaders below all go through it.
static class AlbumLoader
{
    internal static async System.Threading.Tasks.Task<Album?> LoadAlbumAsync(Services svc, string uri, System.Threading.CancellationToken ct)
        => await svc.Library.GetAlbumAsync(uri, ct: ct).ConfigureAwait(false);
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
}

// The DiscoGrid expand drawer body. The discography album is THIN (no tracklist); the one full-album fetch lives in
// DiscoGrid (where the drawer SLOT is sized), and this panel receives the album + tracks + row/state verdict as
// RE-PUSHED props — so the reserved height and the rendered rows derive from the SAME DrawerState and cannot disagree.
sealed class AlbumDrawerPanel : Component
{
    readonly Services _svc;
    readonly Action<string> _play; readonly Action<string, string?> _go;
    readonly Func<ColorF> _accent;
    readonly SelectionModel _sel = new();
    readonly SwipeGroup _swipeGroup = new();
    readonly Func<bool> _showChecks;
    // Non-reactive fields written from Render (the existing `_rows` pattern): the frozen selection-bar / context-menu
    // lambdas below read THESE, so they always see the current album/tracks without re-registration.
    Album _thin = null!;
    IReadOnlyList<Track> _rows = Array.Empty<Track>();

    /// <summary>LIVE drawer slots re-pushed from DiscoGrid on every render (the SelectorBar/ToolTip props idiom). The
    /// panel's rows and loading/empty state all change AFTER mount (Pending → Ready on the same uri), and ctor args
    /// freeze — which is exactly how the old frozen <c>panelH</c> ctor argument locked the panel to the height computed
    /// on the expand frame. An immutable record so an unchanged re-push coalesces (no child re-render). The
    /// <c>"drawer:"+uri</c> Key still remounts per ALBUM, so per-album SelectionModel / SwipeGroup state stays scoped.</summary>
    internal sealed record Props(Album Thin, IReadOnlyList<Track> Tracks, int Rows, bool Loading, bool ReadyEmpty, Action Retry);

    public AlbumDrawerPanel(Services svc, Action<string> play, Action<string, string?> go, Func<ColorF> accent)
    {
        _svc = svc; _play = play; _go = go; _accent = accent;
        _showChecks = () => { _ = _sel.Version.Value; return _sel.SelectedCount > 1; };   // 2+ only (a plain click must not summon checkboxes or the batch bar)
    }

    static readonly ColumnSet DrawerCols = new(Album: false, By: false, Date: false, Video: false, Plays: false, Heart: true, Thumb: false);
    static readonly TrackSize[] DrawerColumns =
        [TrackSize.Px(30f), TrackSize.Px(TrackRow.HeartCol), TrackSize.Star(), TrackSize.Px(52f), TrackSize.Px(40f)];   // trailing "…" lane
    const float DrawerRowContentH = 40f;

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
        int n = p.Rows;                                  // the ONE row verdict — same DrawerState the slot was sized from

        Element body = p.ReadyEmpty ? EmptyNote(p.Retry)
                     : p.Loading ? ShimmerRows(n)
                     : Rows(p.Tracks, n, bridge, lib, acts, menuOverlay);

        // No Height here: the panel HUGS its content — the OUTER drawer slot (DiscoGrid.DrawerHeight) owns the reserved
        // number, and both read the same DrawerState, so the slot never clips a row mid-row. ClipToBounds stays as the
        // belt-and-braces card clip.
        return new BoxEl
        {
            Direction = 1, ClipToBounds = true,
            Padding = new Edges4(Spacing.L, Spacing.S, Spacing.L, Spacing.S),
            Corners = CornerRadius4.All(Radii.Card), Fill = Tok.FillCardSecondary,
            BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            Children =
            [
                Head(),
                ZStack(body, Embed.Comp(() => new SelectionCommandBar(_sel,
                    // Live _rows/RowCap read (NOT a captured `n`): this factory freezes at mount — while the panel is
                    // still loading, a frozen row count would index past the empty track list.
                    i => (uint)i < (uint)Math.Min(_rows.Count, DiscoGrid.RowCap) ? _rows[i] : null,
                    bottomPadding: Spacing.S))),
            ],
        };
    }

    Element Head() => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, Height = 40f,
        Children =
        [
            new BoxEl { Width = 30f, Height = 30f, Shrink = 0f, Corners = CornerRadius4.All(15f), Fill = _accent(),
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, OnClick = () => _play(_thin.Uri),
                // Artwork-derived fill ⇒ ink from the FILL's luminance (the WaveeCta.Palette rule), never the theme's
                // on-accent token: the lifted cover accent is often pale, where TextOnAccentPrimary's glyph vanished.
                Children = [ Icon(Icons.Play, 12f, ColorContrast.PickContrast(_accent())) ] },
            new BoxEl { Grow = 1f, Basis = 0f, OnClick = () => _go("album:" + _thin.Uri, _thin.Name),
                Children = [ new TextEl(_thin.Name) { Size = 14f, Weight = 700, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis } ] },
            AlbumNavAction.Create(() => _go("album:" + _thin.Uri, _thin.Name), 32f),
        ],
    };

    Element Rows(IReadOnlyList<Track> tracks, int n, PlaybackBridge? bridge, LibraryBridge? lib, ActionServices? acts, IOverlayService? menuOverlay)
        => ItemsView.CreateBound(
            n,
            scope =>
            {
                // One plain wrapper carries BOTH the drag source and the context menu (AccentPill's own root has bound
                // visuals, so neither may go there). Unconditional: the drag does not depend on the action services,
                // and the pre-existing conditional wrapper made the row's shape differ between hosts for no reason.
                BoxEl row = new BoxEl
                {
                    Direction = 1,
                    // A COPY source — an album drawer has no membership rows to move. The index is read at PROMOTION,
                    // never captured: bound slots recycle.
                    Draggable = Drag.Source(WaveeDragKinds.Resource, () => DrawerPayload(tracks, n, scope.Index.Peek())),
                    Children = [SelectorVisualsBound.AccentPill(scope,
                        Embed.Comp(() => new DrawerTrackRow(this, scope, tracks, n, bridge, lib)), _showChecks)],
                };
                // Right-click / long-press / the row's "…" cell: the selection-aware track menu (Explorer semantics —
                // inside a multi-selection acts on all of it).
                if (acts is { } a && menuOverlay is { } ov)
                    row = row.WithContextMenu(ov, () => TrackContextMenu.Build(
                        a, _sel, i => (uint)i < (uint)n ? tracks[i] : null,
                        scope.Index.Peek(), static () => null));
                if (acts is null) return row;
                return RowSwipe.WrapBound(row, () =>
                {
                    int i = scope.Index.Peek();
                    return (uint)i < (uint)n
                        ? new ActionContext(ActionTarget.ForTracks(new[] { tracks[i] }), acts)
                        : null;
                }, _swipeGroup, TrackActions.ToggleLike, TrackActions.AddToQueue, scope.Index);
            },
            RepeatLayout.Stack(TrackRow.CompactListItemExtent),
            new ListOptions
            {
                SelectionMode = ItemsSelectionMode.Extended,
                Selection = _sel,
                IsItemInvokedEnabled = true,
                OnInvoked = i =>
                {
                    if ((uint)i >= (uint)n) return;
                    var t = tracks[i];
                    TrackRow.Invoke(bridge, t, () => _ = _svc.Player.PlayAsync(_thin.Uri, i));
                },
                ItemText = i => (uint)i < (uint)n ? tracks[i].Title : "",
                Grow = 0f,
                Scroll = new ScrollOptions { OnScrollGeometryChanged = (g => _swipeGroup.AnyOpen ? BitConverter.SingleToInt32Bits(g.OffsetY) : 0L, _ => _swipeGroup.Close()) },
            });

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

    sealed class DrawerTrackRow : Component
    {
        readonly AlbumDrawerPanel _o;
        readonly RowScope _scope;
        readonly IReadOnlyList<Track> _tracks;
        readonly int _count;
        readonly PlaybackBridge? _bridge;
        readonly LibraryBridge? _lib;
        public DrawerTrackRow(AlbumDrawerPanel o, RowScope scope, IReadOnlyList<Track> tracks, int count, PlaybackBridge? bridge, LibraryBridge? lib)
        { _o = o; _scope = scope; _tracks = tracks; _count = count; _bridge = bridge; _lib = lib; }

        public override Element Render()
        {
            int i = _scope.Index.Value;
            if ((uint)i >= (uint)_count) return new BoxEl();
            var t = _tracks[i];
            var st = TrackRow.StateOf(_bridge, _lib, t);
            Element title = new TextEl(t.Title)
            {
                Size = 13f,
                Weight = 600,
                Color = st.IsNow ? Tok.AccentTextPrimary : Tok.TextPrimary,
                MaxLines = 1,
                Trim = TextTrim.CharacterEllipsis,
                MinWidth = 0f,
            };
            return TrackRow.Grid(t, i, st, DrawerCols, DrawerColumns, DrawerRowContentH, title, showTrackArtist: false, _o._go,
                onPlay: () => TrackRow.Invoke(_bridge, t, () => _ = _o._svc.Player.PlayAsync(_o._thin.Uri, i)),
                onLike: t.Uri.Length > 0 ? () => _lib?.ToggleSaved(t.Uri, t.Title) : null,
                actionsCell: TrackRow.MoreButton(true));   // "…" raises the row's context request (ClickRequestsContext)
        }
    }

    // READY BUT EMPTY. GetAlbumAsync swallows its fetch failure (StoreLibrarySource.EnsureFetchedAsync's `catch { }`)
    // and returns whatever the store holds, so the resource legitimately settles Ready on a trackless album — offline,
    // a failed envelope, or a cold-restored stub. That used to be a 0-row slot with nothing in it and no way out:
    // VirtualCollection has no invalidation, so the stale snapshot never healed. Retry re-runs the loader keeping the
    // current value visible (Resource.Refresh — stale-while-revalidate). Sized by DiscoGrid.NoteRows in the slot.
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

    static Element ShimmerRows(int n)
    {
        var rows = new Element[n];
        for (int i = 0; i < n; i++) rows[i] = ShimmerRow();
        return new BoxEl { Direction = 1, Children = rows };
    }

    static Element ShimmerRow() => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, Height = TrackRow.CompactListItemExtent, Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f),
        Children =
        [
            new BoxEl { Width = 16f, Height = 11f, Corners = CornerRadius4.All(4f), Fill = Tok.FillSubtleSecondary },
            new BoxEl { Grow = 1f, Basis = 0f, Height = 11f, MaxWidth = 240f, Corners = CornerRadius4.All(4f), Fill = Tok.FillSubtleSecondary },
            new BoxEl { Width = 30f, Height = 11f, Corners = CornerRadius4.All(4f), Fill = Tok.FillSubtleSecondary },
            new BoxEl { Width = 40f },   // the reserved "…" lane (matches DrawerColumns' trailing 40px)
        ],
    };
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
    readonly Signal<int> _expanded = new(-1);
    ActionServices? _acts;            // card context menus (Menus.CardAttach) — resolved in Render, read by Cell
    IOverlayService? _menuOverlay;

    const float MinCol = 180f;
    static readonly float Gap = Spacing.L;          // column gap (the vertical row gap is RowGap, folded into rowExtra)

    // Uniform-card geometry → predictable drawer spacing. GridCard's cover is cardW-16; adding 20px vertical padding,
    // an 8px card gap, and 38px for one title + one metadata line yields an exact cardW+50 card. Keeping this separate
    // from RowGap leaves an actual gutter instead of flex-growing the card through it.
    const float CardChrome = 50f;
    const float RowGap     = 20f;       // vertical gap between card rows  (rowExtra = CardChrome + RowGap)
    const float BottomGap  = 30f;       // drawer → next row (breathing room below it)
    const float DrawerHeaderH = 56f;

    /// <summary>Max rows the inline drawer shows. THE single definition — it used to be a literal 10 repeated in
    /// PanelHeight, the panel's shimmer clamp and row clamp, and the selection-bar index guard, all free to drift.</summary>
    internal const int RowCap = AlbumDrawerRows.RowCap;
    /// <summary>Rows' worth of height the Ready-but-empty note + retry occupies.</summary>
    const int NoteRows = 2;

    /// <summary>Rows the drawer will actually render, and why. ONE derivation, read by BOTH the slot sizing
    /// (PanelHeight/DrawerHeight) and the panel body (via the re-pushed Props) — so reserved height and rendered rows
    /// cannot disagree, which is what clipped rows mid-row and left trackless albums with a 0-row slot.</summary>
    readonly record struct DrawerState(string Uri, int Rows, bool Loading, bool ReadyEmpty);

    Resource<Album?> _full;             // the ONE full-album fetch for the open drawer (re-assigned every render)
    Memo<DrawerState>? _drawer;         // the ONE row/state derivation (see Render)
    Action? _retryFull;                 // stable delegate so the re-pushed Props coalesce (a fresh lambda would defeat record equality)

    // The inline drawer's open/close + switch motion. Opacity+Position animate the enter (drop-in) / exit (lift-out);
    // Size=Reflow eases the height when switching to an album with a different track count (the rows below reflow).
    static readonly LayoutTransition DrawerResize = new(
        TransitionChannels.Size,
        MotionTok.ContentResize.ToDynamics(),
        Size: SizeMode.Reflow);
    static readonly LayoutTransition DrawerPresence = new(
        TransitionChannels.Opacity | TransitionChannels.Position,
        MotionTok.StandardEnter.ToDynamics(),
        Enter: new EnterExit(Dy: -8f, Opacity: 0f, Active: true),
        Exit: new EnterExit(Dy: -8f, Opacity: 0f, Active: true),
        ExitDynamics: MotionTok.StandardExit.ToDynamics());

    public DiscoGrid(VirtualCollection<Album> vc, Services svc, Action<string, string?> go, Action<string> play,
                     int initialIndex = 0, Func<ColorF>? accent = null,
                     Action<LazyGridVisibleRange>? onVisibleRangeChanged = null, float expandedTopInset = 28f,
                     float expandedRevealPeek = DrawerHeaderH + 2f * TrackRow.CompactListItemExtent)
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

        // ONE fetch for the open drawer, keyed by uri (collapsed ⇒ "" ⇒ a completed no-op task, never a request).
        // Lifted out of AlbumDrawerPanel so the row count is known where the drawer's SLOT is sized. GetAlbumAsync is
        // cached, so re-expanding the same album costs nothing.
        _ = _vc.Version.Value;                       // subscribe → the expanded uri resolves once its page lands
        int expandedIdx = _expanded.Value;           // subscribe → the fetch re-keys on expand/collapse/switch
        string expandedUri = (expandedIdx >= 0 ? _vc[expandedIdx]?.Uri : null) ?? "";
        _full = UseResource(ct => expandedUri.Length == 0
                ? System.Threading.Tasks.Task.FromResult<Album?>(null)
                : AlbumLoader.LoadAlbumAsync(_svc, expandedUri, ct),
            (Album?)null, expandedUri);

        // A MEMO, not a field and not a signal. The LazyGrid below is a propless Embed.Comp: its ctor delegates freeze
        // at mount and it re-renders only on its OWN subscriptions, so a field written here would be read stale. The
        // frozen DrawerHeight/DrawerFor delegates read this memo, which subscribes LazyGrid to the resource
        // transitively — correct invalidation with zero writes (a write from Render would be a backwards write).
        // NOTE: UseComputed keeps the FIRST closure for the component's lifetime, so the compute derives everything
        // from signals/fields it reads itself — no per-render local may be captured here.
        _drawer = UseComputed(() =>
        {
            _ = _vc.Version.Value;
            int idx = _expanded.Value;
            var al = idx >= 0 ? _vc[idx] : null;
            if (al is null || al.Uri.Length == 0) return new DrawerState("", 0, false, false);
            var l = _full.Loadable;
            // Same fallback chain as the props push in DrawerFor (keep the two in lockstep): the fetched tracklist,
            // else whatever the thin card already carries (usually null in a discography), else empty.
            IReadOnlyList<Track> tracks = l.Value.Value?.Tracks ?? al.Tracks ?? Array.Empty<Track>();
            bool pending = l.State.Value == (byte)LoadState.Pending;
            // LOADING semantics: a resolved SHORT list must not be treated as loading just because the card's stub
            // count is larger — but a still-Pending fetch that has produced nothing yet stays loading no matter how
            // small the hint is. `tracks.Count == 0 && pending` is exactly that rule.
            bool loading = tracks.Count == 0 && pending;
            bool readyEmpty = !pending && tracks.Count == 0;   // Ready OR Failed with nothing to show → note + retry
            int rows = loading ? AlbumDrawerRows.PendingCount(al.TrackCount) : Math.Min(tracks.Count, RowCap);
            return new DrawerState(al.Uri, rows, loading, readyEmpty);
        });

        return Embed.Comp(() => new LazyGrid(
        // The count is the whole facet. LazyGrid realizes only the viewport window plus overscan.
        count: Count,
        cell: Cell,
        ensureRange: (f, l) => _vc.EnsureRange(f, l - 1),
        minColWidth: MinCol, gap: Gap, rowExtra: CardChrome + RowGap, overscanRows: 4,
        expanded: _expanded,
        drawer: DrawerFor,
        drawerHeight: DrawerHeight,
        initialIndex: _initialIndex,
        onVisibleRangeChanged: _visibleRangeChanged,
        expandedTopInset: _expandedTopInset,
        expandedRevealPeek: _expandedRevealPeek));
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
        string date = ReleaseDateLabel(al);
        string subtitle = al.TrackCount > 0
            ? date.Length > 0
                ? Strings.Artist.ReleaseMeta(date, Strings.Artist.TrackCount(al.TrackCount))
                : Strings.Artist.TrackCount(al.TrackCount)
            : date;
        Element card = MediaCard.GridCard(al.Cover, al.Name, subtitle, al.Uri,
            onClick: () => _expanded.Value = _expanded.Peek() == idx ? -1 : idx,
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
            // pairs with the connector bar at the drawer's top edge.
            if (_expanded.Peek() == idx)
                b = b with { BorderColor = _accent(), BorderWidth = 2f, Fill = Tok.FillCardDefault };
            card = b;
        }
        return card;
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

    // Reserve EXACTLY what the panel will render. `rows` comes from the same DrawerState the panel body reads, so a
    // stale/absent stub TrackCount can no longer under-reserve (0 rows for a trackless card while the panel clamped
    // its shimmer to >=1, which ClipToBounds then cut mid-row).
    float PanelHeight(int idx)
    {
        _ = idx;                                        // LazyGrid's Func<int,float> shape; sizing is per-DRAWER and only one is open
        var d = _drawer?.Value ?? default;
        int rows = d.ReadyEmpty ? NoteRows : d.Rows;
        return DrawerHeaderH + rows * TrackRow.CompactListItemExtent;
    }
    // The preceding grid row already includes RowGap, so the drawer starts beyond the card's shadow halo. Its own slot
    // only needs to reserve the panel plus the breathing room before the following row.
    float DrawerHeight(int idx) => PanelHeight(idx) + BottomGap;

    Element DrawerFor(int idx, GridDrawerInfo info)
    {
        var al = _vc?[idx];
        if (al is null) return new BoxEl();
        var d = _drawer?.Value ?? default;
        _retryFull ??= () => _full.Refresh();
        // Re-pushed PROPS, not ctor args. The panel's rows and loading/empty state all change AFTER mount
        // (Pending → Ready on the same uri), and ctor args freeze — which is exactly how the old frozen `panelH` ctor
        // argument locked the panel to the height computed on the expand frame. The Key still remounts per ALBUM, so
        // per-album SelectionModel / SwipeGroup state stays scoped.
        var panel = Embed.Comp(
            new AlbumDrawerPanel.Props(al, _full.Loadable.Value.Value?.Tracks ?? al.Tracks ?? Array.Empty<Track>(),
                                       d.Rows, d.Loading, d.ReadyEmpty, _retryFull),
            () => new AlbumDrawerPanel(_svc, _play, _go, _accent))
            with { Key = "drawer:" + al.Uri };

        // Connector: a short accent bar at the panel's top edge, spanning exactly the expanded card's column → reads as
        // "this drawer belongs to THAT card" (paired with the card's accent border).
        var connector = new BoxEl
        {
            Direction = 0, HitTestVisible = false,
            Children =
            [
                new BoxEl { Width = MathF.Max(0f, info.Left), Height = 0f },
                new BoxEl { Width = info.CellWidth, Height = 3f, Corners = CornerRadius4.All(1.5f), Fill = _accent() },
            ],
        };

        // The OUTER slot owns the transition because its height participates in parent layout. The stable key keeps ONE
        // node across A→B switches (Position/Size-reflow); a true open/close runs the enter/exit channels.
        var inner = new BoxEl
        {
            ZStack = true, Animate = DrawerPresence,
            Children = [ panel, connector ],
        };
        return new BoxEl
        {
            Key = "disco-drawer", Direction = 1,
            Height = DrawerHeight(idx),
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
                Embed.Comp(new Expander.ExpanderSlots(header, grid, parts),
                    () => new Expander { InitiallyExpanded = true }),
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
