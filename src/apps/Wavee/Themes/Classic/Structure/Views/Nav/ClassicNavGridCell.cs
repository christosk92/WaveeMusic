// ── Themes/Classic/Structure/Views/Nav/ClassicNavGridCell.cs — Classic's Library grid tile (themes plan 6.1, T5f) ──────────────────
//
// From `Shell/Sidebar.Cards.cs` (`SidebarCards`): the projection -> media-surface adapter for ONE cell of a grid strip. A grid tile is
// ONE `Controls.Surface` at the sidebar tile shape fed by the `CardData` this builds, and the picture of it (the cover box a no-art
// entry gets, the glyph it wears, the subtitle's look, the cover's size inside the card's plate) is the structure's.
//
// What stayed core is every DECISION the adapter makes, as functions over plain values in `SidebarCardRules` (selected, what activating
// does, play, drop, drag, the title gate, pending, circular), and the verbs and specs it hands the surface, which come from the pane
// through `INavRowHost` (navigate, play, the entry's menu, the resource drop spec, the drag payload).
//
// WHY AN ADAPTER, AND WHY THE SLOT CLASS STAYS HOOK-FREE: `ClassicNavRow` is a hook-free class (every builder is a plain method, so
// the hook order is identical across every recycle). A `Controls.Surface` is a hook-OWNING CHILD component, which that class explicitly
// sanctions, and it is legal here because it is PROPS-DRIVEN: `Embed.Comp(props, factory)` re-pushes the new `CardData` on every rebind
// of the recycling slot instead of freezing a constructor argument at mount. The surface's handlers are trampolines into the newest push,
// and the slot keeps its `ContentType` recycle pools. The slot is NOT a bound slot root that owns invoke and focus (the pane's rows own
// their own click), so the surface runs in FREE mode: it owns the click, the tab stop, the Button role and the hand cursor itself --
// ONE click owner and ONE tab stop per card.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

/// <summary>Classic's adapters from a projected entry to the media surface a grid cell renders.</summary>
internal static class ClassicNavGridCell
{
    /// <summary>One grid cell: the shared media surface at the sidebar tile shape. A cell is not a plan row (one strip draws several), so it asks
    /// the resolver about the ENTRY: the same predicate the row-level sweep ORs across the strip's range. The menu stays on right-click
    /// only: the shared corner "…" is a 30-DIP disc with 8 DIP of padding, which would cover a 24-110 DIP cover.</summary>
    public static Element Build(INavRowHost o, SidebarSection section, in SidebarLibraryEntry entry, float cell, string sel)
        => Controls.Surface(Tile(o, section, in entry, cell, sel), global::Wavee.Shape.SidebarTile);

    /// <summary>One grid cell's data. Each delegate it hands the surface is a closure over a SNAPSHOT of the entry (an <c>in</c> parameter
    /// cannot be captured): the host invokes the newest pushed one through its trampolines, so a data-equal re-push with fresh closures
    /// is still honoured.</summary>
    static Controls.CardData Tile(INavRowHost o, SidebarSection section, in SidebarLibraryEntry entry, float cell, string sel)
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
            section.Shape == SidebarRowShape.EntityTwoLine ? SubtitleOf(Sidebar.PaneText.SubtitleOf(in e)) : null, null,
            ActivateOf(o, in e, e.Name), play, circular, DragOf(o, in e),
            ShowMenu: false,
            CoverOverride: CoverOf(o, in e, SidebarGeometry.TileCover(cell)))
        {
            Selected = SidebarCardRules.Selected(in e, sel),
            Menu = o.GridCellMenu(in e),
            Drop = DropOf(o, section.Id, in e),
        };
    }

    /// <summary>The cover box of a grid tile: the entry's art, or for an entry with no art yet its kind glyph (24,
    /// <c>TextSecondary</c>) on the subtle fill — never the grey skeleton bone the placeholder tile draws.</summary>
    static Element CoverOf(INavRowHost o, in SidebarLibraryEntry e, float size)
    {
        if (HasArt(in e)) return Sidebar.Cover.ForEntry(in e, size);
        return new BoxEl
        {
            Width = size, Height = size, Shrink = 0f,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = CornerRadius4.All(Sidebar.Cover.Radius(size, SidebarCardRules.Circular(in e))),
            Fill = Tok.FillSubtleSecondary,
            Children = [Ui.Icon(KindGlyph(o, in e), SidebarGeometry.GridTileGlyph, Tok.TextSecondary)],
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

    /// <summary>The glyph a no-art tile carries: the entry kind's own mark (a folder's or a route's glyph included).</summary>
    static string KindGlyph(INavRowHost o, in SidebarLibraryEntry e) => e.Kind switch
    {
        SidebarEntryKind.Folder => Icons.Folder,
        SidebarEntryKind.AppRoute => o.DestOf(e.Id).Glyph,
        SidebarEntryKind.Album => Icons.Album,
        SidebarEntryKind.Artist => Icons.Contact,
        SidebarEntryKind.Show => Icons.Microphone,
        _ => Icons.MusicNote,
    };

    // ── the shared pieces ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Today's activation, exactly: a track plays, a routed entry navigates (naming the entry's own title),
    /// anything else has no click. Plain activation — none of these surfaces is multi-selectable, so no modifier-aware
    /// path needs to travel through <c>CardData.OnClick</c>.</summary>
    static Action? ActivateOf(INavRowHost o, in SidebarLibraryEntry entry, string navTitle)
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
    /// still travelling is not eaten by a promotion (the entity row's rule). The payload is built when the gesture lifts.</summary>
    static DragSource? DragOf(INavRowHost o, in SidebarLibraryEntry entry)
    {
        if (!SidebarCardRules.CanDrag(in entry)) return null;
        var e = entry;
        bool member = SidebarCardRules.RootlistMember(in e);
        return Drag.Source(() => o.PayloadOf(in e, member), clickPrimary: true);
    }

    /// <summary>The pane's own drop spec — one resolver, one commit.</summary>
    static DropTargetSpec? DropOf(INavRowHost o, string sectionId, in SidebarLibraryEntry entry)
    {
        switch (SidebarCardRules.Drop(in entry))
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
            ? R.Type.TrackMeta(s) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f }
            : null;
}
