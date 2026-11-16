// ── Home/SpineShelf/SpineShelf.cs — the Spine Shelf's mechanics: geometry, both forms, motion, pointer, dwell, keyboard ────────────────────
//
// Design: C:/WAVEE/scratch/pins/spine-shelf-tech.md. Every theme draws the SAME mechanics through its own skin (SpineShelfSkin: the spine's
// face, the open card's head, a tile's caption, the washes, the meter and progress inks) at its own tier numbers (SpineShelfGeometry, from
// the theme's Rules record). This file owns no look.
//
// THE MOTION (why it cannot hiccup). The shelf is a layout boundary of fixed height (Geometry.Extent): nothing in it reaches the page.
// Each item's width is a BOUND prop (open ? FaceW : SpineW). Opening another item is one scoped layout of the n item boxes, then each item
// rides LayoutTransition(Bounds, Tween(OpenMs, Curve), SizeMode.Reveal): its fill and child clip ease on the presented width, its x on the
// FLIP translate, both composited (AnimEngine.Compose writes TransformDirty|PaintDirty only: no measure, no render, no allocation). With one
// curve e(t) for every item, the closing item's presented right edge, x_a + SpineW + (FaceW − SpineW)(1 − e), is exactly where its right
// neighbour's presented x lands, x_a + SpineW + Gap + (FaceW − SpineW)(1 − e) − Gap: the row keeps its length on every frame, no gap, no
// overlap. The open face is its own boundary at a FIXED width (FaceW, the open item's final width): it is laid out once and only revealed
// or clipped while the item moves, so its text never reflows mid-motion. The spine and face layers cross-fade on seeded opacity rows.
// Song tiles are the same mechanism one level down (TileMs): a hover re-lays out the five tile boxes once; each tile's media layer is laid
// out at the widest a tile gets and kept centred by its own Position FLIP on the same curve; its caption is laid out at the rest width.
// The narrow form (Geometry.Standing) stands the items up: rows of RowH, the open one OpenH, heights eased the same way; the total is the
// same whichever is open.

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Controls.Media;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;
using FluentGpu.Signals;
using Wavee;

namespace Wavee.HomeUi;

/// <summary>A theme's numbers for one tier (its Rules record builds it). <see cref="Standing"/>: the narrow form (items as rows).</summary>
/// <param name="Height">The row form's height (the whole shelf).</param>
/// <param name="SpineW">A spine's width in the row form.</param>
/// <param name="RowH">A spine row's height in the standing form.</param>
/// <param name="OpenH">The open card's height in the standing form.</param>
/// <param name="HeadH">The open card's head row height (its cover's edge, or the head's own height standing).</param>
/// <param name="TileCover">The cover's edge on a tile whose song has no canvas.</param>
/// <param name="Tier">The theme's own tier record, for its skin (value-equal, so an unchanged tier re-renders nothing).</param>
public readonly record struct SpineShelfGeometry(
    bool Standing, float Height, float SpineW, float RowH, float OpenH, float Gap, float Radius,
    float FacePad, float HeadH, float HeadGap, float TileGap, float TileRadius, float TileCover, float MeterW, float ProgressH,
    object? Tier = null)
{
    /// <summary>The shelf's height for <paramref name="n"/> items: the row form's own, or the rows and the open card standing.</summary>
    public float Extent(int n) => n <= 0 ? 0f : Standing ? n * RowH + (n - 1) * Gap + (OpenH - RowH) : Height;

    /// <summary>The open card's height.</summary>
    public float FaceH => Standing ? OpenH : Height;

    /// <summary>The song tiles' height.</summary>
    public float TilesH => MathF.Max(0f, FaceH - 2f * FacePad - HeadH - HeadGap);

    /// <summary>The open card's width in a shelf <paramref name="width"/> wide holding <paramref name="n"/> items.</summary>
    public float FaceW(float width, int n) => Standing ? width : MathF.Max(0f, width - (n - 1) * (SpineW + Gap));
}

/// <summary>A theme's look for the shelf. Every builder is static (a pure function of its arguments and the tokens).</summary>
/// <param name="Spine">An item's closed face: its whole box (SpineW × Height, or the row standing).</param>
/// <param name="Head">The open card's head (cover, reason, title, meta, Play, Save), HeadH tall at the face's inner width.</param>
/// <param name="Caption">A song tile's words over the media (number, title, artist), laid out at the tile's rest width.</param>
/// <param name="SpineWash">An item's closed fill (<paramref name="SpineHoverWash"/> under the pointer); <paramref name="FaceWash"/> its open fill.</param>
public sealed record SpineShelfSkin(
    Func<ShelfItem, int, SpineShelfGeometry, Element> Spine,
    Func<ShelfItem, SpineShelfGeometry, Element> Head,
    Func<ShelfSong, int, SpineShelfGeometry, Element> Caption,
    Func<ShelfItem, GradientSpec> SpineWash,
    Func<ShelfItem, GradientSpec> SpineHoverWash,
    Func<ShelfItem, GradientSpec> FaceWash,
    Func<ColorF> Meter,
    Func<ColorF> Progress,
    Func<ColorF> Placeholder);

/// <summary>One shelf page: its zone key (the identity the open item is remembered under), its items, the tier's numbers and the theme's look.</summary>
public sealed record SpineShelfProps(string Key, ShelfItem[] Items, SpineShelfGeometry Geo, SpineShelfSkin Skin)
{
    /// <summary>Value equality over the items (see <see cref="ShelfItem.Equals(ShelfItem?)"/>): an unchanged shelf re-pushed by a board
    /// re-projection re-renders nothing.</summary>
    public bool Equals(SpineShelfProps? o) => o is not null && Key == o.Key && Geo.Equals(o.Geo) && ReferenceEquals(Skin, o.Skin)
        && Items.AsSpan().SequenceEqual(o.Items);

    public override int GetHashCode() => HashCode.Combine(Key, Geo, Items.Length);
}

/// <summary>The shelf body a theme mounts for a SpineShelf zone: one shelf, or (more than <see cref="SpineShelfRules.MaxItems"/> items) a
/// PagedShelf of shelves driven by the chapter's controller (the header's pager).</summary>
public static class SpineShelfView
{
    public static Element Of(Zone zone, SpineShelfGeometry g, SpineShelfSkin skin, ShelfController? controller, string prevGlyph, string nextGlyph)
    {
        var items = ShelfModel.Of(zone);
        if (items.Length == 0) return new BoxEl();
        int pages = SpineShelfRules.Pages(items.Length);
        if (pages <= 1)
            return Embed.Comp(new SpineShelfProps(zone.Key, items, g, skin), static () => new SpineShelf())
                with { Key = "shelf-page:" + zone.Key, SkeletonProxy = () => Skeleton(items.Length, g) };
        var parts = new SpineShelfProps[pages];
        for (int p = 0; p < pages; p++)
        {
            int start = SpineShelfRules.PageStart(items.Length, pages, p), size = SpineShelfRules.PageSize(items.Length, pages, p);
            parts[p] = new SpineShelfProps(zone.Key + "#" + p.ToString(System.Globalization.CultureInfo.InvariantCulture), items[start..(start + size)], g, skin);
        }
        float height = g.Extent(SpineShelfRules.PageSize(items.Length, pages, 0));
        return PagedShelf.Create<SpineShelfProps>(parts,
            static (part, _, _) => Embed.Comp(part, static () => new SpineShelf()) with { SkeletonProxy = () => Skeleton(part.Items.Length, part.Geo) },
            cardHeight: _ => height, measured: true, pager: ShelfPager.None, controller: controller,
            minCardW: 1f, maxCardW: 1_000_000f, maxColumns: 1, gap: g.Gap, edgeFade: 0f, snap: ShelfSnap.Page,
            prevGlyph: prevGlyph, nextGlyph: nextGlyph, lift: ShelfLift.None,
            keyOf: static (part, _) => "shelf-page:" + part.Key)
            with { Key = "shelf-pages:" + zone.Key };
    }

    /// <summary>The cold-load shape: the shelf's boxes at their own geometry (the open card, the spines or rows), flat, so the loaded shelf lands
    /// in the box its placeholder held.</summary>
    public static Element Skeleton(int n, SpineShelfGeometry g)
    {
        var kids = new Element[Math.Max(1, n)];
        for (int i = 0; i < kids.Length; i++)
            kids[i] = g.Standing
                ? new BoxEl { Height = i == 0 ? g.OpenH : g.RowH, Shrink = 0f, MinWidth = 0f, Corners = CornerRadius4.All(g.Radius) }
                : new BoxEl { Width = i == 0 ? float.NaN : g.SpineW, Grow = i == 0 ? 1f : 0f, Basis = 0f, Shrink = 0f, MinWidth = 0f, Corners = CornerRadius4.All(g.Radius) };
        return new BoxEl
        {
            Direction = (byte)(g.Standing ? 1 : 0), Gap = g.Gap, Height = g.Extent(n), MinWidth = 0f, AlignItems = FlexAlign.Stretch, Children = kids,
        };
    }

    /// <summary>The body's height for <paramref name="zone"/> (the chapter's exact extent, under its header).</summary>
    public static float Extent(Zone zone, SpineShelfGeometry g)
    {
        int n = 0;
        if (zone.Clusters is { } cs) foreach (var c in cs) if (c.Header is not null) n++;
        int pages = SpineShelfRules.Pages(n);
        return pages == 0 ? 0f : g.Extent(SpineShelfRules.PageSize(n, pages, 0));
    }

    /// <summary>The key prefix of every spine (a theme's lint guide names it: the turned words are laid out unturned, see <see cref="Sideways"/>).</summary>
    public const string SpineKey = "shelf:spine:";

    /// <summary>Why a spine's turned words are no clip (the lint guides' text.clip intent).</summary>
    public const string SpineLintReason =
        "a spine's title and artist are turned a quarter (SpineShelfView.Sideways): layout stays unturned, so the rule measures the line across the "
        + "spine while the render draws it along the spine (scratch/sphome/spine-shelf.html)";

    /// <summary>A line of words turned to read bottom to top (a spine's title): laid out <paramref name="length"/> long and
    /// <paramref name="thickness"/> tall, then turned a quarter about its centre inside a box <paramref name="thickness"/> wide and
    /// <paramref name="length"/> tall. Layout stays unrotated (the engine has no vertical text), so both boxes are explicit.</summary>
    public static Element Sideways(Element line, float length, float thickness, FlexAlign align = FlexAlign.Start) => new BoxEl
    {
        Width = thickness, Height = length, Shrink = 0f, MinWidth = 0f, ZStack = true, HitTestVisible = false,
        Children =
        [
            new BoxEl
            {
                Width = length, Height = thickness, Shrink = 0f, MinWidth = 0f, Direction = 0, AlignItems = FlexAlign.Center,
                Justify = align == FlexAlign.End ? FlexJustify.End : FlexJustify.Start,
                // A stack places an oversized layer at its start: the offset centres it on the box before the quarter turn about its centre.
                AlignSelf = FlexAlign.Center, OffsetX = (thickness - length) / 2f,
                Rotation = -90f, TransformOriginX = 0.5f, TransformOriginY = 0.5f,
                Children = [line],
            },
        ],
    };
}

/// <summary>The mounted shelves (the inspector's `home do=shelf`). A page can be mounted twice for a moment (a paged shelf's measuring copy
/// beside its cell), so this is a list of instances, each removed by its own unmount.</summary>
public static class ShelfRegistry
{
    public static readonly List<SpineShelf> Live = new();

    /// <summary>The open item remembered per shelf (zone key, page), for the session.</summary>
    internal static readonly Dictionary<string, string> Open = new(StringComparer.Ordinal);
}

/// <summary>One shelf page. Mounted through <see cref="SpineShelfView.Of"/>.</summary>
public sealed class SpineShelf : Component
{
    // ── state (survives re-renders; the shelf re-renders only when its props change) ──
    Signal<int> _open = null!;   // created at the first render with the remembered item (a render never writes a signal)
    readonly Signal<int> _live = new(-1);   // item * Songs + song, -1 none
    float _seed;   // the slot's known width on the first render (a pack grid tile), until the shelf's own measure lands
    int _dwell = -1, _focus, _settling = -1, _settled = -1;
    string _key = "";
    ShelfItem[] _items = [];
    SpineShelfGeometry _g;
    AnimEngine? _anim;
    InputHooks? _hooks;
    TimerHandle _dwellTimer, _tileTimer;
    bool _armed;
    NodeHandle[] _item = [], _spine = [], _face = [], _meter = [], _progress = [];
    Element[] _built = [];
    ShelfItem[] _builtItems = [];
    SpineShelfGeometry _builtG;
    SpineShelfSkin? _builtSkin;

    /// <summary>An unset extent (the parent's stretch decides).</summary>
    static readonly Prop<float> Free = float.NaN;

    static readonly SpringParams s_fadeIn = SpringParams.FromResponse(SpineShelfRules.FadeMs / 1000f, 1f);
    const int S = SpineShelfRules.Songs;

    // ── reads for the inspector ──
    public int Items => _items.Length;
    public int OpenIndex => _open.Peek();
    public int LiveTile => _live.Peek() < 0 ? -1 : _live.Peek() % S;
    public int Dwelling => _dwell;
    public bool Standing => _g.Standing;
    public string Key => _key;
    /// <summary>Whether this instance's items are in the scene now (a measuring copy's are not, once it is gone).</summary>
    public bool Shown => _item.Length > 0 && !_item[0].IsNull && Context.Scene is { } sc && sc.IsLive(_item[0]);

    public ShelfItem? OpenItem => (uint)_open.Peek() < (uint)_items.Length ? _items[_open.Peek()] : null;

    public override Element Render()
    {
        var p = UseProps<SpineShelfProps>();
        var g = p.Geo;
        var skin = p.Skin;
        var items = p.Items;
        int n = items.Length;
        bool first = _key.Length == 0;
        _key = p.Key;
        _items = items;
        _g = g;
        _anim = Context.Anim;
        _hooks = UseContext(InputHooks.Current);
        ShelfPreview.Warm(UseContext(VideoCompositor.Current) is not null);
        if (_item.Length != n)
        {
            _item = new NodeHandle[n]; _spine = new NodeHandle[n]; _face = new NodeHandle[n]; _meter = new NodeHandle[n];
            _progress = new NodeHandle[n * S];
        }

        // The open item: remembered per shelf identity, else the first in Spotify's order.
        if (first)
        {
            int open = 0;
            if (ShelfRegistry.Open.TryGetValue(p.Key, out var remembered))
                for (int i = 0; i < n; i++) if (items[i].Key == remembered) { open = i; break; }
            _open = new Signal<int>(open);
            _focus = open;
        }
        int openNow = Math.Min(_open.Peek(), Math.Max(0, n - 1));

        // The shelf's width: remembered per key, else the slot's known width (a pack grid tile), so the first render lays the face out at its width.
        var measured = UseMeasuredWidth("spine-shelf:" + p.Key, 1f);
        if (first && measured.Peek() <= 1f
            && Context.ResolveContextSignal?.Invoke(Context.AnchorNode, MeasureMemory.KnownWidth)?.Peek() is float known && known > 1f)
            _seed = known;
        var widthSig = measured;
        float W() { float w = widthSig.Value; return w > 1f ? w : _seed; }
        float FaceW() => g.FaceW(W(), n);
        float TileSpan() => MathF.Max(0f, FaceW() - 2f * g.FacePad - (S - 1) * g.TileGap);
        float RestW() => TileSpan() / S;
        float LiveW() => TileSpan() * SpineShelfRules.LiveShare / (S - 1 + SpineShelfRules.LiveShare);
        float OtherW() => TileSpan() / (S - 1 + SpineShelfRules.LiveShare);

        _dwellTimer = UseTimeout(DwellFired, SpineShelfRules.DwellMs, DepKey.Empty);
        _tileTimer = UseTimeout(TileSettled, SpineShelfRules.TileMs, DepKey.Empty);
        if (!_armed) { _armed = true; _dwellTimer.Cancel(); _tileTimer.Cancel(); }

        string key = p.Key;
        UseEffect(() =>
        {
            ShelfRegistry.Live.Add(this);
            if (_open.Peek() < _items.Length) ShelfPreview.Prepare(_items[_open.Peek()]);
            return () =>
            {
                ShelfRegistry.Live.Remove(this);
                if (ShelfPreview.Live.Peek().StartsWith(key + "|", StringComparison.Ordinal)) ShelfPreview.Leave();
            };
        }, DepKey.Empty);

        // An item that has not changed keeps its element (the reconciler skips an identical element): a re-render for one item's change
        // (its preview songs landing) rebuilds that item alone.
        var row = new Element[n];
        bool sameFrame = _builtG.Equals(g) && ReferenceEquals(_builtSkin, skin) && _built.Length == n;
        for (int i = 0; i < n; i++)
            row[i] = sameFrame && _builtItems[i].Equals(items[i]) ? _built[i] : Item(items[i], i, i == openNow, g, skin, FaceW, RestW, LiveW, OtherW);
        _built = row; _builtItems = items; _builtG = g; _builtSkin = skin;

        // A canvas file that lands while its tile waits re-presents it (CanvasVideo.Files.Epoch).
        var watch = Embed.Comp(new EpochProps(this), static () => new EpochWatch());

        return new BoxEl
        {
            Key = "shelf:" + key, Direction = (byte)(g.Standing ? 1 : 0), Gap = g.Gap, MinWidth = 0f, AlignItems = FlexAlign.Stretch,
            Height = g.Extent(n), Shrink = 0f, IsolateLayout = true,
            Children = [.. row, watch],
        };
    }

    // ── one item: the spine, the face, the meter ────────────────────────────────────────────────────────────────────────

    static readonly LayoutTransition s_rowMotion = new(TransitionChannels.Bounds,
        TransitionDynamics.Tween(SpineShelfRules.OpenMs, SpineShelfRules.Curve), SizeMode.Reveal, Axes: SizeAxes.Width);
    static readonly LayoutTransition s_standMotion = new(TransitionChannels.Bounds,
        TransitionDynamics.Tween(SpineShelfRules.OpenMs, SpineShelfRules.Curve), SizeMode.Reveal, Axes: SizeAxes.Height);
    static readonly LayoutTransition s_tileMotion = new(TransitionChannels.Bounds,
        TransitionDynamics.Tween(SpineShelfRules.TileMs, SpineShelfRules.Curve), SizeMode.Reveal, Axes: SizeAxes.Width);
    static readonly LayoutTransition s_tileSlide = new(TransitionChannels.Position,
        TransitionDynamics.Tween(SpineShelfRules.TileMs, SpineShelfRules.Curve));

    Element Item(ShelfItem item, int i, bool open, SpineShelfGeometry g, SpineShelfSkin skin,
                 Func<float> faceW, Func<float> restW, Func<float> liveW, Func<float> otherW)
    {
        var openSig = _open;
        int index = i;
        string ik = _key + "|" + item.Key;

        var spine = new BoxEl
        {
            Key = SpineShelfView.SpineKey + item.Key, Direction = 1, MinWidth = 0f, Shrink = 0f,
            Width = g.Standing ? float.NaN : g.SpineW, Height = g.Standing ? g.RowH : g.Height,
            Gradient = skin.SpineWash(item), HoverGradient = skin.SpineHoverWash(item), Opacity = open ? 0f : 1f,
            HitTestVisible = Prop.Of(() => openSig.Value != index), Cursor = CursorId.Hand,
            OnClick = () => OpenAt(index), OnRealized = h => { if (index < _spine.Length) _spine[index] = h; },
            Children = [skin.Spine(item, i, g)],
        };

        Element[] tiles;
        if (item.NoSongs) tiles = [CoverTile(item, g)];
        else
        {
            tiles = new Element[S];
            for (int j = 0; j < S; j++) tiles[j] = Tile(item, i, j, g, skin, restW, liveW, otherW);
        }
        var face = new BoxEl
        {
            Key = "shelf:face:" + item.Key, Direction = 1, MinWidth = 0f, Shrink = 0f,
            Width = g.Standing ? Free : Prop.Of(faceW), Height = g.FaceH, IsolateLayout = true,
            JustifySelf = FlexAlign.Start, AlignSelf = FlexAlign.Start,
            Padding = Edges4.All(g.FacePad), Gap = g.HeadGap, Gradient = skin.FaceWash(item), Opacity = open ? 1f : 0f,
            HitTestVisible = Prop.Of(() => openSig.Value == index),
            OnRealized = h => { if (index < _face.Length) _face[index] = h; },
            Children =
            [
                new BoxEl { Direction = 0, Height = g.HeadH, MinWidth = 0f, Shrink = 0f, AlignItems = FlexAlign.Stretch, Children = [skin.Head(item, g)] },
                new BoxEl
                {
                    Key = "shelf:tiles:" + item.Key, Direction = 0, Gap = g.TileGap, Height = g.TilesH, MinWidth = 0f, Shrink = 0f,
                    IsolateLayout = true, AlignItems = FlexAlign.Stretch, Children = tiles,
                },
            ],
        };

        var meter = new BoxEl
        {
            Key = "shelf:meter:" + item.Key, Fill = skin.Meter(), HitTestVisible = false,
            Width = g.Standing ? float.NaN : g.MeterW, Height = g.Standing ? g.MeterW : float.NaN,
            JustifySelf = FlexAlign.Start, AlignSelf = g.Standing ? FlexAlign.End : FlexAlign.Stretch,
            ScaleX = g.Standing ? 0f : 1f, ScaleY = g.Standing ? 1f : 0f, TransformOriginX = 0f, TransformOriginY = 1f,
            OnRealized = h => { if (index < _meter.Length) _meter[index] = h; },
        };

        return new BoxEl
        {
            Key = "shelf:item:" + item.Key, ZStack = true, ClipToBounds = true, Corners = CornerRadius4.All(g.Radius), MinWidth = 0f,
            Shrink = 0f, Grow = 0f,
            Width = g.Standing ? Free : Prop.Of(() => openSig.Value == index ? faceW() : g.SpineW),
            Height = g.Standing ? Prop.Of(() => openSig.Value == index ? g.OpenH : g.RowH) : (Prop<float>)g.Height,
            Layout = g.Standing ? s_standMotion : s_rowMotion,
            Focusable = true, Role = AutomationRole.Button,
            OnHoverMove = _ => DwellStart(index), OnPointerExit = () => DwellCancel(index),
            OnFocusChanged = f => { if (f) _focus = index; },
            OnKeyDown = OnKey,
            OnRealized = h => { if (index < _item.Length) _item[index] = h; },
            Children = [spine, face, meter],
        };
    }

    // ── one song tile ───────────────────────────────────────────────────────────────────────────────────────────────────

    Element Tile(ShelfItem item, int i, int j, SpineShelfGeometry g, SpineShelfSkin skin, Func<float> restW, Func<float> liveW, Func<float> otherW)
    {
        var song = item.Songs[j];
        var live = _live;
        int code = i * S + j;
        string tile = _key + "|" + item.Key + "#" + j.ToString(System.Globalization.CultureInfo.InvariantCulture);
        float TileW()
        {
            int l = live.Value;
            if (l < 0 || l / S != code / S) return restW();
            return l == code ? liveW() : otherW();
        }
        if (song.IsBlank)
            return new BoxEl
            {
                Key = "shelf:tile:" + item.Key + ":" + j.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Width = Prop.Of(restW), Shrink = 0f, MinWidth = 0f, Corners = CornerRadius4.All(g.TileRadius), Fill = skin.Placeholder(),
            };

        var media = new List<Element>(4)
        {
            new BoxEl { Gradient = TileWash(song.Accent) },
        };
        if (song.HasCanvas)
            media.Add(new ImageEl { Source = song.Still ?? song.Cover ?? "", Fit = ImageFit.Cover });
        else if (song.Cover is { } cover)
            media.Add(new BoxEl
            {
                AlignSelf = FlexAlign.Center, JustifySelf = FlexAlign.Center, Width = g.TileCover, Height = g.TileCover,
                Children = [Controls.Artwork(cover, g.TileCover, g.TileCover, g.TileRadius)],
            });
        if (song.HasCanvas) media.Add(Embed.Comp(new TileVideoProps(tile, g.TileRadius), static () => new TileVideo()));

        // The media is laid out at the widest a tile gets (so a widening tile reveals it and never re-crops it) and centred on the tile: an
        // unbounded stack layer may overflow its stack on both sides (FlexLayout.ArrangeZStack), and its own Position FLIP rides the tile's
        // curve, so its centre stays on the tile's presented centre every frame.
        var mediaLayer = new BoxEl
        {
            MeasureUnboundedWidth = true, JustifySelf = FlexAlign.Center, Layout = s_tileSlide, HitTestVisible = false, MinWidth = 0f,
            Children =
            [
                new BoxEl
                {
                    ZStack = true, Height = g.TilesH, Width = Prop.Of(liveW), Shrink = 0f, MinWidth = 0f, HitTestVisible = false,
                    Children = media.ToArray(),
                },
            ],
        };
        var scrim = new BoxEl { HitTestVisible = false, Gradient = s_scrim };
        // The words follow the tile's width: they re-trim once, on the commit frame, never while the tile moves.
        var caption = new BoxEl
        {
            Direction = 1, MinWidth = 0f, HitTestVisible = false,
            Children = [skin.Caption(song, j, g)],
        };
        var progress = new BoxEl
        {
            Height = g.ProgressH, AlignSelf = FlexAlign.End, Fill = skin.Progress(), HitTestVisible = false,
            ScaleX = 0f, TransformOriginX = 0f,
            OnRealized = h => { if (code < _progress.Length) _progress[code] = h; },
        };
        var s = song;
        return new BoxEl
        {
            Key = "shelf:tile:" + item.Key + ":" + j.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ZStack = true, ClipToBounds = true, Corners = CornerRadius4.All(g.TileRadius), Shrink = 0f, MinWidth = 0f,
            Width = Prop.Of(TileW), Layout = s_tileMotion,
            Focusable = true, Cursor = CursorId.Hand, Role = AutomationRole.Button,
            OnClick = () => PlayFrom(i, s),
            OnHoverMove = _ => TileEnter(code, tile, in s),
            OnPointerExit = () => TileLeave(code),
            OnFocusChanged = f => { if (f) TileEnter(code, tile, in s); else TileLeave(code); },
            Children = [mediaLayer, scrim, caption, progress],
        };
    }

    /// <summary>The card the lookup named no song for: one tile across the row, the playlist's cover on its own colour; a click plays it.</summary>
    static Element CoverTile(ShelfItem item, SpineShelfGeometry g)
    {
        var h = item.Header;
        float edge = MathF.Max(0f, g.TilesH - 2f * g.FacePad);
        return new BoxEl
        {
            Key = "shelf:tile:" + item.Key + ":cover", ZStack = true, ClipToBounds = true, Corners = CornerRadius4.All(g.TileRadius), Grow = 1f,
            MinWidth = 0f, Gradient = TileWash(item.Accent), Cursor = CursorId.Hand, Role = AutomationRole.Button, Focusable = true,
            OnClick = () => HomeCardNav.Play(in h),
            Children =
            [
                new BoxEl
                {
                    AlignSelf = FlexAlign.Center, JustifySelf = FlexAlign.Center, Width = edge, Height = edge, HitTestVisible = false,
                    Children = [Controls.Artwork(h.ImageUrl, edge, edge, g.TileRadius)],
                },
            ],
        };
    }

    /// <summary>A song tile's ground: the cover's own colour, darker at the foot (the media sits on it; a song without a canvas shows its
    /// cover on it).</summary>
    static GradientSpec TileWash(uint accent)
    {
        var c = accent != 0u ? ColorScience.ToColor(accent) : new ColorF(0.16f, 0.16f, 0.17f, 1f);
        return new GradientSpec(GradientShape.Linear, 110f, [new GradientStop(0f, ColorF.Lerp(c, new ColorF(1f, 1f, 1f, 1f), 0.18f)), new GradientStop(1f, ColorF.Lerp(c, new ColorF(0f, 0f, 0f, 1f), 0.45f))]);
    }

    /// <summary>The words' scrim: dark at the tile's foot, clear above its middle (the on-media legibility floor).</summary>
    static readonly GradientSpec s_scrim = new(GradientShape.Linear, 90f,
        [new GradientStop(0.4f, new ColorF(0f, 0f, 0f, 0f)), new GradientStop(0.62f, new ColorF(0f, 0f, 0f, 0.25f)), new GradientStop(1f, new ColorF(0f, 0f, 0f, 0.78f))]);

    // ── acts (the pointer, the keyboard and the inspector all come here) ────────────────────────────────────────────────

    /// <summary>Open item <paramref name="i"/>: the widths change in one scoped layout, the motion rides the layout transition, the layers
    /// cross-fade.</summary>
    public void OpenAt(int i)
    {
        int old = _open.Peek();
        if ((uint)i >= (uint)_items.Length || i == old) return;
        DwellCancel(_dwell);
        TileLeave(_live.Peek());
        if (_anim is { } a)
        {
            Fade(a, _spine, old, 1f, 0f);
            Fade(a, _face, old, 0f, 0f);
            Fade(a, _spine, i, 0f, 0f);
            if ((uint)i < (uint)_face.Length && !_face[i].IsNull)
                a.Spring(_face[i], AnimChannel.Opacity, 1f, s_fadeIn, delayMs: SpineShelfRules.FaceDelayMs);
        }
        _open.Value = i;
        _focus = i;
        ShelfRegistry.Open[_key] = _items[i].Key;
        ShelfPreview.Prepare(_items[i]);
    }

    static void Fade(AnimEngine a, NodeHandle[] nodes, int i, float to, float delay)
    {
        if ((uint)i >= (uint)nodes.Length || nodes[i].IsNull) return;
        a.Spring(nodes[i], AnimChannel.Opacity, to, s_fadeIn, delayMs: delay);
    }

    /// <summary>The pointer rests on spine <paramref name="i"/>: the meter fills over DwellMs, then it opens (leaving cancels).</summary>
    public void DwellStart(int i)
    {
        if (i == _open.Peek() || i == _dwell || (uint)i >= (uint)_items.Length) return;
        if (_dwell >= 0) DwellCancel(_dwell);
        _dwell = i;
        if (_anim is { } a && !_meter[i].IsNull)
            a.SeedEased(_meter[i], _g.Standing ? AnimChannel.ScaleX : AnimChannel.ScaleY, 0f, 1f, SpineShelfRules.DwellMs, Easing.Linear);
        _dwellTimer.RestartIn(SpineShelfRules.DwellMs);
    }

    /// <summary>The pointer left spine <paramref name="i"/> before it opened.</summary>
    public void DwellCancel(int i)
    {
        if (i < 0 || i != _dwell) return;
        _dwell = -1;
        _dwellTimer.Cancel();
        if (_anim is { } a && (uint)i < (uint)_meter.Length && !_meter[i].IsNull)
            a.SeedEased(_meter[i], _g.Standing ? AnimChannel.ScaleX : AnimChannel.ScaleY, 1f, 0f, SpineShelfRules.FadeMs / 2f, Easing.EaseOut);
    }

    void DwellFired()
    {
        int i = _dwell;
        if (i < 0) return;
        DwellCancel(i);
        OpenAt(i);
    }

    /// <summary>The pointer (or focus) came onto a tile of the open card: it widens, its clip starts, its canvas follows once it has widened.</summary>
    public void TileEnter(int code, string tile, in ShelfSong song)
    {
        if (code / S != _open.Peek() || code == _live.Peek() || song.IsBlank) return;
        TileLeave(_live.Peek());
        _live.Value = code;
        _settling = code;
        ShelfPreview.Enter(tile, in song);
        _tileTimer.RestartIn(SpineShelfRules.TileMs);
    }

    /// <summary>The pointer (or focus) left tile <paramref name="code"/>: the video and the clip stop first, then the tile narrows.</summary>
    public void TileLeave(int code)
    {
        if (code < 0 || code != _live.Peek()) return;
        _tileTimer.Cancel();
        _settling = -1;
        _settled = -1;
        ShelfPreview.Leave();
        if (_anim is { } a && (uint)code < (uint)_progress.Length && !_progress[code].IsNull)
            a.SeedEased(_progress[code], AnimChannel.ScaleX, 0f, 0f, 1f, Easing.Linear);
        _live.Value = -1;
    }

    void TileSettled()
    {
        int code = _settling;
        if (code < 0 || code != _live.Peek()) return;
        _settled = code;
        // The clip and its progress line start once the tile has widened (ShelfPreview.Settled opens it then).
        var song = _items[code / S].Songs[code % S];
        if (_anim is { } a && (uint)code < (uint)_progress.Length && !_progress[code].IsNull && ShelfPreview.ClipAllowed(in song))
            a.SeedEased(_progress[code], AnimChannel.ScaleX, 0f, 1f, SpineShelfRules.ClipMs, Easing.Linear);
        ShelfPreview.Settled(TileKey(code));
    }

    /// <summary>A canvas file landed: a settled live tile that waited for it presents it now.</summary>
    internal void FileLanded()
    {
        int code = _live.Peek();
        if (code >= 0 && _settled == code) ShelfPreview.Settled(TileKey(code));
    }

    string TileKey(int code) => _key + "|" + _items[code / S].Key + "#" + (code % S).ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The inspector's tile hover: tile <paramref name="j"/> of the open card (-1 leaves).</summary>
    public void HoverTile(int j)
    {
        int open = _open.Peek();
        if (j < 0) { TileLeave(_live.Peek()); return; }
        if ((uint)open >= (uint)_items.Length || (uint)j >= S) return;
        var song = _items[open].Songs[j];
        TileEnter(open * S + j, TileKey(open * S + j), in song);
    }

    /// <summary>Play the item's playlist from <paramref name="song"/>.</summary>
    void PlayFrom(int i, in ShelfSong song)
    {
        if ((uint)i >= (uint)_items.Length || song.IsBlank) return;
        if (i != _open.Peek()) { OpenAt(i); return; }
        Playback.PlayContext(_items[i].Header.Id, song.Card.Id);
    }

    /// <summary>Arrows walk the spines (focus), Home and End jump, Enter and Space open the focused one.</summary>
    public void OnKey(KeyEventArgs a)
    {
        if (a.Handled || _items.Length == 0) return;
        int n = _items.Length, f = _focus;
        int to = a.KeyCode switch
        {
            Keys.Right or Keys.Down => Math.Min(n - 1, f + 1),
            Keys.Left or Keys.Up => Math.Max(0, f - 1),
            Keys.Home => 0,
            Keys.End => n - 1,
            _ => -1,
        };
        if (a.KeyCode is Keys.Enter or Keys.Space) { a.Handled = true; OpenAt(f); return; }
        if (to < 0) return;
        a.Handled = true;
        _focus = to;
        if ((uint)to < (uint)_item.Length && !_item[to].IsNull) (_hooks?.MoveFocusVisual ?? _hooks?.RestoreFocus)?.Invoke(_item[to]);
    }

    // ── the canvas file watcher and the tile's video ────────────────────────────────────────────────────────────────────

    sealed record EpochProps(SpineShelf Shelf);

    /// <summary>Re-renders on CanvasVideo.Files.Epoch (a file landed) and tells the shelf from an effect (never from the render).</summary>
    sealed class EpochWatch : Component
    {
        public override Element Render()
        {
            var shelf = UseProps<EpochProps>().Shelf;
            int epoch = CanvasVideo.Files.Epoch.Value;
            UseEffect(() => shelf.FileLanded(), DepKey.From(epoch));
            return new BoxEl { Width = 0f, Height = 0f, HitTestVisible = false };
        }
    }

    sealed record TileVideoProps(string Tile, float Radius);

    /// <summary>The live tile's canvas video: the one player's surface, mounted only while this tile is the one showing (ShelfPreview.Showing).</summary>
    sealed class TileVideo : Component
    {
        public override Element Render()
        {
            var p = UseProps<TileVideoProps>();
            bool showing = ShelfPreview.Showing.Value == p.Tile;
            if (!showing || ShelfPreview.Video is not { } player) return new BoxEl { HitTestVisible = false };
            return new BoxEl
            {
                Direction = 1, HitTestVisible = false,
                Children = [Embed.Comp(() => new MediaPlayerElement
                {
                    Player = player, IsDecorative = true, AreTransportControlsEnabled = false, ShowStatusOverlay = false,
                    ShowLetterboxBars = false, Stretch = MediaStretch.UniformToFill, CornerRadius = p.Radius,
                })],
            };
        }
    }
}
