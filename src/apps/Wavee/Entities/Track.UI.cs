// ── Entities/Track.UI.cs ───────────────────────────────────────────────────────────────────────────────────────────
// the ROW's core half: the state a row reflects (`StateOf`, the like edge, `Invoke`), the width tracks, the eager row's HOST
// (`EagerRow`), the lane wrappers and navigation the pages call, `RowData` (the track adapter of the shared media surface) and the
// "+N" artist chip. The row's PICTURE moved to the structure in T8c: the eager grid, the # state machine, the heart, the metadata line,
// the Classic title line and every other cell are `Themes\Classic\Structure\Lists\ClassicRowCells.cs`, the eager row's container
// `ClassicEagerRow.cs` (`StyleKeys.EagerTrackRow`). The track menu composition and the thirteen track verbs are the named partial
// `Track.Menu.cs`.
//
// Role: UI
// Owner: M
// Wave: 4.5
// Budget: 2600 lines (Track.UI.cs + Track.Menu.cs)
// Spec: ch 01 §9
//
// ── ONE CELL, EVERY SURFACE (ch 01 §0.1) ────────────────────────────────────────────────────────────────────────────
//
// `Grid` (now `ClassicRowCells.Grid`, reached through `EagerRowStyle.Grid`) is the single definition of what a track row looks
// like. The album drawer, Recents, Home's top tracks and the rails call it; the detail table's virtualized rows use its bound twin
// `BoundGrid` (`ClassicTrackTable`, from Track.UI.Bound.cs — same lanes, keys and wrappers, built once per slot). Callers vary only the `ColumnSet` and the container skin (the table's bound
// skin, `EagerRow`). Everything here is built from RESOLVED values and re-run by the HOST — the only members that
// subscribe on their own are the ones that say so (`StateOf`, `IsNowPlaying`, `RowData`, and the two component hosts:
// the eager row and the "+N" chip).
//
// ── WHAT A RECYCLE COSTS ─────────────────────────────────────────────────────────────────────────────────────────────
//
// A host re-run rebuilds the element records of ONE row. No list is ever grown and copied (every child array is
// counted first and allocated exactly), no LINQ, no per-recycle number format (row numbers come from a lazily grown
// label table; the rest go through `Track.Format`), and a span line allocates ONE click closure resolved by index, not
// one closure per artist. The width tracks are cached per (ColumnSet, art) so the header and every row hold the SAME
// array instance (ch 04 §0.1).
//
// ── HOVER REVEALS ────────────────────────────────────────────────────────────────────────────────────────────────────
//
// Every reveal (`HoverOpacity`) sits on a NON-interactive wrapper, so it inherits hover progress from the nearest
// interactive ancestor — the row skin — and survives the pointer crossing onto the button inside it (parity 10-11).
// A click target inside a reveal is its own child box. Every in-row affordance sets `BlocksDragArm` (ch 01 §0.12).

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

public readonly partial struct Track
{
    // ══ 1. KEYS, STATE, THE WIDTH TRACKS ═════════════════════════════════════════════════════════════════════════════

    /// <summary>Stable per-column cell keys, shared by the row grid and the header grid, so a lane that leaves at a
    /// breakpoint is removed by the keyed diff instead of shifting every later cell onto the wrong column (ch 01 §9 —
    /// without them a surviving Added-by cell is patched with the departed Album cell's content).</summary>
    public static class CellKey
    {
        public const string Num = "c.num", Heart = "c.heart", Art = "c.art", Title = "c.title", Artist = "c.artist",
            Album = "c.album", By = "c.by", Date = "c.date", Plays = "c.plays", Tempo = "c.tempo",
            Duration = "c.dur", More = "c.more", Expand = "c.expand";
        /// <summary>The TRAILING ♥ lane. Its OWN key, not <see cref="Heart"/>'s: the two are mutually exclusive, so
        /// reusing one key would let the keyed diff MOVE the leading heart's node into the trailing ordinal instead of
        /// remounting it — and the two cells differ in exactly the thing mount-only bound wiring decides (the reveal
        /// wrapper's bound <c>Opacity</c>). Distinct keys make that swap a remount.</summary>
        public const string HeartTrailing = "c.heart.t";
    }

    /// <summary>The per-row playback/library state the cells reflect.</summary>
    public readonly record struct RowState(bool IsNow, bool IsPlaying, bool IsBuffering, bool IsTop, bool Saved);

    /// <summary>The row's state, read REACTIVELY: subscribes to <c>Playback.CurrentId</c> (always), and — only on the
    /// now-playing row — to <c>Playback.IsPlaying</c> / <c>Buffering</c> / <c>Pending.Load</c>, plus the Liked edge
    /// table's <c>Changed</c>. <paramref name="extraBuffering"/> is the host's own "this row's play is in flight".</summary>
    public static RowState StateOf(Track t, bool isTop = false, bool extraBuffering = false)
    {
        bool isNow = IsNowPlaying(t);
        bool playing = isNow && Playback.IsPlaying.Value;
        bool buffering = extraBuffering || (isNow && (Playback.Buffering.Value || Playback.Pending.Load.Value));
        return new RowState(isNow, playing, buffering, isTop, LikedNow(t));
    }

    /// <summary>Is this the playable the deck is on? A subscribing read of <c>Playback.CurrentId</c> — compared by
    /// IDENTITY, not slot, so a podcast row (a Track-table row whose playable is an episode) and a remote device's freshly
    /// allocated row still match.</summary>
    public static bool IsNowPlaying(Track t)
    {
        var playing = Playback.CurrentId.Value;
        return t.Slot > 0 && !playing.IsEmpty && playing == t.Id;
    }

    /// <summary>Single-click semantics shared by every surface's play affordance: the now-playing row toggles pause /
    /// resume, any other row runs <paramref name="startDifferent"/>. A peek — this is a click, not a render.</summary>
    public static void Invoke(Track t, Action startDifferent)
    {
        var playing = Playback.CurrentId.Peek();
        bool deckRow = t.Slot > 0 && !playing.IsEmpty && playing == t.Id;
        // The deck row under a FAULT is a dead row: the reducer's Resume returns while `Error != None`, so a toggle
        // there is a click that does nothing. `Shell.PlayerBarRules.RowVerb` says so once; Start runs the host's play,
        // which goes through `PutOnDeck` and clears the fault (the same heal the bar's Retry performs).
        if (Shell.PlayerBarRules.RowVerb(deckRow, Playback.Error.Peek()) == Shell.RowAction.Toggle)
        {
            Playback.TogglePlay("track.row");
            return;
        }
        startDifferent();
    }

    internal static bool LikedNow(Track t)
    {
        _ = Entities.Current.Edges.Liked.Changed.Value;   // subscribe: an optimistic like re-skins the heart in the same frame
        var me = User.Me;
        return t.Slot > 0 && me.Slot > 0 && me.Likes(t);
    }

    static readonly Dictionary<(ColumnSet Set, float Art), TrackSize[]> s_rowTrackCache = new();

    /// <summary>THE width tracks for a column set, cached per (set, art): the header and the rows read the SAME array
    /// instance (ch 04 §0.1), and a scroll frame never rebuilds one (ch 01 §9, "Where the plan is wrong" 7). Order:
    /// # · ♥ · art · Title* · Artist* · Album* · By · Date · Plays · Tempo · ♥(trailing) · Duration · Actions · Expand.
    /// The key space is finite (a dozen flags × seven tiers × four art rungs), so the cache is never trimmed —
    /// trimming would hand the header and the rows two different instances for one shape. The KEY is the whole
    /// <see cref="ColumnSet"/>, so <see cref="ColumnSet.HeartTrailing"/> forks the cache by construction.</summary>
    public static TrackSize[] TracksFor(in ColumnSet set, float art)
    {
        var key = (set, art);
        if (s_rowTrackCache.TryGetValue(key, out var cached)) return cached;
        var arr = new TrackSize[CellCount(in set)];
        int i = 0;
        arr[i++] = TrackSize.Px(Lane.Num);
        if (set.Heart) arr[i++] = TrackSize.Px(Lane.Heart);
        if (set.Thumb) arr[i++] = TrackSize.Px(art);
        arr[i++] = TrackSize.Star(Lane.TitleStar);
        if (set.Artist) arr[i++] = TrackSize.Star(Lane.ArtistStar);
        if (set.Album) arr[i++] = TrackSize.Star(Lane.AlbumStar);
        if (set.By) arr[i++] = TrackSize.Px(Lane.By);
        if (set.Date) arr[i++] = TrackSize.Px(Lane.Date);
        if (set.Plays) arr[i++] = TrackSize.Px(Lane.Plays);
        // The SAME gate the cell uses, so the width track and the cell can never disagree (a mismatch shifts every
        // later column).
        if (RowMetrics.ShowTempo(in set)) arr[i++] = TrackSize.Px(Lane.Tempo);
        // The trailing ♥ — the SAME gate the cell reads, and it sits IMMEDIATELY before the duration (the reader's
        // 28 | 1fr | 32 | 52), never after it: the duration is the row's last fact, and a heart past it reads as chrome.
        if (RowMetrics.ShowHeartTrailing(in set)) arr[i++] = TrackSize.Px(Lane.HeartTrailing);
        if (RowMetrics.ShowDuration(in set)) arr[i++] = TrackSize.Px(Lane.Duration);
        if (set.Actions) arr[i++] = TrackSize.Px(Lane.Actions);
        if (set.Expand) arr[i++] = TrackSize.Px(Lane.Expand);
        s_rowTrackCache[key] = arr;
        return arr;
    }

    /// <summary>How many cells (and width tracks) a set produces. ONE count for both, so a row always emits exactly one
    /// cell per track.
    /// <para>THREE builders size themselves from this: <c>Grid</c>, the bound twin <c>BoundGrid</c>
    /// (Track.UI.Bound.cs) and the table's column header (Track.Table.Chrome.cs). All three emit the TRAILING ♥ cell (the eager
    /// reader row's, and a quiet table's, D16) in the same position, so the cells and the tracks never fall out of step.</para></summary>
    internal static int CellCount(in ColumnSet set)
        => 2 + (RowMetrics.ShowDuration(in set) ? 1 : 0) + (set.Heart ? 1 : 0) + (set.Thumb ? 1 : 0) + (set.Artist ? 1 : 0) + (set.Album ? 1 : 0)
           + (set.By ? 1 : 0) + (set.Date ? 1 : 0) + (set.Plays ? 1 : 0) + (RowMetrics.ShowTempo(in set) ? 1 : 0)
           + (RowMetrics.ShowHeartTrailing(in set) ? 1 : 0)
           + (set.Actions ? 1 : 0) + (set.Expand ? 1 : 0);

    // ══ 2. THE GRID ══════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Everything a caller varies about one grid beyond the state and the lane set.
    /// <para><b>Construct with at least one named argument</b> (<c>new GridOptions(OnPlay: …)</c>): a record struct's
    /// <c>default</c> / parameterless <c>new()</c> zero-initializes and skips the primary-constructor defaults, so
    /// <see cref="MoreEnabled"/> would read false. <see cref="Art"/> 0 is treated as <c>RowMetrics.ThumbSize</c> for the
    /// same reason.</para>
    /// <para><see cref="AddedAt"/> and <see cref="NowUnixSeconds"/> are UNIX seconds; <see cref="NowUnixSeconds"/> 0
    /// resolves the live clock (<c>Store.ToUnix(Entities.Now)</c> — <c>Entities.Now</c> itself is APP seconds).</para>
    /// <para>Both ♥ lanes wear the row reveal (tuned Classic L2): a liked row paints its filled heart at rest, an unliked
    /// one shows its outline only on ROW hover.</para></summary>
    public readonly record struct GridOptions(
        bool ShowTrackArtist = false, float Art = RowMetrics.ThumbSize, Action? OnPlay = null, Action? OnLike = null,
        int AddedAt = 0, User AddedBy = default, byte ChartStatus = 0, bool LikePop = false, Element? ActionsCell = null,
        bool ShowAlbumInMeta = false, bool ShowListBadges = false, Element? ExpandCell = null, bool MoreEnabled = true,
        IReadSignal<bool>? HoverPaused = null, Func<ColorF>? Accent = null, long NowUnixSeconds = 0);

    internal const float RowNotYetOutOpacity = 0.45f;   // ch 01 §0.7: the whole title column steps back, not a colour swap
    internal const float RowEqualizerHeight = 13f;
    internal const float RowTransportBox = 24f;
    const float RowMoreBox = 28f;

    /// <summary>ONE decode size for every row thumbnail, on every density, in both the eager <c>Grid</c> and the
    /// bound <c>BoundArtwork</c>. It used to be <c>art * 2</c>, and <c>Controls.Artwork</c>'s own doc says why that was
    /// the bug: an explicit <c>decodePx</c> decodes a SQUARE at that exact literal and "a card and a detail cover that
    /// pass the SAME literal resolve to ONE cached texture and neither re-decodes". The art ladder is 32 · 40 · 48
    /// (<c>TableRules.ArtSizeFor</c>), so <c>art * 2</c> forked the SAME cover into three textures — 64, 80 and 96 —
    /// and a density change re-decoded every visible row against a 40 MB image cache on Weak-tier GPUs. 96 is the top of
    /// the ladder (Comfortable 48 DIP at 2×), so no density is ever under-sampled.</summary>
    internal const int RowArtDecodePx = 96;

    /// <summary>The cover a row paints: its OWN image, else its album's (ch 01 GAP, the same fallback
    /// <c>Track.Table.Chrome.cs</c>'s pane thumbs and <c>ClassicArtistSections</c>'s video rail already make). A track row is
    /// an entry in an album, and a producer that staged the row without a cover — a playlist item, a search hit, a
    /// terminal "unavailable" verdict — leaves nothing but the flat placeholder tint behind otherwise. The album handle
    /// is checked for validity first: <c>Column&lt;T&gt;</c> is an unguarded slab, so <c>Album.ImageId</c> on
    /// <c>Table.None</c> is not a read to make. The bind re-fires when a late album row lands because
    /// <c>RowPresentation</c> carries the album's <c>Version</c>.
    /// <para>Public, not internal: this assembly has no <c>InternalsVisibleTo</c> (see <c>Playlist.UI.cs</c>), and the
    /// fallback is a rule worth a fact rather than a rule worth re-deriving per surface.</para></summary>
    public static string? RowArtUrl(Track d)
        => Controls.ArtUrl(d.ImageId) ?? (d.Album.IsValid ? Controls.ArtUrl(d.Album.ImageId) : null);

    internal static readonly Marquee.Style s_rowMarqueeStyle = new()
    {
        // The engine defaults for Speed/Gap/delays/fade (ch 30 §3.4, "now-playing ROW marquee"); only size, weight and
        // ink are the row's. The ink is a THUNK so a live theme flip re-tints the frozen style.
        FontSize = 14f, Weight = 600, Foreground = Prop.Of(static () => Tok.AccentTextPrimary),
    };

    // ══ 3. THE CELLS ═════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Per-slot like-edge detector: true only when the SAME row flipped unsaved → saved since this slot's last
    /// render. A recycle binds a different slot, so scrolling never reports an edge (ch 01 §9, parity 22).</summary>
    public static bool LikeEdge(ref int prevSlot, ref bool prevSaved, Track t, bool saved)
    {
        bool edge = saved && !prevSaved && t.Slot > 0 && prevSlot == t.Slot;
        prevSlot = t.Slot;
        prevSaved = saved;
        return edge;
    }

    /// <summary>The row's "…" lane: <c>Controls.MoreButton</c> — the shared affordance with its
    /// <c>ClickRequestsContext</c> and <c>BlocksDragArm</c> — at the row's 28-DIP circle (Modern, Emphatic hover/press) or
    /// a square with no scale (Classic, ch 01 W8). It is invisible at rest in both row styles and appears on ROW hover
    /// (tuned Classic L2): the reveal rides a NON-interactive wrapper so it follows the row. <paramref name="shown"/> (a bound
    /// row's focus fact) also shows it at rest, so the focused row offers it to the keyboard. <paramref name="enabled"/> false is an invisible, inert placeholder that reserves the
    /// lane (a skeleton / overscan row). Not a tab stop — the list owns the one roving stop.
    /// <para><paramref name="box"/> above the 28 (the Compact row's 40 target, board C-RowStates-Compact) sizes the button, and
    /// <paramref name="atRest"/> shows it at rest on every row (a Compact row has no hover to reveal it).</para></summary>
    public static Element MoreCell(bool enabled, bool classic, string? key = null, Func<bool>? shown = null, float box = 0f, bool atRest = false)
    {
        float edge = box > RowMoreBox ? box : RowMoreBox;
        var button = Controls.MoreButton(null, requestsContext: enabled) with
        {
            Width = edge, Height = edge,
            Corners = classic ? CornerRadius4.All(Radii.None) : Radii.Circle(edge),
            HoverScale = classic ? 1f : R.Motion.ScaleEmphatic.Hover,
            PressScale = classic ? 1f : R.Motion.ScaleEmphatic.Press,
            Opacity = 1f, HoverOpacity = float.NaN,
            Focusable = false,
        };
        if (!enabled) button = button with { OnClick = null, Cursor = null, HitTestVisible = false };
        return new BoxEl
        {
            Key = key, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, MinWidth = 0f, ClipToBounds = true,
            Children =
            [
                new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Opacity = enabled && atRest ? (Prop<float>)1f
                        : enabled && shown is not null ? Prop.Of(() => shown() ? 1f : 0f) : (Prop<float>)0f,
                    HoverOpacity = enabled ? 1f : 0f,
                    Children = [button],
                },
            ],
        };
    }

    /// <summary>The expand chevron, both skins (ch 01 W14): 24 DIP, radius 4, the glyph SWAPS — ChevronRight secondary
    /// closed, ChevronDown accent open — so the control states its own state even when its drawer has scrolled away.
    /// Focusable with a Button role: Tab reaches it and Space/Enter toggle it.
    /// <para>Ported rather than <c>Controls.ExpandChevron</c>: that one rotates through a <c>Prop.Of</c> over a captured
    /// bool, and a bound channel is wired at MOUNT — a patched row would keep its first angle (FGRP002).</para></summary>
    public static Element ExpandCell(bool open, Action toggle, string? key = null)
        => RowCenterCell(new BoxEl
        {
            Width = Spacing.XXL, Height = Spacing.XXL, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = Radii.ControlAll, HoverFill = Tok.FillControlSecondary,
            Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
            FocusVisualMargin = Edges4.All(1f),
            OnClick = toggle,
            BlocksDragArm = true,
            Children = [Icon(open ? Icons.ChevronDown : Icons.ChevronRight, 12f, open ? Tok.AccentTextPrimary : Tok.TextSecondary)],
        }, key);

    /// <summary>The indeterminate fetch/buffer ring: 16 DIP in accent (ch 01 W4).</summary>
    public static Element Spinner() => ProgressRing.Indeterminate(size: 16f, foreground: Tok.AccentTextPrimary);

    // ── link spans ───────────────────────────────────────────────────────────────────────────────────────────────────
    //
    // Span layout, exactly allocated: [artist0 ", " artist1 …] [" · " container]. Artist i sits at span 2i; the
    // container is the LAST span. A link is `IsLink` (never a per-span closure) and ONE `OnSpanClick` resolves the index
    // against the CURRENT edge run at click time. The run is the data adapter's too (`RowSubtitle`), so it is core.

    /// <summary>The billed artists as one ellipsized run of per-artist links (Classic's Artist lane, the row data's subtitle, the
    /// queue). A span per artist, ", " joined; a name-only artist (no uri) is plain text. Empty box for no artists.</summary>
    public static Element ArtistLinks(Track t, float size, float lineHeight, ColorF color)
    {
        int count = t.ArtistSlots.Length;
        if (BuildLinkSpans(t, count, default) is not { } spans) return new BoxEl();
        return new SpanTextEl(spans)
        {
            // A run with no LineHeight falls to the shaper's natural box and leaves the rhythm; 12 pins the ramp's 16.
            Size = size, LineHeight = lineHeight > 0f ? lineHeight : size <= 12f ? 16f : float.NaN,
            Color = color, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MaxLines = 1,
            MinWidth = 0f,
            OnSpanClick = LinkClick(t, count, album: false),
        };
    }

    internal static TextSpan[]? BuildLinkSpans(Track t, int artistCount, (EntityUri Uri, string Name) container)
    {
        bool hasContainer = container.Name is { Length: > 0 };
        var slots = t.ArtistSlots;
        if (artistCount > slots.Length) artistCount = slots.Length;
        int artistSpans = artistCount == 0 ? 0 : artistCount * 2 - 1;
        int total = artistSpans + (hasContainer ? (artistSpans > 0 ? 2 : 1) : 0);
        if (total == 0) return null;
        var spans = new TextSpan[total];
        int n = 0;
        for (int i = 0; i < artistCount; i++)
        {
            if (i > 0) spans[n++] = new TextSpan(", ");
            var a = new Artist(slots[i]);
            spans[n++] = new TextSpan(a.Name, IsLink: a.Uri.IsValid);
        }
        if (hasContainer)
        {
            if (n > 0) spans[n++] = new TextSpan(" · ");
            spans[n++] = new TextSpan(container.Name, IsLink: container.Uri.IsValid);
        }
        return spans;
    }

    /// <summary>ONE click closure per run: span 2i → artist i; the trailing span → the container.</summary>
    internal static Action<int> LinkClick(Track t, int artistCount, bool album)
    {
        int artistSpans = artistCount == 0 ? 0 : artistCount * 2 - 1;
        return index =>
        {
            if (index < artistSpans) { if ((index & 1) == 0) GoToArtistAt(t, index / 2); }
            else if (album) GoToContainer(t);
        };
    }


    /// <summary>The row's container: its album, or — for a podcast row, which carries its SHOW in the album slot — the
    /// show, read from the table the row's kind names (ch 04 DATA GAPS).</summary>
    internal static (EntityUri Uri, string Name) ContainerOf(Track t)
    {
        int slot = t.AlbumSlot;
        if (slot <= 0) return (default, "");
        if (t.IsPodcast)
        {
            var show = new Show(slot);
            return show.IsValid ? (show.Uri, show.Title) : (default, "");
        }
        var album = new Album(slot);
        return album.IsValid ? (album.Uri, album.Title) : (default, "");
    }

    // ── navigation ───────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Open an artist page — through the one composer (<c>Shell.For</c>) every "go to" call site uses.</summary>
    public static void GoToArtist(Artist a)
    {
        if (!a.IsValid || !a.Uri.IsValid) return;
        Shell.GoTo(Shell.For(a.Uri, a.Name));
    }

    /// <summary>Open an album page.</summary>
    public static void GoToAlbum(Album a)
    {
        if (!a.IsValid || !a.Uri.IsValid) return;
        Shell.GoTo(Shell.For(a.Uri, a.Title));
    }

    internal static void GoToArtistAt(Track t, int index)
    {
        var slots = t.ArtistSlots;   // re-read at CLICK time: the run may have been re-committed since the render
        if ((uint)index < (uint)slots.Length) GoToArtist(new Artist(slots[index]));
    }

    internal static void GoToContainer(Track t)
    {
        var (uri, name) = ContainerOf(t);
        if (uri.IsValid) Shell.GoTo(Shell.For(uri, name));
    }

    // ── cell wrappers and the factual rung ───────────────────────────────────────────────────────────────────────────
    //
    // Both halves are load-bearing whenever the grid is handed less width than its fixed tracks need: MinWidth 0 lets a
    // cell's content yield instead of flooring at its natural min, and ClipToBounds bounds what still overflows to its own
    // track — without it a squeezed cell paints over its neighbours (the rail-open pile-up, ch 01 §9).

    // Each wrapper takes the lane's key at construction: the grid never `with { Key = … }`-clones a cell after the fact.

    internal static BoxEl RowCenterCell(Element content, string? key = null) => new()
    {
        Key = key, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, MinWidth = 0f, ClipToBounds = true,
        Children = [content],
    };

    internal static BoxEl RowLeftCell(Element content, string? key = null) => new()
    {
        Key = key, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Start, MinWidth = 0f, ClipToBounds = true,
        Children = [content],
    };

    internal static BoxEl RowEndCell(Element content, string? key = null) => new()
    {
        Key = key, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.End, MinWidth = 0f, ClipToBounds = true,
        Children = [content],
    };

    // The keys this file adds (scratchpad batch-loc WP-4.5-U1.json) — literal, so the file compiles before the merge
    // regenerates `Strings`.
    internal const string LocChartNew = "detail.row.chartNew";
    const string LocMoreArtists = "detail.row.moreArtists";

    // ══ 4. THE EAGER ROW ═════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>What an eager row varies. Construct with a named argument (see <see cref="GridOptions"/>'s record-struct
    /// note): <c>default</c> would read <see cref="ShowTrackArtist"/> false.</summary>
    public readonly record struct EagerRowOptions(bool ShowTrackArtist = true, bool Zebra = false, Element? ActionsCell = null,
                                                  Action? OnLike = null, Func<ContextMenuModel?>? Menu = null);

    /// <summary>A self-contained, EAGER interactive row for short preview lists (Home "Top tracks", Recents) — the SAME
    /// <c>Grid</c> inside a hover container that is the interactive ancestor, so the transport reveal, the
    /// equalizer and the heart behave exactly like the virtualized table (ch 01 W17). A component, so the row's hover
    /// signal (the reveal source AND the equalizer's pause gate) survives parent re-renders; it subscribes to playback,
    /// the Liked edge and the track table itself. Single click plays (<see cref="Invoke"/>); right-click / the "…" opens
    /// <see cref="EagerRowOptions.Menu"/>, or the single-track menu when that is null. The title is the plain accent
    /// ellipsis — the marquee is the full lists' now-playing row alone.</summary>
    public static Element EagerRow(Track t, int displayIndex, in ColumnSet set, TrackSize[] tracks, float rowH,
                                   Action onPlay, in EagerRowOptions o)
        => Embed.Comp(new EagerRowProps(t, displayIndex, set, tracks, rowH, onPlay, o), static () => new EagerTrackRowHost());

    /// <summary>Equality is the re-render gate, over DATA only: delegates are ignored (the host invokes the newest from
    /// its plain field), the actions cell compares by reference (an Element cannot be value-compared).</summary>
    sealed record EagerRowProps(Track Track, int DisplayIndex, ColumnSet Set, TrackSize[] Tracks, float RowH, Action OnPlay,
                                EagerRowOptions Options)
    {
        public bool Equals(EagerRowProps? other)
            => other is not null && other.Track == Track && other.DisplayIndex == DisplayIndex && other.Set == Set
               && ReferenceEquals(other.Tracks, Tracks) && other.RowH.Equals(RowH)
               && other.Options.ShowTrackArtist == Options.ShowTrackArtist && other.Options.Zebra == Options.Zebra
               && ReferenceEquals(other.Options.ActionsCell, Options.ActionsCell);

        public override int GetHashCode() => HashCode.Combine(Track.Slot, DisplayIndex, Set, RowH);
    }

    sealed class EagerTrackRowHost : Component
    {
        EagerRowProps? _latest;
        int _likeSlot;
        bool _likeSaved;
        Action? _play, _like;
        Func<ContextMenuModel?>? _menu;

        public override Element Render()
        {
            var p = UsePropsOrDefault<EagerRowProps>();
            var hovered = UseSignal(false);
            var svc = UseContext(Overlay.Service);
            // The structure's eager-row templates (T8c): resolved once per host (the registry is ambient; a theme switch remounts the root).
            var registry = UseContext(global::FluentGpu.Theming.ViewRegistry.Context);
            if (p is null) return new BoxEl();
            var style = _style ??= global::Wavee.Theming.Contracts.ListStyleLookup.Of(registry, global::Wavee.Theming.Contracts.StyleKeys.EagerTrackRow);
            if (style is null) return new BoxEl();
            _latest = p;
            Action play = _play ??= PlayLatest;
            Action like = _like ??= LikeLatest;
            Func<ContextMenuModel?> menu = _menu ??= MenuLatest;

            // C1 (RC9 / D10): a whole-table `Tracks.Changed.Value` read here made EVERY mounted eager row re-render
            // on every landing batch (14-25 rows x ~10 frames per click). `stamp` still WAKES on every batch — it
            // reads `Tracks.Changed`, `Edges.TrackArtists.Changed` and `ScopeEpoch` — but a `Memo` only republishes
            // (and only then does THIS row's render effect re-run) when the computed VALUE actually differs, so a
            // batch that touched other rows costs this one nothing beyond the array reads below.
            // Placed AFTER `_latest = p` (not up with `UsePropsOrDefault`/`UseSignal`/`UseContext`) on purpose:
            // `Memo<T>`'s constructor calls `Recompute()` eagerly, so the FIRST time this hook is reached it must see
            // this host's real props, or the memo would permanently cache the slot-0 sentinel's version. The hook's
            // call SITE (file+line) stays fixed every render — only reachability is conditional on `p`, same as the
            // whole-table read it replaces — so relative order among the earlier, always-called hooks is unchanged.
            // The compute closure reads `_latest` (a field), never a value captured at construction — same as
            // `PlayLatest`/`LikeLatest`/`MenuLatest` below. This is not belt-and-braces: the Recents page's
            // `ClassicRecentsRowSlot` (a virtualized, RECYCLED row scope) calls `SingleRow`/`ChildRow` -> this `EagerRow` with NO
            // `Key` of its own, so when that scope recycles to a different track `EagerTrackRowHost` is REUSED in
            // place — its props are re-pushed (a new `EagerRowProps`, new `Track.Slot`), never remounted. A slot
            // captured once at this memo's construction would silently keep scoring the row the host was FIRST built
            // for, forever. Reading `_latest.Track.Slot` live is what keeps the memo correct across that recycle.
            //
            // Two sources, confirmed separately:
            //  - Title / duration / explicit / image live on the Tracks table itself: `Track.cs:591`'s
            //    `t.Applied(slot, identity, auth, ref t.IdentityAuthority)` bumps `Version[slot]` for the WHOLE
            //    Identity group (title, artist line, image, duration, flags incl. explicit) on every commit, so
            //    `Tracks.Version[slot]` alone covers every column this row paints from the Tracks table.
            //  - The artists line does NOT: `Grid`'s `artistInTitle`/`set.Artist` path calls `MetadataLine`/
            //    `ArtistLinks`, which read `t.ArtistSlots` -> `Edges.TrackArtists.Targets(slot)` directly — a CSR
            //    edge, not a Tracks column. `Track.Table.cs:1861-1867`'s bound row documents the exact same row
            //    needing this: a disk-restored row no longer claims the `Artists` bit, so the edge can land in a
            //    LATER drain than the row's own `Applied` call, and `Tracks.Version[slot]` does not move when it
            //    does. `Edges.TrackArtists.Version(slot)` is the edge's own per-parent version (`Edges.cs:279`,
            //    woken by its own `Changed`), so folding it in here keeps the credit line reactive without going
            //    back to a whole-table read.
            // Now-playing / liked state need NO addition: `StateOf` (used below) already carries its own narrow
            // subscriptions (`Playback.CurrentId` always; `IsPlaying`/`Buffering`/`Pending.Load` only on the
            // now-playing row) and `LikedNow` reads the Liked EDGE's own `Changed` — the heart
            // was already independent of this whole-table read, not something this change has to preserve.
            var stamp = UseComputed(() =>
            {
                _ = Entities.ScopeEpoch.Value;                            // a scope switch invalidates every slot
                _ = Entities.Current.Tracks.Changed.Value;
                _ = Entities.Current.Edges.TrackArtists.Changed.Value;
                var tracks = Entities.Current.Tracks;
                int slot = _latest?.Track.Slot ?? 0;
                uint trackVer = (uint)slot < (uint)tracks.Count ? tracks.Version[slot] : 0u;
                uint artistVer = Entities.Current.Edges.TrackArtists.Version(slot);
                return (trackVer, artistVer);
            });
            _ = stamp.Value;   // subscribes; republishes only when THIS row's stamp moved
            var t = p.Track;
            var set = p.Set;
            var st = StateOf(t);
            bool pop = LikeEdge(ref _likeSlot, ref _likeSaved, t, st.Saved);
            bool oddZebra = p.Options.Zebra && p.DisplayIndex % 2 != 0;

            var options = new GridOptions(
                ShowTrackArtist: p.Options.ShowTrackArtist, Art: RowMetrics.ThumbSize,
                OnPlay: play, OnLike: like, LikePop: pop,
                ActionsCell: p.Options.ActionsCell ?? (set.Actions ? MoreCell(true, set.Classic) : null),
                MoreEnabled: true, HoverPaused: hovered);
            // The title carries the full-text tooltip while it is cut (the plain run only; the marquee scrolls instead).
            Element title = style.Title(t, st.IsNow);
            var grid = style.Grid(t, p.DisplayIndex, in st, in set, p.Tracks, p.RowH, title, in options);

            // A row whose column set has no "…" lane still offers the menu: the standard "…" as a hover overlay at the
            // row's end over an opaque chip, reserving no width.
            bool overlayMore = !set.Actions && p.Options.ActionsCell is null && !Controls.IsNullOverlay(svc);

            var row = style.Skin(new global::Wavee.Theming.Contracts.EagerRowSkin
            {
                Grid = grid, Classic = set.Classic, OddZebra = oddZebra, RowHeight = p.RowH, OverlayMore = overlayMore,
                Draggable = _drag ??= Drag.Source(DragLatest), OnClick = play,
                // Real enter/exit edges: the PointerBit every HoverOpacity descendant inherits AND the equalizer pause.
                OnHoverMove = _ => { if (!hovered.Peek()) hovered.Value = true; },
                OnPointerExit = () => { if (hovered.Peek()) hovered.Value = false; },
            });
            return Controls.IsNullOverlay(svc) ? row : ContextSheets.Attach(row, svc, menu);
        }

        DragSource? _drag;
        global::Wavee.Theming.Contracts.EagerRowStyle? _style;

        /// <summary>The row's drag (selection-less: the row's own track), built cold at drag promotion from the NEWEST props.</summary>
        DragPayload DragLatest() => RowPayload(_latest!.Track, null);

        void PlayLatest()
        {
            if (_latest is not { } p) return;
            Invoke(p.Track, p.OnPlay);
        }

        void LikeLatest()
        {
            if (_latest is not { } p) return;
            if (p.Options.OnLike is { } like) like(); else ToggleLike(p.Track);
        }

        ContextMenuModel? MenuLatest()
        {
            if (_latest is not { } p) return null;
            return p.Options.Menu is { } menu ? menu() : Menu(new[] { p.Track }, s_defaultMenuOptions);
        }
    }

    /// <summary>Flip the signed-in account's like for one track — the optimistic edge write (<c>User.Like</c> /
    /// <c>Unlike</c>), which fills the heart in the same frame.</summary>
    static void ToggleLike(Track t)
    {
        var me = User.Me;
        if (me.Slot <= 0 || t.Slot <= 0) return;
        if (me.Likes(t)) me.Unlike(t); else me.Like(t);
    }

    // ══ 5. THE ROW DATA (the shared media surface's track adapter) ═══════════════════════════════════════════════════

    /// <summary>What a surface varies about a track row's <see cref="Controls.CardData"/> — the per-site choices and
    /// nothing the surface decides itself (the hover plate, the hand cursor, the play FAB and the "…" are the HOST's,
    /// derived from the data's presence and the shape). Hand it stable delegates: the host runs the NEWEST pushed handler
    /// through a trampoline and the data compares them by presence only, so a fresh closure never re-renders a row
    /// (the subtitle's artist links carry their own click closures, so a caller that re-renders does re-render its rows
    /// — the safe direction).
    /// <para><b>Construct with a named argument</b> (<c>new Track.RowDataOptions(OnClick: …)</c>): <c>default</c>
    /// zero-initialises a record struct and skips the defaults, so <see cref="ShowArtwork"/> would read false.</para>
    /// <para><see cref="OnClick"/> is the ROW's click — play-at-index in a list, open on a hit; null is a display-only row
    /// (no hand, no Button role, no tab stop; the FAB still plays). <see cref="OnPlay"/> is
    /// the cover FAB's "start this" leg; <see cref="Invoke(Track, Action)"/> wraps it, so on the deck row the FAB toggles
    /// pause instead, and null plays the one track. <see cref="ShowArtwork"/> false is the hidden-artwork look (ch 01 W28):
    /// the cover's square holds a play glyph — pair it with <see cref="TrackRowRules.ArtEdge"/> for the shape's art edge.
    /// <see cref="ShowExplicit"/> / <see cref="ShowVideo"/> put the "E" badge / film glyph ahead of the artists.
    /// <see cref="Draggable"/> hands the row a single-track drag (a row that owns another drag — the queue's
    /// reorder — says false); <see cref="QueueItemId"/> non-null marks it a queue row (the drag is then a reorder no
    /// playlist may deposit, and the menu addresses the queue entry). <see cref="ShowMenu"/> false keeps the right-click
    /// menu and drops the "…" button (the queue's rows and the rail's Next up: <see cref="QueueRowRules.ShowsMenuButton"/>,
    /// where the "…" overlay would cover the ✕). <see cref="MenuArgs"/> is the menu's own
    /// options (null = the defaults). <see cref="Trailing"/> is the right cluster (heart, duration, add) and
    /// <see cref="Selected"/> the opened/chosen skin.</para></summary>
    public readonly record struct RowDataOptions(
        Action? OnClick, Action? OnPlay = null, bool ShowArtwork = true, bool ShowExplicit = true, bool ShowVideo = false,
        bool Draggable = true, bool ShowMenu = true, MenuOptions? MenuArgs = null, ulong? QueueItemId = null,
        Element? Trailing = null, bool Selected = false)
    {
        /// <summary><c>new RowDataOptions()</c> means the defaults above (artwork, menu and drag on), not a zeroed struct.</summary>
        public RowDataOptions() : this(OnClick: null) { }
    }

    /// <summary>THE track adapter of the shared media surface: the <see cref="Controls.CardData"/> of one track row —
    /// title, the artists subtitle ([E] · [film] · artist links, each artist its own link), the cover (the track's own,
    /// else its album's), the play FAB's handler, a single-track drag source and the single-track menu. Feed it to
    /// <c>Controls.Surface(data, Shape.Row(edge))</c>. A track the table has no row for (slot ≤ 0) yields title-less data
    /// with no play, no drag and no menu.
    /// <para>It READS the track table, so the calling component subscribes to it (a per-card host used to): a title or
    /// credit line landing re-renders the caller, never the page behind it when the rows sit in a component of their
    /// own.</para></summary>
    public static Controls.CardData RowData(Track t, in RowDataOptions o)
    {
        _ = Entities.ScopeEpoch.Value;                    // FIRST (G-179): a scope switch re-points the table read below
        _ = Entities.Current.Tracks.Changed.Value;
        var track = t;
        int slot = t.Slot;
        var can = TrackRowRules.AffordancesOf(slot, o.Draggable);
        string uri = can.Play ? t.Uri.Text : "";
        string title = can.Play ? t.Title : "";

        Action start = o.OnPlay ?? (() => PlayTracks(new[] { track }));
        Action? play = can.Play ? () => Invoke(track, start) : null;

        ulong? queueItem = o.QueueItemId;
        DragSource? drag = can.Drag ? Drag.Source(() => RowPayload(track, queueItem)) : null;

        var args = o.MenuArgs ?? s_defaultMenuOptions;
        args = args with { QueueItemId = TrackRowRules.MenuQueueItem(queueItem, args.QueueItemId) };
        Func<ContextMenuModel?>? menu = can.Menu ? () => Menu(new[] { track }, in args) : null;

        var parts = TrackRowRules.SubtitleOf(o.ShowExplicit, can.Play && t.IsExplicit, o.ShowVideo, can.Play && t.HasVideo,
                                             can.Play && t.ArtistSlots.Length > 0);
        string? cover = o.ShowArtwork && can.Play ? RowArtUrl(t) : null;
        return new Controls.CardData(uri, title, RowSubtitle(track, in parts), cover, o.OnClick, play,
                                     Drag: drag, ShowMenu: o.ShowMenu, CoverOverride: o.ShowArtwork ? null : NoArtworkTile())
        {
            Menu = menu, Trailing = o.Trailing, Selected = o.Selected,
        };
    }

    /// <summary>The artists subtitle: [E badge] · [film] · the artist links, "·" between the parts that exist. Null when
    /// there is nothing to say.</summary>
    static Element? RowSubtitle(Track t, in TrackRowRules.SubtitleParts p)
    {
        if (p.Children == 0) return null;
        var kids = new Element[p.Children];
        int k = 0;
        if (p.Badge) kids[k++] = Controls.ExplicitBadge(14f);
        if (p.Film)
        {
            if (k > 0) kids[k++] = Middot();
            kids[k++] = Icon(Icons.Movie, 13f, Tok.TextTertiary);
        }
        if (p.Artists)
        {
            if (k > 0) kids[k++] = Middot();
            kids[k++] = ArtistLinks(t, 12f, 16f, Tok.TextSecondary);
        }
        return new BoxEl { Direction = 0, Gap = Spacing.XS, AlignItems = FlexAlign.Center, MinWidth = 0f, Children = kids };
    }

    static Element Middot() => Caption("·") with { Color = Tok.TextTertiary };

    /// <summary>Hidden artwork (ch 01 W28): a play glyph where the cover was — the art WAS the play affordance, and the
    /// row (and the FAB the surface raises over this square while it is hot) is what plays now.</summary>
    static Element NoArtworkTile() => new BoxEl
    {
        Width = TrackRowRules.NoArtworkEdge, Height = TrackRowRules.NoArtworkEdge, Shrink = 0f,
        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Children = [Icon(Icons.Play, 14f, Tok.TextPrimary)],
    };

    /// <summary>The drag a track row carries, built cold at drag promotion: the one-track snapshot every destination acts
    /// on, its kind the row's own (an episode rides a track row), and — for a queue row — the queue item that marks it a
    /// reorder no playlist, tab or player-bar surface may read as a deposit.</summary>
    static DragPayload RowPayload(Track t, ulong? queueItemId)
    {
        string uri = t.Uri.Text;
        return new DragPayload(Drag.PlayableKind(uri), uri, uri, t.Title, new EntityRef(EntityKind.Track, t.Slot),
                               Tracks: [t], SourceQueueItemId: queueItemId);
    }

    /// <summary>The recommendation row's "add to this playlist" button: a bordered 28 circle, Add 15 primary, Emphatic
    /// hover/press, its own affordance (never a drag handle). A <see cref="RowDataOptions.Trailing"/> cluster part.</summary>
    public static Element AddButton(Action onAdd) => new BoxEl
    {
        Width = RowMoreBox, Height = RowMoreBox, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Corners = Radii.Circle(RowMoreBox), BorderWidth = 1f, BorderColor = Tok.StrokeControlDefault,
        HoverScale = R.Motion.ScaleEmphatic.Hover, PressScale = R.Motion.ScaleEmphatic.Press,
        Cursor = CursorId.Hand, OnClick = onAdd,
        Role = AutomationRole.Button, BlocksDragArm = true,
        Children = [Icon(Icons.Add, 15f, Tok.TextPrimary)],
    }.Interactive(Interaction.Subtle);

    // ══ 6. THE "+N" ARTIST CHIP ══════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The artist-overflow chip: a tertiary "+N" (N = the artists NOT already shown inline) that opens a menu
    /// flyout of every credited artist, each navigating to its own page, BottomEdgeAlignedLeft with light dismiss; a
    /// second click closes it (ch 01 §6.1). Empty when nothing overflows.</summary>
    public static Element ArtistMoreButton(ReadOnlySpan<int> artistSlots, int shown)
        => Embed.Comp(new ArtistOverflowProps(artistSlots.ToArray(), shown), static () => new ArtistOverflowHost());

    sealed record ArtistOverflowProps(int[] Slots, int Shown)
    {
        public bool Equals(ArtistOverflowProps? other)
            => other is not null && other.Shown == Shown && ((ReadOnlySpan<int>)Slots).SequenceEqual(other.Slots);

        public override int GetHashCode() => HashCode.Combine(Slots.Length, Shown);
    }

    sealed class ArtistOverflowHost : Component
    {
        ArtistOverflowProps? _latest;

        public override Element Render()
        {
            var p = UsePropsOrDefault<ArtistOverflowProps>();
            var anchor = UseRef(NodeHandle.Null);
            var handle = UseRef<OverlayHandle?>(null);
            var svc = UseContext(Overlay.Service);
            if (p is null) return new BoxEl();
            _latest = p;
            int extra = Math.Max(0, p.Slots.Length - p.Shown);
            if (extra == 0) return new BoxEl();

            void Toggle()
            {
                if (Controls.IsNullOverlay(svc) || _latest is not { } latest) return;
                if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
                var slots = latest.Slots;
                var items = new MenuFlyoutItem[slots.Length];
                for (int i = 0; i < slots.Length; i++)
                {
                    var artist = new Artist(slots[i]);   // fresh per row: each navigates to its OWN artist
                    items[i] = new MenuFlyoutItem(artist.Name, default, artist.Uri.IsValid, () => GoToArtist(artist));
                }
                var opened = svc.Open(
                    () => anchor.Value,
                    () => MenuFlyout.Create(items, () => handle.Value?.Close(), size: OverlayFit.MenuSize),
                    FlyoutPlacement.BottomEdgeAlignedLeft,
                    new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = false });
                handle.Value = opened;
                opened.ClosedAction = () => handle.Value = null;
            }

            return new BoxEl
            {
                Shrink = 0f, Padding = new Edges4(Spacing.XXS, 0f, Spacing.XXS, 0f),
                OnRealized = h => anchor.Value = h,
                Role = AutomationRole.Button, Cursor = CursorId.Hand, BlocksDragArm = true,
                OnClick = Toggle,
                Children = [Caption(Loc.Format(LocMoreArtists, ("count", extra))) with { Weight = 600, Color = Tok.TextTertiary }],
            };
        }
    }
}

/// <summary>The track row adapter's pure decisions (<see cref="Track.RowData"/>), engine-free so a fact pins each one:
/// which affordances a row has, the art edge, which parts the artists subtitle holds, and whose queue item the menu
/// addresses. The adapter only feeds them.</summary>
public static class TrackRowRules
{
    /// <summary>The play-glyph square that stands where hidden artwork was (ch 01 W28 — it was a 32 button).</summary>
    public const float NoArtworkEdge = 32f;

    /// <summary>Which affordances a row has. A track the table has no row for (slot ≤ 0) has none; a real one plays and
    /// has a menu, and drags only when its surface wants it (a row that owns another drag — the queue's reorder — does
    /// not).</summary>
    public readonly record struct Affordances(bool Play, bool Menu, bool Drag);

    public static Affordances AffordancesOf(int slot, bool draggable)
    {
        bool real = slot > 0;
        return new Affordances(Play: real, Menu: real, Drag: real && draggable);
    }

    /// <summary>The art edge of a row's shape: the art's own while artwork shows, else the play-glyph square.</summary>
    public static float ArtEdge(float art, bool showArtwork) => showArtwork ? art : NoArtworkEdge;

    /// <summary>The parts of the artists subtitle — the "E" badge, the film glyph, the artist links — and how many
    /// children the row holds with a "·" between each pair.</summary>
    public readonly record struct SubtitleParts(bool Badge, bool Film, bool Artists)
    {
        public int Parts => (Badge ? 1 : 0) + (Film ? 1 : 0) + (Artists ? 1 : 0);
        public int Children => Parts == 0 ? 0 : 2 * Parts - 1;
    }

    /// <summary>A badge only when the surface wants it AND the track is explicit; a film glyph only when the surface wants
    /// it AND the track has a video; the artists whenever the track credits any.</summary>
    public static SubtitleParts SubtitleOf(bool showExplicit, bool isExplicit, bool showVideo, bool hasVideo, bool hasArtists)
        => new(showExplicit && isExplicit, showVideo && hasVideo, hasArtists);

    /// <summary>The queue item the track menu addresses (<c>Track.MenuOptions.QueueItemId</c>): a queue row's own non-zero
    /// item wins, else whatever the caller's menu options already say — a queue row with no item yet (0) is not a
    /// removable entry.</summary>
    public static long MenuQueueItem(ulong? queueItemId, long argsQueueItemId)
        => queueItemId is { } id && id != 0 ? (long)id : argsQueueItemId;
}
