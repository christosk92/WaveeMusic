// ── Shell/Sidebar.Cards.cs ─────────────────────────────────────────────────────────────────────────────────────────
// the sidebar's projection → media-surface adapters: a grid tile is ONE `Controls.Surface` fed by `SidebarCards`, and
// every decision the adapter makes is a function in `SidebarCardRules`
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

/// <summary>The sidebar's media card: a Grid-presentation cell. (The rows, the folder tile and the glyph tiles are the
/// sidebar's own grammar, not media.)</summary>
public enum SidebarCardSurface : byte { Tile }

/// <summary>What activating a projected entry does: a track PLAYS (it has no detail route), everything with a route
/// NAVIGATES, a folder (it expands in place) and a route-less entry do nothing — their surface carries NO click, so it is
/// display-only (no hand, no Button role, no tab stop).</summary>
public enum SidebarCardActivation : byte { None, Play, Navigate }

/// <summary>What a card is as a drop destination: nothing, a track DEPOSIT (an editable playlist), or a REFUSAL that
/// says why (a playlist the user cannot edit — the sidebar names the reason).</summary>
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

    /// <summary>A play affordance exists on the grid tile for an entry that has a playable context. <c>IsPlayable</c> is
    /// Playlist / Album / Show / Track: an artist has no single context to start.</summary>
    public static bool HasPlay(in SidebarLibraryEntry entry) => entry.IsPlayable && !string.IsNullOrEmpty(entry.Uri);

    /// <summary>A playlist is the one drop destination a card can be. An editable one takes a track deposit; a read-only one
    /// REFUSES with a reason — exactly what <c>EntityRow</c> does.</summary>
    public static SidebarCardDrop Drop(SidebarCardSurface surface, in SidebarLibraryEntry entry)
        => entry.Kind != SidebarEntryKind.Playlist ? SidebarCardDrop.None
         : entry.CanEdit ? SidebarCardDrop.Deposit
         : SidebarCardDrop.Refuse;

    /// <summary>A card is a drag source (pin it, add its tracks to a playlist) unless it is a track — a track is never a pin
    /// source, enforced by the kind and not per surface.</summary>
    public static bool CanDrag(in SidebarLibraryEntry entry) => !entry.IsTrack;

    /// <summary>A playlist or a folder is a rootlist member, so its drag payload is file-able in the tree.</summary>
    public static bool RootlistMember(in SidebarLibraryEntry entry)
        => entry.Kind is SidebarEntryKind.Playlist or SidebarEntryKind.Folder;

    /// <summary>The title a tile paints — Trap 5: an entry whose identity has not landed shows NOTHING, never
    /// the raw uri fragment.</summary>
    public static string TitleOf(in SidebarLibraryEntry entry)
        => entry.Name.Length > 0 ? entry.Name
         : SidebarProjection.ShouldShowUriFallbackTitle(entry.IsPinned, entry.IdentityKnown) ? Sidebar.PaneText.ShortUri(entry.Uri)
         : "";

    /// <summary>An entry with no title yet, which renders as a bone instead of a tile with an empty title — and, like an
    /// unresolved <c>EntityRow</c>, is not invokable.</summary>
    public static bool IsPending(in SidebarLibraryEntry entry) => TitleOf(in entry).Length == 0;

    /// <summary>An artist and a circular entry are drawn as a circle (the grid tile's cover box follows the same answer).</summary>
    public static bool Circular(in SidebarLibraryEntry entry) => entry.Circular || entry.Kind == SidebarEntryKind.Artist;

    /// <summary>A grid tile's cover: the cell less the shared grid card's plate padding on both sides. The cover is a fixed
    /// square (it is built from a <c>Cover</c> factory, not a fluid image), so it is sized from the cell the strip derived.</summary>
    public static float TileCover(float cell) => MathF.Max(0f, cell - 2f * SurfaceGeometry.ShelfPlatePad);
}

public static partial class Sidebar
{
    /// <summary>THE adapters: a projected entry → the <see cref="Controls.CardData"/> its media surface
    /// renders. Each runs inside its slot's render, so it reads the live pane state a recycle needs, and every delegate it hands
    /// the surface is a closure over a SNAPSHOT of the entry (an <c>in</c> parameter cannot be captured) — the host invokes the
    /// newest pushed one through its trampolines, so a data-equal re-push with fresh closures is still honoured.</summary>
    internal static class SidebarCards
    {
        // ── the grid tile (one cell of a SidebarRowKind.GridStrip) ──────────────────────────────────────────────────

        /// <summary>One grid cell's data. A cell is not a plan row (one strip draws several), so it asks the resolver about the
        /// ENTRY — the same predicate the row-level sweep ORs across the strip's range. The menu stays on right-click only: the
        /// shared corner "…" is a 30-DIP disc with 8 DIP of padding, which would cover a 24-110 DIP cover.</summary>
        public static Controls.CardData Tile(PaneView o, SidebarSection section, in SidebarLibraryEntry entry, float cell,
                                             string sel)
        {
            var e = entry;
            bool circular = SidebarCardRules.Circular(in e);
            Action? play = null;
            if (SidebarCardRules.HasPlay(in e))
            {
                string uri = e.Uri;
                play = () => o.Play(uri, asTrack: false);
            }
            // ForEntry, never the raw cover factory: an app-route entry keeps its glyph tile and Liked its dynamic cover.
            return new Controls.CardData(e.Uri, SidebarCardRules.TitleOf(in e),
                section.Shape == SidebarRowShape.EntityTwoLine ? SubtitleOf(PaneText.SubtitleOf(in e)) : null, null,
                ActivateOf(o, in e, e.Name), play, circular, DragOf(in e),
                ShowMenu: false,
                CoverOverride: CoverOf(in e, SidebarCardRules.TileCover(cell)))
            {
                Selected = SidebarCardRules.Selected(in e, sel),
                Menu = o.GridCellMenu(in e),
                Drop = DropOf(o, SidebarCardSurface.Tile, section.Id, in e),
            };
        }

        /// <summary>The cover box of a grid tile: the entry's art, or for an entry with no art yet its kind glyph (24,
        /// <c>TextSecondary</c>) on the subtle fill — never the grey skeleton bone the placeholder tile draws.</summary>
        static Element CoverOf(in SidebarLibraryEntry e, float size)
        {
            if (HasArt(in e)) return Cover.ForEntry(in e, size);
            return new BoxEl
            {
                Width = size, Height = size, Shrink = 0f,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Corners = CornerRadius4.All(Cover.Radius(size, SidebarCardRules.Circular(in e))),
                Fill = Tok.FillSubtleSecondary,
                Children = [Ui.Icon(KindGlyph(in e), 24f, Tok.TextSecondary)],
            };
        }

        /// <summary>An entry paints art when it has a cover, a mosaic or is the Liked collection (its own mosaic / heart).
        /// Folders and app routes wear their own glyph, so they never take the bone either.</summary>
        static bool HasArt(in SidebarLibraryEntry e)
        {
            if (string.Equals(e.Id, SidebarCatalogue.LikedRoute, StringComparison.Ordinal) || EntityUri.IsLikedCollection(e.Uri))
                return true;
            if (e.Kind is SidebarEntryKind.Folder or SidebarEntryKind.AppRoute) return false;
            return Controls.ArtUrl(e.Cover) is not null || e.MosaicTiles is { Count: > 0 };
        }

        /// <summary>The glyph a no-art tile carries: the entry kind's own mark (a folder's or a route's glyph included), the one
        /// rule the Zune band's unresolved pins share (<see cref="SidebarKindGlyph"/>).</summary>
        static string KindGlyph(in SidebarLibraryEntry e) => SidebarKindGlyph.For(e.Kind, e.Id, e.Uri);

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

        /// <summary>The pane's own drop spec — one resolver, one commit.</summary>
        static DropTargetSpec? DropOf(PaneView o, SidebarCardSurface surface, string sectionId, in SidebarLibraryEntry entry)
        {
            switch (SidebarCardRules.Drop(surface, in entry))
            {
                case SidebarCardDrop.Deposit:
                    return o.ResourceDropSpec(sectionId, slot: -1, entry.Uri, entry.Name, isPlaylistRow: true);
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
