// ── Entities/User.UI.cs ────────────────────────────────────────────────────────────────────────────────────────────
// the library's CORE facts and the shared small pieces its pictures read: the persisted view / sort codes, the A-Z sticky letter and jump strip (the profile
// lists share them until T10c2), the navigator row's extents, bound subtitle texts, art url and drag payloads, the drag a search hit carries, and the library
// mutation seam (`LibrarySeam`) the shared save/follow affordances read
//
// Role: CORE
// Owner: O
// Wave: 5 (the rails/letters: the 2026-09-17 library rework, waves L1-L2)
// Spec: ch 15 §1-§6, §9 (0.2.9 LibrarySortView.cs, LibraryPage.cs row/card/grip/search-row builders); contract §2.6;
//       library-rework-implementation.md §5.2 (the rails, the letters, the strip, the two subtitle caches)
//
// T10c1 (themes plan 6.1): the PICTURES that lived here are Classic's (`Themes\Classic\Structure\Views\Pages\ClassicLibraryPage*.cs`,
// `ClassicLibraryRails.cs`): the word rails and the view toggle with its trimmed panel, the letter header, the crumb bar, the column grip, the bound
// navigator rows and cards with their grid selection chrome, and the search-row shapes. What stays is what a decision or another page reads.
//
// DELETED IN WAVE L2 (no legacy paths — their consumers went with them): `SortViewPill` / `SortPanel` and their two
// hosts (the word rail replaced the sort pill outright), `DiscoRow` / `DiscoCard` / `DiscoSubtitle` / `AlbumCode` (the
// discography grid pane is gone; `Artist.Reader` stacks art + tracks blocks instead) and `GoToArtistLink` (the reader's
// doorway is a `Controls.Named` icon button). `SortLabelKey` / `SortLabel` / `ViewGlyph` / `IsGridView` /
// `IsCompactView` STAY: the sidebar's Library V3 shares the codes and the view panel still draws the view glyphs.
//
// MOVED IN PODCAST WAVE P1 (podcast-show-rework-implementation.md §5.5): the rail itself — `RailBar`, `RailWord`,
// `RailInk`, `WordRailHost` and the RailWord* metrics — is `Controls.Words` now, shared with the podcast reader.
//
// ZERO ALLOCATION ON A SCROLL FRAME. Every navigator row is an `ItemsView.CreateBound` slot: the template runs ONCE per
// slot and every per-item value is a bind over `BoundItemScope<LibraryNavItem>` (a slot + its row version), so a
// recycle rewrites the slot's item signal and re-fires only those binds — no `List<Element>` per row, no remount.
// Handlers resolve the CURRENT item at invocation (`Peek`), never a mount-time capture.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>One navigator / discography slot: the row's kind and slot plus its row VERSION, so a fact landing on the row
/// changes the item (and re-fires the slot's binds) even though the slot did not move.
/// <para><b>The version is the row's whole re-render trigger.</b> The bound item signal is equality-gated (engine
/// <c>BoundItemsSource.BindItem</c>), so a bind only re-fires when this record CHANGES: whatever a row's text or art
/// reads has to be inside <see cref="Version"/>. <see cref="Of"/> is the plain case — the entity's own row version —
/// and a caller whose row displays a fact off ANOTHER table folds that in itself (the navigator's
/// <c>LibraryPage.FillRowVersions</c>: the album row's billed-artist name, the artist row's release/song counts). The
/// list must NOT be remounted to refresh a row; that was the 0.2.10 remount storm.</para></summary>
public readonly record struct LibraryNavItem(EntityKind Kind, int Slot, uint Version)
{
    public static LibraryNavItem Of(EntityKind kind, int slot)
    {
        Table? table = Entities.TableFor(kind);
        return new(kind, slot, table is null || slot <= Table.None || slot >= table.Count ? 0u : table.Version[slot]);
    }
}

public readonly partial struct User
{
    // ══ 1. THE PERSISTED CODES (shared with the sidebar's Library V3 — never renumber) ═══════════════════════════════

    /// <summary>Sort code → its Title-Case loc KEY. Codes 0..4 are persisted (<c>library.&lt;kind&gt;.sort</c>); unknown →
    /// Recents. The library's own rail reads the lowercase <c>library.rail.*</c> keys through
    /// <see cref="LibraryWordRail.WordKey"/> — these labels are the SIDEBAR's (Library V3's pills share the codes).</summary>
    public static string SortLabelKey(int code) => code switch
    {
        1 => Strings.Library.Sort.RecentlyAdded,
        2 => Strings.Library.Sort.Alphabetical,
        3 => Strings.Library.Sort.Creator,
        4 => Strings.Library.Sort.ReleaseDate,
        _ => Strings.Library.Sort.Recents,
    };

    public static string SortLabel(int code) => Loc.Get(SortLabelKey(code));

    /// <summary>View code (0 CompactList · 1 List · 2 CompactGrid · 3 Grid) → its glyph (the view toggle's and the view
    /// panel's four cells; the sidebar's own switch reads the same codes).</summary>
    public static string ViewGlyph(int view) => view >= 2 ? Icons.ViewGrid : Icons.ViewList;

    public static bool IsGridView(int view) => view >= 2;
    public static bool IsCompactView(int view) => view is 0 or 2;

    // ══ 2. THE LETTERS' SHARED INK FADE ══════════════════════════════════════════════════════════════════════════════
    //
    // The word rails (the navigator's sort rail, the reader's scope and sort rails) and the view toggle with its trimmed panel are Classic's
    // (`Themes\Classic\Structure\Views\Pages\ClassicLibraryRails.cs`, `ClassicLibraryPage.Toolbar.cs`); the rail itself is `Controls.Words`.

    /// <summary>The ink/underline fade the A–Z strip shares with the rail words: the 83-ms WinUI BrushTransition.</summary>
    static readonly FluentGpu.Animation.MotionTokenDef RailInkFade = FluentGpu.Animation.MotionTok.ControlFaster;

    // ══ 3. THE LETTERS: THE HEADER ITEM, THE STICKY OVERLAY, THE A–Z STRIP (W2) ═══════════════════════════════════════

    static readonly string[] s_letters = ["A", "B", "C", "D", "E", "F", "G", "H", "I", "J", "K", "L", "M", "N", "O", "P", "Q", "R", "S", "T", "U", "V", "W", "X", "Y", "Z"];

    /// <summary>Letter index (0 = "#", 1..26 = A..Z — <see cref="LibraryLetters.Of"/>'s space) → its glyph.</summary>
    public static string LetterText(int letter) => letter <= 0 || letter > s_letters.Length ? "#" : s_letters[letter - 1];

    /// <summary>The pinned twin of the navigator's inline letter header (`ClassicLibraryPage.LetterHeader`): a ZStack overlay over the list, pushed by the distance the
    /// page derives from the scroll geometry (Recents' <c>StickyDayHeader</c> idiom — a bound transform, so a scroll frame
    /// never re-renders it). Always mounted; before any letter is under the top it is simply transparent, which is cheaper
    /// than collapsing it (a presence flip would relayout the overlay).</summary>
    public static Element StickyLetter(IReadSignal<int> letter, IReadSignal<float> push) => new BoxEl
    {
        // "nav:sticky" (§3.3 / A1): a stable key on the ROOT so the navigator host can place this overlay as a KEYED
        // sibling of the keyed list — a key on a component's single-child slot is inert (RC1), but this element is
        // returned straight into a `Children` array, where `ReconcileChildren` honors it.
        Key = "nav:sticky",
        Height = LibraryLetters.HeaderExtent, Direction = 0, AlignItems = FlexAlign.End, HitTestVisible = false,
        AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start,
        Padding = new Edges4(Spacing.S, 0f, Spacing.S, Spacing.XS),
        Opacity = Prop.Of(() => letter.Value >= 0 ? 1f : 0f),
        Transform = Prop.Of(() => Affine2D.Translation(0f, push.Value)),
        Children = [LetterPlate(Prop.Of(() => LetterText(letter.Value)))],
    };

    internal static Element LetterPlate(Prop<string> text) => new BoxEl
    {
        Padding = new Edges4(6f, 1f, 6f, 1f), Corners = CornerRadius4.All(4f), Fill = Tok.FillCardDefault,
        BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
        Children = [new TextEl(text) { Size = 12f, LineHeight = 16f, Weight = 600, CharSpacing = 60f, Color = Tok.TextTertiary }],
    };

    /// <summary>The A–Z strip on the navigator's right edge: 27 rows of 18 × 13. A present letter is full ink and the one
    /// under the viewport top is accent/700 (two stacked runs again — <c>Weight</c> is not bindable); an absent letter sits
    /// at 30 % and its tap NO-OPS, because <paramref name="jump"/> resolves through <c>LibraryLetters.HeaderFlat</c> and
    /// finds nothing. <paramref name="present"/> is the letters' bitmask, <paramref name="current"/> the sticky letter.</summary>
    public static Element JumpStrip(IReadSignal<uint> present, IReadSignal<int> current, Action<int> jump)
    {
        var rows = new Element[LibraryLetters.Count];
        for (int l = 0; l < rows.Length; l++)
        {
            int letter = l;
            Func<bool> here = () => (present.Value & (1u << letter)) != 0;
            Func<bool> now = () => current.Value == letter;
            string text = LetterText(letter);
            rows[l] = new BoxEl
            {
                Width = 18f, Height = 13f, ZStack = true, Corners = CornerRadius4.All(3f),
                // 27 letters must not become 27 tab stops: the strip is a pointer affordance beside a typeahead list.
                Role = AutomationRole.Button, TabStop = false, Cursor = CursorId.Hand, HoverFill = Tok.FillSubtleSecondary,
                Opacity = Prop.Of(() => here() ? 1f : 0.3f), Transition = RailInkFade, OnClick = () => jump(letter),
                Children =
                [
                    StripInk(text, 400, Prop.Of(() => now() ? ColorF.Transparent : Tok.TextTertiary)),
                    StripInk(text, 700, Prop.Of(() => now() ? Tok.AccentTextPrimary : ColorF.Transparent)),
                ],
            };
        }
        return new BoxEl
        {
            // "nav:strip" (§3.3 / A1): same reasoning as StickyLetter's "nav:sticky" — a stable key on the ROOT so
            // the navigator host can place this strip as a KEYED sibling, not lose it to an inert single-child key.
            Key = "nav:strip",
            Direction = 1, Width = 18f, Shrink = 0f, Justify = FlexJustify.Center,
            Padding = new Edges4(0f, Spacing.XS, 0f, Spacing.XS), Children = rows,
        };
    }

    static TextEl StripInk(string text, ushort weight, Prop<ColorF> ink) => R.Type.MicroMeta(text) with
    {
        Weight = weight, Color = ink, BrushTransitionMs = Design.Motion.Fast,
        AlignSelf = FlexAlign.Center, JustifySelf = FlexAlign.Center,
    };

    // ══ 4. THE NAVIGATOR ROW'S FACTS (W1-W5, W19, W22) ═══════════════════════════════════════════════════════════════

    /// <summary>The navigator LIST row's PLATE height (W1): the selected backplate itself — 56, 40 compact — which is the
    /// prototype's row rhythm and the number Classic's navigator row (`ClassicLibraryPage.NavRow`) sets as its explicit height.</summary>
    public const float NavRowPlate = 56f, NavRowCompactPlate = 40f;

    /// <summary>The margin the engine's bound list chrome puts around every plate (<c>SelectorVisualsBound</c>'s
    /// <c>s_backplateMargin {4,2,4,2}</c>) — the vertical half, because the main axis is the only one the list's extents
    /// add up. It is part of the row and NOT of the plate: the 2-DIP gap between plates is the ListView language.</summary>
    public const float NavRowMarginY = 2f;

    /// <summary>The navigator LIST row's OUTER extents — the analytic seed the page hands <c>RepeatLayout.Extents</c>,
    /// which MUST agree with what the row MEASURES: the measured list corrects a row to its measurement, so a seed that
    /// counted the plate and forgot its 2+2 margin was 4 DIP short PER ROW, and the pinned letter, the jump strip, the
    /// scrollbar thumb and every bring-into-view drifted by a whole row every fourteen. The plate + its margin is the
    /// one number both halves read, and <see cref="LibraryLetters"/>' offsets are summed at it.</summary>
    public const float NavRowExtent = NavRowPlate + 2f * NavRowMarginY,
                       NavRowCompactExtent = NavRowCompactPlate + 2f * NavRowMarginY;

    static readonly FormatCache<int> s_artistCounts = new();
    static readonly FormatCache<long> s_albumSubtitles = new();

    /// <summary>The navigator row's second line as a BOUND text. An ARTIST row says what the library HOLDS of them — "3 albums · 34 songs"
    /// (ch 15 §7 gap 10: it used to say the literal word "Artist") — an ALBUM row "artist · year", and a SHOW row keeps its
    /// publisher. Every one of them is a bind through a HOISTED cache, so a recycle looks a string up and never formats. The line's type is
    /// Classic's (`ClassicLibraryPage.NavSubtitle`).</summary>
    internal static Prop<string> NavSubtitleText(in BoundItemScope<LibraryNavItem> scope, EntityKind kind) => kind switch
    {
        EntityKind.Artist => scope.Text(static it => ArtistCountCode(it.Slot), s_artistCounts, static code => ArtistCountText(code)),
        EntityKind.Album => scope.Text(static it => AlbumSubtitleKey(it.Slot), s_albumSubtitles, static key => AlbumSubtitleText(key)),
        _ => scope.Text(static it => LibraryRows.SubtitleOf(it.Kind, it.Slot)),
    };

    // The counts packed into one int key (albums << 12 | songs, each clamped to 12 bits): two numbers, so the cache holds
    // one string per distinct pair rather than one per artist.
    static int ArtistCountCode(int slot)
        => (Math.Min(LibraryAlbumCountOf(slot), 4095) << 12) | Math.Min(LibrarySongCountOf(slot), 4095);

    static string ArtistCountText(int code) => ArtistCountLine(code >> 12, code & 4095);

    /// <summary>"3 albums · 34 songs" — and, for an artist whose whole presence in your library is liked TRACKS (§11's
    /// group 2: nothing of theirs saved, one liked feature), "12 songs" on its own. The bare word "Artist" survives for
    /// 0/0 ONLY, which is a followed artist nothing of whose library has answered yet — before the rework it was what an
    /// artist with fifty liked songs and no saved album read as. (<c>nSongs</c> carries its own " · " because it is the
    /// continuation of the albums clause; <c>nSongsOnly</c> is the standalone line and does not.)
    /// <para>THREE branches, no counting. <c>nAlbums</c> / <c>nSongs</c> / <c>nSongsOnly</c> are ICU plurals and pick
    /// their own singular, so the <c>== 1</c> arms that used to reach for <c>oneAlbum</c> / <c>oneSong</c> are gone. They
    /// were also wrong the moment only ONE half was singular: an artist with one album and one song composed the
    /// hand-picked "1 album" with a <c>nSongs</c> that had no singular branch yet and read "1 album · 1 songs". A
    /// plural rule belongs to the message, never to the caller — and a language whose "one" category is not the number
    /// 1 (Russian's 21, Welsh's 2) can only be spelled inside the ICU entry.</para>
    /// <para>Public, not private: this assembly has no <c>InternalsVisibleTo</c> (see <c>Playlist.UI.cs</c>), and the
    /// rule is pinned by a fact rather than by reading this source.</para></summary>
    public static string ArtistCountLine(int albums, int songs)
        => albums <= 0
            ? (songs <= 0 ? Loc.Get(Strings.Search.TypeArtist) : Strings.Library.NSongsOnly(songs))
            : Strings.Library.NAlbums(albums) + (songs > 0 ? Strings.Library.NSongs(songs) : "");

    // "artist · year" keyed by (the billed artist's NAME id, the year): the album slot would have done for the CACHE, but
    // keying on the name's id means a name that lands after the year re-keys instead of serving the stale line forever.
    static long AlbumSubtitleKey(int slot)
    {
        var scope = Entities.Current;
        var billed = scope.Edges.AlbumArtists.Targets(slot);
        int name = billed.Length > 0 && billed[0] > Table.None ? scope.Artists.Name[billed[0]].Value : 0;
        int year = LibraryRows.YearOf(EntityKind.Album, slot);
        return ((long)(uint)name << 16) | (uint)Math.Clamp(year, 0, 0xFFFF);
    }

    static string AlbumSubtitleText(long key)
    {
        string artist = Entities.Strings.Resolve(new StringId((int)(uint)(key >> 16)));
        int year = (int)(key & 0xFFFF);
        if (year <= 0) return artist;
        return artist.Length > 0 ? artist + " · " + FormatCache.Int(year) : FormatCache.Int(year);
    }

    internal static string? ArtOf(in LibraryNavItem it) => Controls.ArtUrl(LibraryRows.ImageOf(it.Kind, it.Slot));

    /// <summary>A library row is a drag SOURCE only, click-primary (×2 drag box); the payload is built at promotion from
    /// the slot's CURRENT item. Tracks resolve lazily through the sidebar's library seam.</summary>
    internal static DragSource DragOf(in BoundItemScope<LibraryNavItem> scope)
    {
        var item = scope.Item;
        return Drag.Source(() => PayloadOf(item.Peek()), clickPrimary: true);
    }

    internal static DragPayload PayloadOf(in LibraryNavItem it)
    {
        var id = LibraryRows.IdOf(it.Kind, it.Slot);
        string uri = id.Text;
        var kind = Drag.KindOf(it.Kind);
        string prefix = it.Kind switch
        {
            EntityKind.Artist => SidebarPinId.ArtistPrefix,
            EntityKind.Show => SidebarPinId.ShowPrefix,
            _ => SidebarPinId.AlbumPrefix,
        };
        Func<CancellationToken, Task<Track[]>>? resolver = null;
        if (uri.Length > 0 && kind is DragKind.Album or DragKind.Show && Sidebar.LibraryWrites?.ResolveTracks is { } resolve)
            resolver = ct => resolve(uri, ct);
        return new DragPayload(kind, prefix + uri, uri, LibraryRows.TitleOf(it.Kind, it.Slot), new EntityRef(it.Kind, it.Slot),
                               TrackResolver: resolver, ArtUrl: ArtOf(it));
    }

    // ══ 5. THE DRAG A SEARCH HIT CARRIES ═════════════════════════════════════════════════════════════════════════════
    //
    // The search rows themselves (the artist / album hit on the shared media surface, the track hit, the facet header, the zero-hit message) and the
    // navigator's grid selection chrome are Classic's (`ClassicLibraryPage.Search.cs`, `.Nav.cs`).

    /// <summary>An artist / album hit drags exactly what its navigator row does (<see cref="PayloadOf"/>: the entity, with
    /// an album's tracks resolved lazily) — click-primary, because selecting is the common intent on a hit.</summary>
    internal static DragSource HitDrag(EntityKind kind, int slot)
        => Drag.Source(() => PayloadOf(new LibraryNavItem(kind, slot, 0u)), clickPrimary: true);

    // ══ 6. THE LIBRARY SEAM (contract §2.6 → Controls.Library) ═══════════════════════════════════════════════════════

    /// <summary>The save/follow affordances' one seam. <c>IsSaved</c> SUBSCRIBES (the scope epoch + the relation's
    /// <c>Changed</c>) so a heart re-skins the frame the optimistic edge lands; a pending remove reads unsaved. Tracks,
    /// albums, artists and shows go through <see cref="Add"/>/<see cref="Remove"/>; a playlist follows through the
    /// rootlist (<c>Spotify.Library.FollowPlaylist</c>); a user follows through <c>Spotify.Library.FollowUser</c> (read off
    /// <c>User.IsFollowedByViewer</c>, subscribed through the users table's <c>Changed</c>); the liked collection reads
    /// saved and is not toggled.</summary>
    public static Controls.LibrarySeam LibrarySeam { get; } = new(IsSavedUri, ToggleSavedUri);

    static bool IsSavedUri(string uri)
    {
        _ = Entities.ScopeEpoch.Value;
        var scope = Entities.Current;
        if (scope is null || string.IsNullOrEmpty(uri) || scope.MeSlot <= Table.None) return false;
        if (EntityUri.IsLikedCollection(uri)) return true;
        var id = EntityUri.Parse(uri).Id;
        var table = RelationOf(id.Kind, scope);
        if (id.Kind == EntityKind.Playlist)
        {
            _ = scope.Edges.Rootlist.Changed.Value;
            return scope.Playlists.TryGetSlot(id, out int p) && scope.Edges.Rootlist.Contains(scope.MeSlot, p);
        }
        if (id.Kind == EntityKind.User)
        {
            _ = scope.Users.Changed.Value;
            return scope.Users.TryGetSlot(id, out int u) && new User(u).IsFollowedByViewer;
        }
        if (table is null || Entities.TableFor(id.Kind) is not { } rows) return false;
        _ = table.Changed.Value;
        return rows.TryGetSlot(id, out int slot) && table.Contains(scope.MeSlot, slot)
               && table.PendingOf(scope.MeSlot, slot) != EdgePending.Remove;
    }

    static void ToggleSavedUri(string uri, string? name)
    {
        var scope = Entities.Current;
        if (scope is null || string.IsNullOrEmpty(uri) || EntityUri.IsLikedCollection(uri)) return;
        var me = Me;
        if (!me.IsValid) return;
        var id = EntityUri.Parse(uri).Id;
        if (id.Kind == EntityKind.Playlist)
        {
            bool following = scope.Playlists.TryGetSlot(id, out int p) && scope.Edges.Rootlist.Contains(me.Slot, p);
            Spotify.Library.FollowPlaylist(uri, !following);
            return;
        }
        if (id.Kind == EntityKind.User)
        {
            bool following = scope.Users.TryGetSlot(id, out int u) && new User(u).IsFollowedByViewer;
            Spotify.Library.FollowUser(uri, !following);                    // a no-op on your own profile
            return;
        }
        if (Entities.TableFor(id.Kind) is not { } rows) return;
        LibraryEdgeKind kind = id.Kind switch
        {
            EntityKind.Album => LibraryEdgeKind.SavedAlbums,
            EntityKind.Artist => LibraryEdgeKind.FollowedArtists,
            EntityKind.Show => LibraryEdgeKind.SavedShows,
            EntityKind.Track => LibraryEdgeKind.Liked,
            _ => LibraryEdgeKind.Pins,
        };
        if (kind == LibraryEdgeKind.Pins) return;   // an episode / user / concert has no library relation
        int slot = rows.Slot(id);
        if (me.Has(kind, slot) && me.PendingOf(kind, slot) != EdgePending.Remove) me.Remove(kind, slot, id);
        else me.Add(kind, slot, id);
        Entities.Publish();
    }

    static EdgeTable<LibraryEdge>? RelationOf(EntityKind kind, Scope scope) => kind switch
    {
        EntityKind.Track => scope.Edges.Liked,
        EntityKind.Album => scope.Edges.SavedAlbums,
        EntityKind.Artist => scope.Edges.FollowedArtists,
        EntityKind.Show => scope.Edges.SavedShows,
        _ => null,
    };
}
