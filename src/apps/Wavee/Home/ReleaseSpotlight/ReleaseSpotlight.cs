// ── Home/ReleaseSpotlight/ReleaseSpotlight.cs — the Release Spotlight's mechanics: pages, the card's parts, the hover preview ─────────────────
//
// Concept: C:/WAVEE/scratch/sphome/release-spotlight.html (owner 2026-11-17: "perfect, build, but make sure classic looks like wavee classic").
// The concept sets the STRUCTURE and the behaviour; every theme draws the card in its OWN design language through its skin
// (ReleaseSpotlightSkin: the card's frame and ground, the cover, the words and actions, a song row's look, a waiting cover) at its own tier
// numbers (ReleaseSpotlightGeometry, from the theme's Rules record). This file owns no look.
//
// THE CARD. A fixed-height box (Geometry.Height, a layout boundary): the cover, the words (the theme's eyebrow with the artist, the title, the
// meta line, an EP's or an album's first songs, Play, Save and the previews' sound button) and the song's canvas as a tall tile on the right.
// Narrow tiers stack the cover over the words and drop the canvas. Nothing in the card changes size when the lookups land: the meta line and
// the songs' durations change their words in place.
//
// THE PREVIEW (the Spine Shelf's, reused whole). Pointing at the card (a real move: OnHoverMove is hover intent, never content sliding under a
// still pointer) previews its face song; pointing at a listed song previews that one. The claim goes to PreviewAudio (muted unless the shared
// sound button is on, the music ducked, its ownership check every tick: HoldFails below), the canvas to ShelfPreview's one player. The media
// opens SettleMs after the pointer came to rest on a song, so a pointer crossing the card opens nothing. The canvas video mounts UNDER its
// still in the still's box and the still fades out on its first frame; on a leave the still fades back in over the playing video, then the
// video is released (the shelf's crossfade). The canvas tile holds one layer per song: moving to another song cross-fades the layers.
//
// PAGES. More than one release (HomeProjection's fold, the Following facet's run) pages through a PagedShelf of cards driven by the chapter's
// controller (the theme's header pager); the next releases' covers wait under the card's right end, a click turns to that page.

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

/// <summary>A theme's numbers for one tier (its Rules record builds it). <see cref="Narrow"/>: the cover over the words, no canvas.</summary>
/// <param name="Height">The card's exact height.</param>
/// <param name="Pad">The card's inner padding around the cover, the words and the canvas (0: the cover and the canvas run to the card's edges).</param>
/// <param name="Gap">The air between the cover, the words and the canvas.</param>
/// <param name="Cover">The cover's edge (narrow: its width unless <see cref="CoverBleeds"/>).</param>
/// <param name="NarrowCoverH">Narrow: the cover's height.</param>
/// <param name="CoverBleeds">The cover runs edge to edge (Zune): the full height beside the words, the full width over them.</param>
/// <param name="CopyPad">The words' own padding inside their column.</param>
/// <param name="TrackRows">How many of an EP's or an album's songs the card lists.</param>
/// <param name="NextCover">A waiting cover's edge under the card (pages only).</param>
/// <param name="Tier">The theme's own tier record, for its skin (value-equal, so an unchanged tier re-renders nothing).</param>
public readonly record struct ReleaseSpotlightGeometry(
    bool Narrow, float Height, float Pad, float Gap, float Radius, float Cover, float CoverRadius, float NarrowCoverH, bool CoverBleeds,
    Edges4 CopyPad, float CanvasW, float CanvasH, float CanvasRadius, int TrackRows, float TrackRowH, float TrackRadius,
    float NextCover, float NextGap, float StripGap, float PageGap, float ProgressH, object? Tier = null)
{
    /// <summary>The spotlight's height for <paramref name="n"/> releases: the card, and the waiting covers' row under it while it pages.</summary>
    public float Extent(int n) => n <= 0 ? 0f : n == 1 ? Height : Height + StripGap + NextCover;
}

/// <summary>A theme's look for the spotlight. Every builder is static (a pure function of its arguments and the tokens).</summary>
/// <param name="Frame">The card's own frame (its ground, border and corners) around the card's content, which fills it.</param>
/// <param name="Cover">The release's cover at the geometry's size (the theme's corners and lift).</param>
/// <param name="Copy">The words: the eyebrow with the artist, the title, the meta line, the song list (null: none to list) and the actions.</param>
/// <param name="TrackRow">A listed song's look: its number, title and duration ("" while unknown) at the row's height.</param>
/// <param name="NextCover">A waiting release's cover under the card.</param>
/// <param name="TrackLive">The fill of the song being previewed (and of a row under the pointer).</param>
/// <param name="Progress">The clip's progress line along the card's foot.</param>
/// <param name="FormSwap">The theme's declared swap fade between the wide and the narrow form (different trees, never one re-laid out at once).</param>
/// <param name="Resize">The theme's height reflow: a tier step changes the card's exact height, and the spotlight eases to it through real layout
/// (the bands under it ride it frame by frame; a snapped height re-packed the board under it at once).</param>
public sealed record ReleaseSpotlightSkin(
    Func<SpotlightItem, ReleaseSpotlightGeometry, Element, Element> Frame,
    Func<SpotlightItem, ReleaseSpotlightGeometry, Element> Cover,
    Func<SpotlightItem, SpotlightFacts, ReleaseSpotlightGeometry, Element?, Element> Copy,
    Func<ShelfSong, int, string, ReleaseSpotlightGeometry, Element> TrackRow,
    Func<SpotlightItem, ReleaseSpotlightGeometry, Element> NextCover,
    Func<ColorF> TrackLive,
    Func<ColorF> Progress,
    EnterExit FormSwap,
    MotionTokenDef FormSwapMotion,
    LayoutTransition Resize);

/// <summary>The spotlight body a theme mounts for a ReleaseSpotlight zone.</summary>
public static class ReleaseSpotlightView
{
    /// <summary>The body: one card, or (more than one release) a PagedShelf of cards driven by <paramref name="controller"/> (the chapter
    /// header's pager) over the waiting covers.</summary>
    public static Element Of(Zone zone, ReleaseSpotlightGeometry g, ReleaseSpotlightSkin skin, ShelfController? controller, string prevGlyph, string nextGlyph)
    {
        var items = SpotlightModel.Of(zone);
        if (items.Length == 0) return new BoxEl();
        // The two forms are different trees: each under its own key in a children list, the old one fading out over the new (a declared swap).
        var keyed = new BoxEl
        {
            Key = (g.Narrow ? "spot-narrow:" : "spot-wide:") + zone.Key,
            Direction = 1, MinWidth = 0f, AlignSelf = FlexAlign.Stretch,
            Enter = skin.FormSwap, Exit = skin.FormSwap, Transition = skin.FormSwapMotion,
            Children = [Form(zone.Key, items, g, skin, controller, prevGlyph, nextGlyph)],
        };
        // The body's exact height, eased by the theme's reflow when a tier step changes it.
        return new BoxEl
        {
            Key = "spot-body:" + zone.Key, Direction = 1, MinWidth = 0f, AlignSelf = FlexAlign.Stretch, Height = g.Extent(items.Length), Shrink = 0f,
            Layout = skin.Resize, Children = [keyed],
        };
    }

    static Element Form(string key, SpotlightItem[] items, ReleaseSpotlightGeometry g, ReleaseSpotlightSkin skin, ShelfController? controller,
                        string prevGlyph, string nextGlyph)
    {
        if (items.Length == 1)
            return Card(key, items[0], g, skin);
        var pages = PagedShelf.Create<SpotlightItem>(items,
            (item, _, _) => Card(key, item, g, skin),
            cardHeight: _ => g.Height, measured: false, pager: ShelfPager.None, controller: controller,
            minCardW: 1f, maxCardW: 1_000_000f, maxColumns: 1, gap: g.PageGap, edgeFade: 0f, snap: ShelfSnap.Page,
            offPageDim: HomeModuleLayout.OffPageDim, pageMemoryKey: key, prevGlyph: prevGlyph, nextGlyph: nextGlyph, lift: ShelfLift.None,
            keyOf: static (item, _) => "spot-page:" + item.Key)
            with { Key = "spot-pages:" + key };
        // The loading shape is the first page, as the shelf shows it: PagedShelf's own proxy lays a row of cards at the fitted width, and a
        // card is the page's whole width.
        if (pages.Is(out ComponentEl shelf)) pages = shelf with { SkeletonProxy = () => SpotlightCard.Proxy(key, items[0], g, skin) };
        return new BoxEl
        {
            Direction = 1, Gap = g.StripGap, MinWidth = 0f, AlignItems = FlexAlign.Stretch, Height = g.Extent(items.Length), Shrink = 0f,
            Children = [pages, Embed.Comp(new StripProps(key, items, g, skin, controller), static () => new SpotlightStrip())],
        };
    }

    static Element Card(string key, SpotlightItem item, ReleaseSpotlightGeometry g, ReleaseSpotlightSkin skin)
        => Embed.Comp(new SpotlightCardProps(key, item, g, skin), static () => new SpotlightCard())
            with { Key = "spot-card:" + key + ":" + item.Key, SkeletonProxy = () => SpotlightCard.Proxy(key, item, g, skin) };

    /// <summary>The body's height for <paramref name="zone"/> (the chapter's exact extent, under its header).</summary>
    public static float Extent(Zone zone, ReleaseSpotlightGeometry g) => g.Extent(SpotlightModel.Count(zone));

    /// <summary>Whether <paramref name="zone"/> pages (its header then carries the pager).</summary>
    public static bool Pages(Zone zone) => SpotlightModel.Count(zone) > 1;
}

/// <summary>The mounted spotlight cards (the inspector's `home do=spotlight`).</summary>
public static class SpotlightRegistry
{
    public static readonly List<SpotlightCard> Live = new();
}

sealed record StripProps(string Key, SpotlightItem[] Items, ReleaseSpotlightGeometry Geo, ReleaseSpotlightSkin Skin, ShelfController? Controller)
{
    public bool Equals(StripProps? o) => o is not null && Key == o.Key && Geo.Equals(o.Geo) && ReferenceEquals(Skin, o.Skin)
        && ReferenceEquals(Controller, o.Controller) && Items.AsSpan().SequenceEqual(o.Items);

    public override int GetHashCode() => HashCode.Combine(Key, Items.Length);
}

/// <summary>The waiting covers under a paging spotlight's right end: the releases after the one shown (around the end), a click turns to it.</summary>
sealed class SpotlightStrip : Component
{
    public override Element Render()
    {
        var p = UseProps<StripProps>();
        int n = p.Items.Length;
        int page = p.Controller is { } c ? Math.Clamp(c.Page.Value, 0, Math.Max(0, n - 1)) : 0;
        int count = Math.Min(n - 1, ReleaseSpotlightRules.NextCovers);
        var kids = new Element[count + 1];
        kids[0] = new BoxEl { Grow = 1f, MinWidth = 0f, HitTestVisible = false };
        var controller = p.Controller;
        for (int k = 0; k < count; k++)
        {
            int at = (page + 1 + k) % n;
            var item = p.Items[at];
            kids[k + 1] = Controls.Named(new BoxEl
            {
                Key = "spot:next:" + item.Key, Width = p.Geo.NextCover, Height = p.Geo.NextCover, Shrink = 0f,
                Cursor = CursorId.Hand, Role = AutomationRole.Button, Focusable = true,
                OnClick = () => controller?.GoTo(at),
                Children = [p.Skin.NextCover(item, p.Geo)],
            }, Strings.Home.Spotlight.ShowRelease(item.Title));
        }
        return new BoxEl
        {
            Direction = 0, Gap = p.Geo.NextGap, Height = p.Geo.NextCover, MinWidth = 0f, Shrink = 0f, AlignItems = FlexAlign.Center,
            Children = kids,
        };
    }
}

sealed record SpotlightCardProps(string Key, SpotlightItem Item, ReleaseSpotlightGeometry Geo, ReleaseSpotlightSkin Skin)
{
    public bool Equals(SpotlightCardProps? o) => o is not null && Key == o.Key && Item.Equals(o.Item) && Geo.Equals(o.Geo) && ReferenceEquals(Skin, o.Skin);

    public override int GetHashCode() => HashCode.Combine(Key, Item.Key);
}

/// <summary>One release's card. Mounted through <see cref="ReleaseSpotlightView.Of"/>.</summary>
public sealed class SpotlightCard : Component, IPreviewOwner
{
    Signal<SpotlightFacts> _facts = null!;   // created at the first render (a render never writes a signal); FactsWatch writes it
    readonly Signal<int> _live = new(-1);    // the song being previewed, -1 none
    SpotlightItem _item = null!;
    ReleaseSpotlightGeometry _g;
    string _key = "";
    string[] _tiles = [];
    int _face, _settled = -1, _releasing = -1, _inspectorHold = -1;
    AnimEngine? _anim;
    InputHooks? _hooks;
    TimerHandle _settleTimer, _releaseTimer;
    bool _armed;
    NodeHandle _root, _progress;
    NodeHandle[] _layer = [], _still = [], _row = [];

    /// <summary>The card's loading proxy: its own tree over the facts on hand, nothing wired (the skeleton derives it flat at its real size).</summary>
    internal static Element Proxy(string key, SpotlightItem item, ReleaseSpotlightGeometry g, ReleaseSpotlightSkin skin)
        => Tree(new SpotlightCardProps(key, item, g, skin), item.IsBlank ? SpotlightFacts.Blank : SpotlightModel.FactsOf(item, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), null);

    static readonly SpringParams s_swap = SpringParams.FromResponse(SpineShelfRules.SwapMs / 1000f, 1f);
    static readonly SpringParams s_face = SpringParams.FromResponse(ReleaseSpotlightRules.FaceMs / 1000f, 1f);

    // ── reads for the inspector ──
    public string Key => _key;
    public SpotlightItem Item => _item;
    public int LiveSong => _live.Peek();
    public SpotlightFacts Facts => _facts.Peek();
    public bool Shown => !_root.IsNull && Context.Scene is { } sc && sc.IsLive(_root);

    /// <summary>How much of the card the window shows (DIP²): a paged spotlight keeps its neighbours mounted off screen.</summary>
    public float VisibleArea => Shown && Context.Scene is { } sc && sc.VisibleWindowRect(_root) is var r ? MathF.Max(0f, r.W) * MathF.Max(0f, r.H) : 0f;

    public override Element Render()
    {
        var p = UseProps<SpotlightCardProps>();
        var item = p.Item;
        var g = p.Geo;
        var skin = p.Skin;
        bool first = _tiles.Length == 0 && _key.Length == 0;
        _item = item;
        _g = g;
        _key = p.Key;
        _anim = Context.Anim;
        _hooks = UseContext(InputHooks.Current);
        ShelfPreview.Warm(UseContext(VideoCompositor.Current) is not null);
        int n = item.Songs.Length;
        if (_tiles.Length != n || first)
        {
            _tiles = new string[n];
            for (int j = 0; j < n; j++) _tiles[j] = TileKey(p.Key, item, j);
            _layer = new NodeHandle[n]; _still = new NodeHandle[n]; _row = new NodeHandle[n];
            _face = Math.Min(item.FaceSong, Math.Max(0, n - 1));
        }
        if (first) _facts = new Signal<SpotlightFacts>(SpotlightModel.FactsOf(item, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        var facts = _facts.Value;

        _settleTimer = UseTimeout(SettleFired, ReleaseSpotlightRules.SettleMs, DepKey.Empty);
        _releaseTimer = UseTimeout(ReleaseVideo, SpineShelfRules.SwapMs, DepKey.Empty);
        if (!_armed) { _armed = true; _settleTimer.Cancel(); _releaseTimer.Cancel(); }

        UseEffect(() =>
        {
            SpotlightRegistry.Live.Add(this);
            PreviewAudio.ClipStarted += OnClipStarted;
            SpotlightModel.Prepare(_item);
            ShelfPreview.Prepare(_item.Songs);
            return () =>
            {
                SpotlightRegistry.Live.Remove(this);
                PreviewAudio.ClipStarted -= OnClipStarted;
                if (_live.Peek() >= 0 && ShelfPreview.Live.Peek() == TileOf(_live.Peek())) ShelfPreview.Leave();
                if (Owns(ShelfPreview.Showing.Peek())) ShelfPreview.Release(ShelfPreview.Showing.Peek());
                PreviewAudio.Disown(this);
            };
        }, DepKey.Empty);

        return Tree(p, facts, this);
    }

    /// <summary>The card's tree: what the mounted card renders (<paramref name="card"/> its instance: the pointer's handlers, the nodes it
    /// animates) and, with no card, its loading proxy (the same builders over the same facts, nothing wired): the skeleton derives the real
    /// card at its real size.</summary>
    internal static Element Tree(SpotlightCardProps p, SpotlightFacts facts, SpotlightCard? card)
    {
        var item = p.Item;
        var g = p.Geo;
        var skin = p.Skin;
        // The words: the song list for an EP or an album (the geometry's rows), the theme's copy around it.
        Element? tracks = item.ListsSongs && g.TrackRows > 0 ? Tracks(item, facts, g, skin, card) : null;
        var copy = new BoxEl
        {
            Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, MinHeight = 0f, Padding = g.CopyPad, ClipToBounds = true,
            Justify = FlexJustify.Center,
            Children = [skin.Copy(item, facts, g, tracks)],
        };
        Element cover = g.Narrow
            ? new BoxEl
            {
                Width = g.CoverBleeds ? float.NaN : g.Cover, Height = g.NarrowCoverH, Shrink = 0f, MinWidth = 0f,
                AlignSelf = g.CoverBleeds ? FlexAlign.Stretch : FlexAlign.Start, Children = [skin.Cover(item, g)],
            }
            : new BoxEl
            {
                Width = g.Cover, Height = g.CoverBleeds ? float.NaN : g.Cover, Shrink = 0f, MinWidth = 0f,
                AlignSelf = g.CoverBleeds ? FlexAlign.Stretch : FlexAlign.Center, Children = [skin.Cover(item, g)],
            };
        var parts = new List<Element>(3) { cover, copy };
        // The wide form always carries the canvas tile (a song with no canvas shows its cover on a blur of its colours there): the card's
        // columns never change when the preview songs land.
        if (!g.Narrow) parts.Add(CanvasTile(p.Key, item, g, card));
        var layout = new BoxEl
        {
            Direction = (byte)(g.Narrow ? 1 : 0), Gap = g.Gap, Padding = Edges4.All(g.Pad), MinWidth = 0f, Grow = 1f,
            AlignItems = FlexAlign.Stretch, Children = parts.ToArray(),
        };
        var progress = new BoxEl
        {
            Height = g.ProgressH, AlignSelf = FlexAlign.End, Fill = skin.Progress(), HitTestVisible = false,
            ScaleX = 0f, TransformOriginX = 0f, OnRealized = card is null ? null : h => card._progress = h,
        };
        var inner = new BoxEl
        {
            ZStack = true, Grow = 1f, MinWidth = 0f, AlignItems = FlexAlign.Stretch, Height = g.Height,
            // The proxy carries no progress line (at rest it is scaled to nothing; derived, it would be a bar across the card).
            Children = card is null ? [layout] : [layout, progress, Embed.Comp(new WatchProps(card), static () => new FactsWatch())],
        };
        return new BoxEl
        {
            Key = "spot:card:" + item.Key, Direction = 1, Height = g.Height, MinWidth = 0f, Shrink = 0f, AlignItems = FlexAlign.Stretch,
            IsolateLayout = true,
            OnHoverMove = card is null ? null : _ => card.CardHover(), OnPointerExit = card is null ? null : card.CardLeave,
            OnRealized = card is null ? null : h => card._root = h,
            Children = [skin.Frame(item, g, inner)],
        };
    }

    // ── the parts ───────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>An EP's or an album's first songs: each row previews its song under the pointer (or focus) and plays from it on a click.</summary>
    static Element Tracks(SpotlightItem item, SpotlightFacts facts, ReleaseSpotlightGeometry g, ReleaseSpotlightSkin skin, SpotlightCard? card)
    {
        // Always the geometry's rows: a song the lookup has not named (yet, or at all) holds its row as a quiet placeholder.
        int count = g.TrackRows;
        var rows = new Element[count];
        var live = card?._live;
        var clear = new ColorF(0f, 0f, 0f, 0f);
        for (int j = 0; j < count; j++)
        {
            var song = j < item.Songs.Length ? item.Songs[j] : ShelfSong.Blank;
            int code = j;
            string duration = j < facts.Durations.Length ? facts.Durations[j] : "";
            var liveFill = skin.TrackLive();
            rows[j] = new BoxEl
            {
                Key = "spot:track:" + item.Key + ":" + j.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Direction = 0, Height = g.TrackRowH, Shrink = 0f, MinWidth = 0f, AlignItems = FlexAlign.Center,
                Corners = CornerRadius4.All(g.TrackRadius),
                Fill = live is null ? clear : Prop.Of(() => live.Value == code ? liveFill : clear),
                HoverFill = card is null ? clear : liveFill, PressedFill = card is null ? clear : liveFill,
                Focusable = !song.IsBlank, AllowFocusOnInteraction = false, Cursor = song.IsBlank ? CursorId.Arrow : CursorId.Hand, Role = AutomationRole.Button,
                HitTestVisible = card is not null && !song.IsBlank,
                OnClick = card is null ? null : () => card.PlayFrom(code), OnHoverMove = card is null ? null : _ => card.Enter(code),
                OnFocusChanged = card is null ? null : f => card.RowFocus(code, f),
                OnRealized = card is null ? null : h => { if (code < card._row.Length) card._row[code] = h; },
                Children = [skin.TrackRow(song, j, duration, g)],
            };
        }
        return new BoxEl { Direction = 1, MinWidth = 0f, Shrink = 0f, Children = rows };
    }

    /// <summary>The canvas tile: one layer per song (its canvas still with the video under it, or its cover on a blur of its colours), the face
    /// song's at rest; the layers cross-fade when the pointer moves to another song.</summary>
    static Element CanvasTile(string key, SpotlightItem item, ReleaseSpotlightGeometry g, SpotlightCard? card)
    {
        int face = card?._face ?? item.FaceSong;
        var layers = new List<Element>(item.Songs.Length);
        for (int j = 0; j < item.Songs.Length; j++)
        {
            var song = item.Songs[j];
            if (song.IsBlank) continue;
            int code = j;
            string tile = TileKey(key, item, j);
            Element[] media;
            if (song.HasCanvas)
                media =
                [
                    card is null ? new BoxEl { HitTestVisible = false } : Embed.Comp(new VideoProps(card, tile), static () => new SongVideo()),
                    new BoxEl
                    {
                        Key = "spot:still:" + tile, ZStack = true, HitTestVisible = false,
                        OnRealized = card is null ? null : h => { if (code < card._still.Length) card._still[code] = h; },
                        Children = [new ImageEl { Source = song.Still ?? song.Cover ?? "", Fit = ImageFit.Cover, DecodePx = SpineShelfRules.StillDecodePx }],
                    },
                ];
            else if ((song.Cover ?? item.Cover) is { } cover)
            {
                float edge = MathF.Min(g.CanvasW, g.CanvasH) * SpineShelfRules.CoverShare;
                media =
                [
                    new ImageEl { Source = cover, Fit = ImageFit.Cover, DecodePx = SpineShelfRules.BlurDecodePx },
                    new BoxEl { Fill = new ColorF(0f, 0f, 0f, SpineShelfRules.BlurDim), HitTestVisible = false },
                    new BoxEl
                    {
                        AlignSelf = FlexAlign.Center, JustifySelf = FlexAlign.Center, Width = edge, Height = edge, HitTestVisible = false,
                        Children = [Controls.Artwork(cover, edge, edge, g.CanvasRadius)],
                    },
                ];
            }
            else media = [];
            layers.Add(new BoxEl
            {
                Key = "spot:layer:" + tile, ZStack = true, HitTestVisible = false, Opacity = j == face ? 1f : 0f,
                OnRealized = card is null ? null : h => { if (code < card._layer.Length) card._layer[code] = h; },
                Children = media,
            });
        }
        // No song named yet (the answer or the lookup on its way): the tile's flat placeholder at its size.
        if (layers.Count == 0) layers.Add(Controls.Artwork(null, g.CanvasW, g.CanvasH, g.CanvasRadius));
        return new BoxEl
        {
            Key = "spot:canvas:" + item.Key, ZStack = true, ClipToBounds = true, Corners = CornerRadius4.All(g.CanvasRadius),
            Width = g.CanvasW, Height = g.CanvasH, Shrink = 0f, AlignSelf = FlexAlign.Center, Gradient = Wash(item.Accent),
            HitTestVisible = false, Children = layers.ToArray(),
        };
    }

    /// <summary>The canvas tile's ground under its media: the cover's own colour, darker at the foot (the shelf tile's).</summary>
    static GradientSpec Wash(uint accent)
    {
        var c = accent != 0u ? ColorScience.ToColor(accent) : new ColorF(0.16f, 0.16f, 0.17f, 1f);
        return new GradientSpec(GradientShape.Linear, 110f, [new GradientStop(0f, ColorF.Lerp(c, new ColorF(1f, 1f, 1f, 1f), 0.18f)), new GradientStop(1f, ColorF.Lerp(c, new ColorF(0f, 0f, 0f, 1f), 0.45f))]);
    }

    // ── acts (the pointer, the keyboard and the inspector all come here) ───────────────────────────────────────────────

    /// <summary>Song <paramref name="j"/>'s preview tile key: "zone|album#j" (ShelfPreview's and PreviewAudio's identity).</summary>
    static string TileKey(string key, SpotlightItem item, int j) => key + "|" + item.Key + "#" + j.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>A listed song's row took or lost keyboard focus: focus previews it as the pointer does.</summary>
    void RowFocus(int code, bool focused)
    {
        if (focused) Enter(code);
        else if (_live.Peek() == code) Leave(code, crossFade: true);
    }

    string TileOf(int code) => (uint)code < (uint)_tiles.Length ? _tiles[code] : "";

    bool Owns(string tile) => tile.Length > 0 && Array.IndexOf(_tiles, tile) >= 0;

    /// <summary>The pointer moved on the card: with nothing previewing yet, its face song starts (a move inside the card keeps the song it is on).</summary>
    void CardHover()
    {
        if (_live.Peek() >= 0) return;
        Enter(_face);
    }

    /// <summary>The pointer left the card: the preview fades out, the canvas still fades back in over its video.</summary>
    void CardLeave()
    {
        int code = _live.Peek();
        if (code >= 0 && code == _inspectorHold) return;
        Leave(code, crossFade: true);
    }

    /// <summary>Preview song <paramref name="code"/>: the claim moves first (so the song left hands its clip over: the two cross), the canvas
    /// layers cross-fade, and the media opens once the pointer has rested SettleMs.</summary>
    public void Enter(int code)
    {
        if ((uint)code >= (uint)_item.Songs.Length || code == _live.Peek()) return;
        var song = _item.Songs[code];
        if (song.IsBlank) return;
        if (song.Clip is not null) PreviewAudio.Claim(this, code, _tiles[code]);
        Leave(_live.Peek(), crossFade: true);
        _live.Value = code;
        if (code != _face && _anim is { } a)
        {
            if ((uint)_face < (uint)_layer.Length && !_layer[_face].IsNull) a.Spring(_layer[_face], AnimChannel.Opacity, 0f, s_face);
            if (!_layer[code].IsNull) a.Spring(_layer[code], AnimChannel.Opacity, 1f, s_face);
        }
        _face = code;
        ShelfPreview.Enter(_tiles[code], in song);
        _settleTimer.RestartIn(ReleaseSpotlightRules.SettleMs);
    }

    void SettleFired()
    {
        int code = _live.Peek();
        if (code < 0) return;
        _settled = code;
        if (_releasing == code)
        {
            _releasing = -1;
            _releaseTimer.Cancel();
            HideStill(code);
        }
        ShelfPreview.Settled(_tiles[code]);
    }

    /// <summary>Song <paramref name="code"/> stops previewing: its clip fades out, and a video it shows stays under its still while the still
    /// fades back in over it (SwapMs), then it is released. Without <paramref name="crossFade"/> the video goes at once under the still.</summary>
    void Leave(int code, bool crossFade)
    {
        if (code < 0 || code != _live.Peek()) return;
        if (_inspectorHold == code) _inspectorHold = -1;
        _settleTimer.Cancel();
        _settled = -1;
        bool shows = ShelfPreview.Showing.Peek() == _tiles[code];
        if (shows && crossFade && _anim is { } a && !_still[code].IsNull)
        {
            if (_releasing >= 0 && _releasing != code) ReleaseNow();
            _releasing = code;
            a.Spring(_still[code], AnimChannel.Opacity, 1f, s_swap);
            _releaseTimer.RestartIn(SpineShelfRules.SwapMs + SpineShelfRules.RestFrameMs);
            ShelfPreview.Leave(release: false);
        }
        else
        {
            if (shows) ShowStill(code);
            if (_releasing >= 0) ReleaseNow();
            ShelfPreview.Leave();
        }
        if (_anim is { } p && !_progress.IsNull) p.SeedEased(_progress, AnimChannel.ScaleX, 0f, 0f, 1f, Easing.Linear);
        _live.Value = -1;
    }

    void ReleaseVideo()
    {
        if (_releasing >= 0) ReleaseNow();
    }

    void ReleaseNow()
    {
        int code = _releasing;
        _releasing = -1;
        _releaseTimer.Cancel();
        if (code < 0) return;
        ShowStill(code);
        ShelfPreview.Release(_tiles[code]);
    }

    internal void VideoPresented(string tile)
    {
        int code = _live.Peek();
        if (code < 0 || tile != _tiles[code] || _releasing == code) return;
        Log.Info("home", "spotlight.canvas presented " + tile + " (the still fades out)");   // cold: once per hover
        HideStill(code);
    }

    void HideStill(int code)
    {
        if (_anim is not { } a || (uint)code >= (uint)_still.Length || _still[code].IsNull) return;
        a.Spring(_still[code], AnimChannel.Opacity, 0f, s_swap);
    }

    void ShowStill(int code)
    {
        if (_anim is not { } a || (uint)code >= (uint)_still.Length || _still[code].IsNull) return;
        a.SeedEased(_still[code], AnimChannel.Opacity, 1f, 1f, 1f, Easing.Linear);
    }

    void OnClipStarted(string tile)
    {
        int code = _live.Peek();
        if (code < 0 || tile != _tiles[code]) return;
        if (_anim is { } a && !_progress.IsNull) a.SeedEased(_progress, AnimChannel.ScaleX, 0f, 1f, SpineShelfRules.ClipMs, Easing.Linear);
    }

    /// <summary>A canvas file landed: a settled song that waited for it presents it now.</summary>
    internal void FileLanded()
    {
        int code = _live.Peek();
        if (code >= 0 && _settled == code) ShelfPreview.Settled(_tiles[code]);
    }

    /// <summary>A fact the card shows changed (a lookup landed): the card re-renders with it.</summary>
    internal void FactsChanged(SpotlightFacts facts)
    {
        if (!facts.Equals(_facts.Peek())) _facts.Value = facts;
    }

    /// <summary>Play the release from <paramref name="code"/> (a listed song's click).</summary>
    void PlayFrom(int code)
    {
        if ((uint)code >= (uint)_item.Songs.Length || _item.Songs[code].IsBlank) return;
        Playback.PlayContext(_item.Card.Id, _item.Songs[code].Card.Id);
    }

    /// <summary>The inspector's hover: song <paramref name="code"/> (-1 the card's face song; -2 leaves). It stands for a pointer resting on the
    /// card, so the ownership check counts it as held until it leaves; `hover key=` and `pointer path=` drive the real pointer instead.</summary>
    public void HoverSong(int code)
    {
        if (code == -2) { _inspectorHold = -1; Leave(_live.Peek(), crossFade: true); return; }
        int to = code < 0 ? _face : code;
        Enter(to);
        if (_live.Peek() == to) _inspectorHold = to;
    }

    // ── the ownership check (PreviewAudio, every tick) ──────────────────────────────────────────────────────────────

    /// <summary>"" while this card still holds song <paramref name="code"/>, else why not: mounted and shown, the song live here AND named by
    /// ShelfPreview.Live (audio and video agree on one owner), the window active and not minimized, and the pointer inside the card's visible rect
    /// or keyboard focus on the song's row. Allocation-free.</summary>
    public string HoldFails(int code, string tile)
    {
        if (!SpotlightRegistry.Live.Contains(this)) return "unmounted";
        if (!Shown) return "hidden";
        if (_live.Peek() != code) return "not-live";
        if (!string.Equals(ShelfPreview.Live.Peek(), tile, StringComparison.Ordinal)) return "not-preview-live";
        if (Context.Scene is not { } scene) return "tile-gone";
        var hooks = _hooks;
        if (hooks?.GetWindowState is { } state && state() == FluentGpu.Pal.WindowState.Minimized) return "minimized";
        if (hooks?.IsWindowActive is { } active && !active()) return "window-inactive";
        if (_inspectorHold == code) return "";
        if (hooks?.GetFocus is { } focus && (uint)code < (uint)_row.Length && !_row[code].IsNull && focus() == _row[code]) return "";
        if (hooks?.IsPointerInWindow is { } inWindow && !inWindow()) return "pointer-left-window";
        if (hooks?.GetPointerPosition?.Invoke() is not { } point) return "no-pointer";
        return scene.VisibleWindowRect(_root).Contains(point) ? "" : "pointer-off-card";
    }

    public void HoldLost(int code)
    {
        if (code >= 0 && code == _live.Peek()) Leave(code, crossFade: true);
    }

    // ── the watchers and the canvas video ───────────────────────────────────────────────────────────────────────────

    sealed record WatchProps(SpotlightCard Card);

    /// <summary>Re-renders on the tables the facts read (the album, its artist, its songs) and on a canvas file landing, and tells the card from an
    /// effect (never from the render). The previews' frame clock rides here too.</summary>
    sealed class FactsWatch : Component
    {
        public override Element Render()
        {
            var card = UseProps<WatchProps>().Card;
            var scope = Entities.Current;
            _ = Entities.ScopeEpoch.Value;
            uint stamp = (uint)scope.Albums.Changed.Value ^ ((uint)scope.Artists.Changed.Value << 8) ^ ((uint)scope.Tracks.Changed.Value << 16)
                ^ ((uint)scope.Edges.AlbumTracks.Changed.Value << 24);
            int epoch = CanvasVideo.Files.Epoch.Value;
            UseEffect(() => card.FactsChanged(SpotlightModel.FactsOf(card.Item, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())), DepKey.From((int)stamp));
            UseEffect(() => card.FileLanded(), DepKey.From(epoch));
            return new BoxEl { Width = 0f, Height = 0f, HitTestVisible = false, Children = [Embed.Comp(static () => new PreviewAudio.Clock())] };
        }
    }

    sealed record VideoProps(SpotlightCard Card, string Tile);

    /// <summary>A canvas song's video: the one player's surface, mounted only while this song is the one showing (ShelfPreview.Showing), in the
    /// still's box under the still (the shelf tile's, Home/SpineShelf/SpineShelf.cs TileVideo).</summary>
    sealed class SongVideo : Component
    {
        public override Element Render()
        {
            var p = UseProps<VideoProps>();
            bool showing = ShelfPreview.Showing.Value == p.Tile;
            if (!showing || ShelfPreview.Video is not { } player) return new BoxEl { HitTestVisible = false };
            var card = p.Card;
            string tile = p.Tile;
            return new BoxEl
            {
                Direction = 1, HitTestVisible = false, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, MinHeight = 0f,
                AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch,
                Key = "spot:video:" + tile,
                Children = [Embed.Comp(() => new MediaPlayerElement
                {
                    Player = player, IsDecorative = true, AreTransportControlsEnabled = false, ShowStatusOverlay = false,
                    ShowLetterboxBars = false, Stretch = MediaStretch.UniformToFill,
                    SurfacePresented = () => card.VideoPresented(tile),
                })],
            };
        }
    }
}
