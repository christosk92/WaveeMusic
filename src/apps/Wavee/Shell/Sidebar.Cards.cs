// ── Shell/Sidebar.Cards.cs ─────────────────────────────────────────────────────────────────────────────────────────
// the sidebar's projection → media-surface adapters: a hero card, a grid tile and a collapsed-rail tile are each ONE
// `Controls.Surface` fed by `SidebarCards`, and every decision the adapters make is a function in `SidebarCardRules`
//
// Role: UI
// Owner: J
// Wave: 3 (shared media surface)
// Budget: 350 lines
// Spec: docs/plans/wavee/shared-media-surface-implementation.md §2.7, §4 wave 3
//
// ── WHY ADAPTERS, AND WHY THE SLOT CLASS STAYS HOOK-FREE ─────────────────────────────────────────────────────────────
//
// `PaneSlot` (Sidebar.UI.Slot.cs) is a hook-free class: every builder is a plain method, so the hook order is identical
// across every recycle. A `Controls.Surface` is a hook-OWNING CHILD component, which that class explicitly sanctions, and
// it is legal here because it is PROPS-DRIVEN: `Embed.Comp(props, factory)` re-pushes the new `CardData` on every rebind of
// the recycling slot instead of freezing a constructor argument at mount. The surface is therefore built with a plain
// call, its handlers are trampolines into the newest push, and the slot keeps its `ContentType` recycle pools.
//
// The slot is NOT a bound slot root that owns invoke and focus (the pane's rows own their own click), so it provides no
// `ItemsView.SlotRow` and the surface runs in FREE mode: it owns the click, the tab stop, the Button role and the hand
// cursor itself — ONE click owner and ONE tab stop per card.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;

namespace Wavee;

/// <summary>The three sidebar surfaces that are media cards: the EntityEmbed spotlight card, a Grid-presentation cell and
/// a collapsed-rail tile. (The rows, the folder tile and the glyph tiles are the sidebar's own grammar, not media.)</summary>
public enum SidebarCardSurface : byte { Hero, Tile, Rail }

/// <summary>What activating a projected entry does: a track PLAYS (it has no detail route), everything with a route
/// NAVIGATES, a folder (it expands in place) and a route-less entry do nothing — their surface carries NO click, so it is
/// display-only (no hand, no Button role, no tab stop).</summary>
public enum SidebarCardActivation : byte { None, Play, Navigate }

/// <summary>What a card is as a drop destination: nothing, a track DEPOSIT (an editable playlist), or a REFUSAL that
/// says why (a playlist the user cannot edit — the expanded layouts name the reason, the 56-DIP rail stays transparent).</summary>
public enum SidebarCardDrop : byte { None, Deposit, Refuse }

/// <summary>The pure facts behind the sidebar's media cards, per entry kind. No engine type is touched, so each answer is
/// pinned by a fact (<c>SidebarCardsTests</c>) instead of a source read. Public only because this assembly has no
/// <c>InternalsVisibleTo</c>.</summary>
public static class SidebarCardRules
{
    /// <summary>Selected = the entry's nav route IS the live route (<see cref="SidebarRowResolve.EntrySelects"/> — the ONE
    /// rule the row sweep also uses). A folder and a track have no route, so they are never selected.</summary>
    public static bool Selected(in SidebarLibraryEntry entry, string route) => SidebarRowResolve.EntrySelects(in entry, route);

    public static SidebarCardActivation Activation(in SidebarLibraryEntry entry)
        => entry.IsTrack ? SidebarCardActivation.Play
         : entry.RouteKey is { Length: > 0 } ? SidebarCardActivation.Navigate
         : SidebarCardActivation.None;

    /// <summary>A play affordance exists on the hero (when its section's <c>PlayButton</c> option is on) and on the grid tile
    /// for an entry that has a playable context; never on the rail tile (no room — the tile is the target, the context menu
    /// the verbs). <c>IsPlayable</c> is Playlist / Album / Show / Track: an artist has no single context to start.</summary>
    public static bool HasPlay(SidebarCardSurface surface, in SidebarLibraryEntry entry, bool playButton)
        => surface != SidebarCardSurface.Rail
           && entry.IsPlayable && !string.IsNullOrEmpty(entry.Uri)
           && (surface != SidebarCardSurface.Hero || playButton);

    /// <summary>A playlist is the one drop destination a card can be. An editable one takes a track deposit; a read-only one
    /// REFUSES with a reason on the expanded surfaces — exactly what <c>EntityRow</c> does — but stays transparent on the rail,
    /// where a refusal sentence has no room and the pre-existing tile never carried the spec.</summary>
    public static SidebarCardDrop Drop(SidebarCardSurface surface, in SidebarLibraryEntry entry)
        => entry.Kind != SidebarEntryKind.Playlist ? SidebarCardDrop.None
         : entry.CanEdit ? SidebarCardDrop.Deposit
         : surface == SidebarCardSurface.Rail ? SidebarCardDrop.None
         : SidebarCardDrop.Refuse;

    /// <summary>A card is a drag source (pin it, add its tracks to a playlist) unless it is a track — a track is never a pin
    /// source, enforced by the kind and not per surface.</summary>
    public static bool CanDrag(in SidebarLibraryEntry entry) => !entry.IsTrack;

    /// <summary>A playlist or a folder is a rootlist member, so its drag payload is file-able in the tree.</summary>
    public static bool RootlistMember(in SidebarLibraryEntry entry)
        => entry.Kind is SidebarEntryKind.Playlist or SidebarEntryKind.Folder;

    /// <summary>The "…" glyph (the surface's own hot-revealed trailing button) exists on the hero row. A grid tile keeps the
    /// menu on right-click only: the shared corner "…" is a 30-DIP disc with 8 DIP of padding, which would cover a 24-110 DIP
    /// cover. The rail tile has no placement for one at all.</summary>
    public static bool ShowsMenuGlyph(SidebarCardSurface surface) => surface == SidebarCardSurface.Hero;

    /// <summary>The title a tile or a rail tile paints — Trap 5: an entry whose identity has not landed shows NOTHING, never
    /// the raw uri fragment.</summary>
    public static string TitleOf(in SidebarLibraryEntry entry)
        => entry.Name.Length > 0 ? entry.Name
         : SidebarProjection.ShouldShowUriFallbackTitle(entry.IsPinned, entry.IdentityKnown) ? Sidebar.PaneText.ShortUri(entry.Uri)
         : "";

    /// <summary>An entry with no title yet. The rail tile is label-less (its tooltip IS the title), so such an entry renders
    /// as a bone instead of a tile with an empty tooltip — and, like an unresolved <c>EntityRow</c>, is not invokable.</summary>
    public static bool IsPending(in SidebarLibraryEntry entry) => TitleOf(in entry).Length == 0;

    /// <summary>The hero's title: the authored alias, then the resolved name, then the item's cached title, then the key's
    /// last segment — never blank.</summary>
    public static string HeroTitle(string? alias, bool resolved, string? name, string? cached, string rowKey)
        => alias is { Length: > 0 } ? alias
         : resolved && name is { Length: > 0 } ? name
         : cached is { Length: > 0 } ? cached
         : Sidebar.PaneText.ShortUri(rowKey);

    /// <summary>The hero's art square: its pinned height less the row's padding on both sides, so the surface's content is
    /// EXACTLY the section's one card height (iron rule 4).</summary>
    public static float HeroCover(float cardHeight) => cardHeight - 2f * SurfaceGeometry.RowPad;

    /// <summary>The hero's shape: the shared sidebar hero row at the section's card height and its derived art edge.</summary>
    public static SurfaceShape HeroShape(float cardHeight)
        => Shape.SidebarHero with { ArtEdge = HeroCover(cardHeight), MinHeight = cardHeight };

    /// <summary>A grid tile's cover: the cell less the shared grid card's plate padding on both sides. The cover is a fixed
    /// square (it is built from a <c>Cover</c> factory, not a fluid image), so it is sized from the cell the strip derived.</summary>
    public static float TileCover(float cell) => MathF.Max(0f, cell - 2f * SurfaceGeometry.ShelfPlatePad);
}

public static partial class Sidebar
{
    /// <summary>THE adapters: a projected entry (or a hero's plan row) → the <see cref="Controls.CardData"/> its media surface
    /// renders. Each runs inside its slot's render, so it reads the live pane state a recycle needs, and every delegate it hands
    /// the surface is a closure over a SNAPSHOT of the entry (an <c>in</c> parameter cannot be captured) — the host invokes the
    /// newest pushed one through its trampolines, so a data-equal re-push with fresh closures is still honoured.</summary>
    internal static class SidebarCards
    {
        // ── the hero (SidebarRowKind.EntityCard) ─────────────────────────────────────────────────────────────────────

        /// <summary>The spotlight card's data. An UNRESOLVED entity is still a card — its title and art come from the item's
        /// cached values and it carries no click, menu, play, drag or drop; the caller dims and disables it
        /// (<c>Resolved</c>), because a surface has no disabled state of its own. Selection is the pane's own resolver, so the
        /// card that draws the selected skin and the row whose epoch the route sweep bumped can never disagree.</summary>
        public static (Controls.CardData Data, bool Resolved) Hero(PaneView o, SidebarSectionSpec section, in SidebarRow row,
                                                                    string sel, int index)
        {
            string rowKey = row.Key;
            string sectionId = row.SectionId;
            var item = PaneText.ItemOf(section, rowKey);
            var entries = o.Plan.Entries;
            bool resolved = (uint)row.EntryIndex < (uint)entries.Count;
            var entry = resolved ? entries[row.EntryIndex] : default;
            float height = PaneMetrics.CardHeight(section);
            float cover = SidebarCardRules.HeroCover(height);

            string title = SidebarCardRules.HeroTitle(item?.LabelOverride, resolved, resolved ? entry.Name : null,
                item?.FallbackTitle, rowKey);
            string? subtitle = resolved ? PaneText.SubtitleOf(in entry) : Loc.Get(PaneLoc.MissingEntity);
            bool circular = resolved
                ? entry.Circular || entry.Kind == SidebarEntryKind.Artist
                : item?.EntityKind == SidebarEntityKind.Artist;
            // ForEntry for a resolved card: it owes the entry's kind dispatch (folder tile, route glyph, Liked's cover).
            Element art = resolved
                ? Cover.ForEntry(in entry, cover)
                : Cover.ArtUrl(item?.FallbackImageUrl, rowKey, cover, circular);

            Action? play = null;
            Action? click = null;
            DragSource? drag = null;
            DropTargetSpec? drop = null;
            Func<ContextMenuModel?>? menu = null;
            string uri = resolved ? entry.Uri : "";
            if (resolved)
            {
                if (SidebarCardRules.HasPlay(SidebarCardSurface.Hero, in entry, section.Opts.PlayButton))
                    play = () => o.Play(uri, asTrack: false);
                click = ActivateOf(o, in entry, title);
                drag = DragOf(in entry);
                drop = DropOf(o, SidebarCardSurface.Hero, sectionId, in entry);
                menu = o.EntryMenu(section, index, in entry, item, rowKey);
            }

            var data = new Controls.CardData(uri, title, SubtitleOf(subtitle), null, click, play, circular, drag,
                ShowMenu: SidebarCardRules.ShowsMenuGlyph(SidebarCardSurface.Hero), CoverOverride: art)
            {
                Height = height,
                Selected = o.RowSelectsRoute(index, sel),
                Menu = menu,
                Drop = drop,
            };
            return (data, resolved);
        }

        // ── the grid tile (one cell of a SidebarRowKind.GridStrip) ──────────────────────────────────────────────────

        /// <summary>One grid cell's data. A cell is not a plan row (one strip draws several), so it asks the resolver about the
        /// ENTRY — the same predicate the row-level sweep ORs across the strip's range.</summary>
        public static Controls.CardData Tile(PaneView o, SidebarSectionSpec section, in SidebarLibraryEntry entry, float cell,
                                             string sel)
        {
            var e = entry;
            bool circular = e.Circular || e.Kind == SidebarEntryKind.Artist;
            Action? play = null;
            if (SidebarCardRules.HasPlay(SidebarCardSurface.Tile, in e, playButton: true))
            {
                string uri = e.Uri;
                play = () => o.Play(uri, asTrack: false);
            }
            // ForEntry, never the raw cover factory: an app-route entry keeps its glyph tile and Liked its dynamic cover.
            return new Controls.CardData(e.Uri, SidebarCardRules.TitleOf(in e),
                section.Opts.Subtitles ? SubtitleOf(PaneText.SubtitleOf(in e)) : null, null,
                ActivateOf(o, in e, e.Name), play, circular, DragOf(in e),
                ShowMenu: SidebarCardRules.ShowsMenuGlyph(SidebarCardSurface.Tile),
                CoverOverride: Cover.ForEntry(in e, SidebarCardRules.TileCover(cell)))
            {
                Selected = SidebarCardRules.Selected(in e, sel),
                Menu = o.GridCellMenu(in e),
                Drop = DropOf(o, SidebarCardSurface.Tile, section.Id, in e),
            };
        }

        // ── the rail tile (label-less: the tooltip IS the title) ────────────────────────────────────────────────────

        /// <summary>A projected entry as a rail tile: the cover at the detent's art edge (<see cref="SidebarRailMetrics.Art"/>),
        /// the entry's menu on right-click (the same one the expanded row opens), and — for an editable playlist — the
        /// track-deposit drop spec whose cue is the rail's own published uri (<see cref="PaneView.IsRailDropActive"/>).</summary>
        public static Controls.CardData RailOf(PaneView o, string sectionId, in SidebarLibraryEntry entry, string sel,
                                               in SidebarRailMetrics m)
        {
            var e = entry;
            bool circular = e.Circular || e.Kind == SidebarEntryKind.Artist;
            return new Controls.CardData(e.Uri, SidebarCardRules.TitleOf(in e), null, null,
                ActivateOf(o, in e, e.Name), null, circular, DragOf(in e),
                ShowMenu: SidebarCardRules.ShowsMenuGlyph(SidebarCardSurface.Rail),
                CoverOverride: Cover.Art(e.Cover, e.MosaicTiles, e.Id, m.Art, circular))
            {
                Selected = SidebarCardRules.Selected(in e, sel),
                Menu = o.RailTileMenu(sectionId, in e),
                Drop = DropOf(o, SidebarCardSurface.Rail, sectionId, in e),
            };
        }

        /// <summary>A rail tile from a mode's own parts (Library V3's hand-placed tiles carry an item, not a projected entry):
        /// the art, the title the tooltip shows, what a click does and whether it is the open route.</summary>
        public static Controls.CardData RailOf(Element art, string title, string uri, Action? click, bool selected,
                                               DropTargetSpec? drop = null)
            => new(uri, title, null, null, click, null, false, null, ShowMenu: false, CoverOverride: art)
            {
                Selected = selected,
                Drop = drop,
            };

        // ── the shared pieces ────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Today's activation, exactly: a track plays, a routed entry navigates (naming the entry's own title),
        /// anything else has no click. Plain activation — none of these surfaces is multi-selectable, so no modifier-aware
        /// path needs to travel through <c>CardData.OnClick</c>.</summary>
        static Action? ActivateOf(PaneView o, in SidebarLibraryEntry entry, string navTitle)
        {
            var e = entry;
            switch (SidebarCardRules.Activation(in e))
            {
                case SidebarCardActivation.Play:
                    return () => o.Play(e.Uri, asTrack: true);
                case SidebarCardActivation.Navigate:
                {
                    string route = e.RouteKey!;
                    return () => o.Navigate(route, navTitle, in e);
                }
                default:
                    return null;
            }
        }

        /// <summary>The resource drag, click-primary: navigating is the constant intent, so a click landed while the mouse is
        /// still travelling is not eaten by a promotion (the <c>EntityRow</c> rule). The payload is built when the gesture lifts.</summary>
        static DragSource? DragOf(in SidebarLibraryEntry entry)
        {
            if (!SidebarCardRules.CanDrag(in entry)) return null;
            var e = entry;
            bool member = SidebarCardRules.RootlistMember(in e);
            return Drag.Source(() => PaneView.PayloadOf(in e, member), clickPrimary: true);
        }

        /// <summary>The pane's own drop spec — one resolver, one commit. Rail tiles also publish the rail cue uri.</summary>
        static DropTargetSpec? DropOf(PaneView o, SidebarCardSurface surface, string sectionId, in SidebarLibraryEntry entry)
        {
            switch (SidebarCardRules.Drop(surface, in entry))
            {
                case SidebarCardDrop.Deposit:
                    return o.ResourceDropSpec(sectionId, slot: -1, entry.Uri, entry.Name,
                        railCueUri: surface == SidebarCardSurface.Rail ? entry.Uri : null, isPlaylistRow: true);
                case SidebarCardDrop.Refuse:
                    return o.ResourceDropSpec(sectionId, slot: -1, playlistUri: null, entry.Name, isPlaylistRow: true);
                default:
                    return null;
            }
        }

        static Element? SubtitleOf(string? subtitle)
            => subtitle is { Length: > 0 } s
                ? global::Wavee.Design.Type.TrackMeta(s) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f }
                : null;
    }
}
