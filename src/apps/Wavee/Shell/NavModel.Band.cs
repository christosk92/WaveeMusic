// ── Shell/NavModel.Band.cs — a band's pins: their context menu and the drag-to-pin target (contract 1.88) ──────────────────────
//
// Role: CORE. A band-style pane (Zune) draws the pins from `BandPins`; it does not mount the sidebar pane, so the pane's row menus and
// its Pinned band drop are not there for it. This is the band's share of the same verbs:
//   · THE MENU (`BandPin.Menu`): the entity's own menu (`Menus.Container`: play, the queue pair, save, open, share) with the pin slot
//     filled by Unpin, and the band's Move left / Move right (the band shows the pins in a row); a route pin's Open · Unpin · the moves.
//     Unpin and the moves are the pin store's recorded writes (`Unpin`, `MovePin`), so they toast and undo as the sidebar's do.
//   · THE DROP (`BandProps.PinDrop`): an album, a playlist, an artist, a show or a route dropped on the pins is pinned where the band shows
//     it (`Drop` → `DropPin`: the signed-out guard and the already-pinned MOVE are the sidebar's). A track drag is transparent here.

using FluentGpu.Controls;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;

namespace Wavee;

public sealed partial class NavModel
{
    // ── the menu ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Does the band show this pin (a folder opens nothing and a pin with no route has nowhere to go): <see cref="BuildBandPins"/>' own test.</summary>
    static bool ShownInBand(SidebarPin pin) => pin.Kind != SidebarEntryKind.Folder && pin.RouteKey.Length > 0;

    /// <summary>The pin's menu, built at OPEN time from the live store (a pin moved or renamed since the band built is read as it is now).</summary>
    ContextMenuModel? BandPinMenu(string pinId)
    {
        var pins = Sidebar.Pins;
        int at = pins.IndexOf(pinId);
        if (at < 0) return null;
        var pin = pins[at];
        string name = pin.Name.Length > 0 ? pin.Name : PinName(pinId);
        string? art = pin.Cover.Length > 0 ? pin.Cover : null;
        var unpin = new MenuFlyoutItem(Loc.Get(Strings.Sidebar.Pin.UnpinShort), ActionIcons.Resolve(ActionIcons.Unpin), true,
            () => Unpin(pinId, name));
        var moves = BandMoveRows(pinId);
        // Open is the pin's own click (the same navigation, no journey origin), so the menu and the tile never go to two different places.
        string routeKey = pin.RouteKey;
        var open = new MenuFlyoutItem(Loc.Get(Strings.Menu.Open), ActionIcons.Resolve(ActionIcons.Open), routeKey.Length > 0,
            () => Navigate(routeKey, name));

        var uri = EntityUri.Parse(pin.Uri);
        var target = !uri.IsValid ? default : pin.Kind switch
        {
            SidebarEntryKind.Playlist => ActionTarget.ForPlaylist(uri, name),
            SidebarEntryKind.Album => ActionTarget.ForAlbum(uri, name),
            SidebarEntryKind.Artist => ActionTarget.ForArtist(uri, name),
            SidebarEntryKind.Show => ActionTarget.ForShow(uri, name),
            _ => default,
        };
        if (target.Kind != TargetKind.None
            && Menus.Container(in target, art, null, new ContainerExtras
            {
                Liked = EntityUri.IsLikedCollection(pin.Uri),
                Pin = () => unpin,
                Tail = moves,
            }) is { } entity)
            return WithOpen(entity, open, unpin);

        // A route pin (Your Episodes, a browse page): Open · Unpin, then the moves.
        var rows = new List<MenuFlyoutItem>(5) { open, unpin };
        if (moves is { Count: > 0 })
        {
            Actions.Menu.OpenGroup(rows);
            for (int i = 0; i < moves.Count; i++) rows.Add(moves[i]);
        }
        return new ContextMenuModel(rows, Actions.Menu.Header(art, name, null));
    }

    /// <summary>The art of a pin whose cached cover is still empty (one just pinned from a band, where no sidebar pane resolves the pins): the entity's
    /// own image when its row is loaded (it is: the page or the card it was dragged from showed it), cached into the pin through the store's display
    /// refresh (<see cref="Sidebar.TouchPin"/>: no commit of its own, no version bump), so it persists. Null when none is known.</summary>
    static string? BandCoverOf(SidebarPin pin)
    {
        if (pin.Uri.Length == 0) return null;
        var uri = EntityUri.Parse(pin.Uri);
        if (!uri.IsValid) return null;
        var scope = Entities.Current;
        StringId image = default;
        switch (pin.Kind)
        {
            case SidebarEntryKind.Playlist when scope.Playlists.TryGetSlot(uri.Id, out int p): image = new Playlist(p).ImageId; break;
            case SidebarEntryKind.Album when scope.Albums.TryGetSlot(uri.Id, out int a): image = new Album(a).ImageId; break;
            case SidebarEntryKind.Artist when scope.Artists.TryGetSlot(uri.Id, out int r): image = new Artist(r).ImageId; break;
            case SidebarEntryKind.Show when scope.Shows.TryGetSlot(uri.Id, out int s): image = new Show(s).ImageId; break;
        }
        if (image.IsEmpty) return null;
        Sidebar.TouchPin(pin.Id, null, image);
        return Entities.Strings.Resolve(image);
    }

    /// <summary>The entity menu with the pin's Open in front of its Unpin (where the card menu's Open row stands): the container menu offers Open
    /// only with a journey origin, and the pin's click carries none. An Open row the menu already has is kept as it is.</summary>
    static ContextMenuModel WithOpen(ContextMenuModel menu, MenuFlyoutItem open, MenuFlyoutItem unpin)
    {
        var rows = menu.Rows;
        string openLabel = open.Label;
        for (int i = 0; i < rows.Count; i++)
            if (string.Equals(rows[i].Label, openLabel, StringComparison.Ordinal)) return menu;
        var next = new List<MenuFlyoutItem>(rows.Count + 1);
        bool placed = false;
        for (int i = 0; i < rows.Count; i++)
        {
            if (!placed && ReferenceEquals(rows[i].Invoke, unpin.Invoke)) { next.Add(open); placed = true; }
            next.Add(rows[i]);
        }
        if (!placed) next.Insert(0, open);
        return menu with { Rows = next };
    }

    /// <summary>Move left / Move right among the pins the band shows, each present only where there is a shown neighbour that way (a move never
    /// carries a pin past the band's cap, where it would leave the band). Null when neither applies.</summary>
    List<MenuFlyoutItem>? BandMoveRows(string pinId)
    {
        var (_, prev, next) = BandNeighbours(pinId);
        if (prev < 0 && next < 0) return null;
        var rows = new List<MenuFlyoutItem>(2);
        if (prev >= 0) rows.Add(new MenuFlyoutItem(Loc.Get(Strings.Menu.MoveLeft), Icons.ChevronLeft, true, () => StepBandPin(pinId, -1)));
        if (next >= 0) rows.Add(new MenuFlyoutItem(Loc.Get(Strings.Menu.MoveRight), Icons.ChevronRight, true, () => StepBandPin(pinId, 1)));
        return rows;
    }

    /// <summary>The store indices of the pin and of its shown neighbours (-1 where there is none), among the first <c>MaxBandPins</c> the band shows.</summary>
    (int Self, int Prev, int Next) BandNeighbours(string pinId)
    {
        var pins = Sidebar.Pins;
        int max = Math.Max(0, _bandPinsMax), shown = 0, self = -1, prev = -1;
        for (int i = 0; i < pins.Count && shown < max; i++)
        {
            if (!ShownInBand(pins[i])) continue;
            shown++;
            if (self >= 0) return (self, prev, i);
            if (string.Equals(pins[i].Id, pinId, StringComparison.Ordinal)) self = i;
            else prev = i;
        }
        return self >= 0 ? (self, prev, -1) : (-1, -1, -1);
    }

    /// <summary>One shown step left (<paramref name="step"/> &lt; 0) or right, re-read at the click: the store's remove-then-insert puts the pin
    /// before its left neighbour, or after its right one.</summary>
    void StepBandPin(string pinId, int step)
    {
        var (self, prev, next) = BandNeighbours(pinId);
        int to = step < 0 ? prev : next;
        if (self >= 0 && to >= 0) MovePin(self, to);
    }

    // ── the drop ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    static readonly Signal<bool> s_bandDropOver = new(false);
    static BandPinDrop? s_bandDrop;
    static DragPayload? s_bandCaptionFor;
    static string? s_bandCaption;

    /// <summary>The band's drag-to-pin target (one instance: <c>BandProps</c> is one static record). Its verbs reach the model at the gesture.</summary>
    public static BandPinDrop BandDrop => s_bandDrop ??= new BandPinDrop(
        FluentGpu.Controls.Drop.Target<DragPayload>(Drag.Resource,
            accepts: static p => p.CanPin,
            onDrop: static (p, _) =>
            {
                s_bandDropOver.Value = false;
                var model = Sidebar.Model;
                model.Drop(p, NavDropTarget.PinIndex(model.BandDropSlot()));
            },
            onEnter: BandHover, onOver: BandHover,
            onLeave: static _ => s_bandDropOver.Value = false,
            // A track is not a pin: it passes through (no refusal cue), as it does over the sidebar's route rows.
            transparent: static p => !p.CanPin,
            visualPolicy: DropTargetVisualPolicy.Spotlight,
            spotlightWhen: static s => Drag.Unwrap(s.Payload) is { CanPin: true }),
        s_bandDropOver,
        Armed: static () =>
        {
            bool live = Drag.LivePinnable();
            // A gesture that ended over the pins (Escape, a drop elsewhere after leaving late) never reaches OnLeave: the edge clears the cue.
            if (!live) s_bandDropOver.SetIfChanged(false);
            return live;
        });

    /// <summary>The cue and the chip's caption ("Pin {name}"; an already pinned item moves, so it says nothing). The caption is cached per gesture:
    /// this runs per pointer move.</summary>
    static void BandHover(DragPayload p, DragSession s)
    {
        s_bandDropOver.SetIfChanged(true);
        if (!ReferenceEquals(p, s_bandCaptionFor))
        {
            s_bandCaptionFor = p;
            string? id = SidebarPinId.Canonical(p.Id) ?? SidebarPinId.Canonical(p.Uri);
            s_bandCaption = id is not null && Sidebar.IsPinned(id) ? null : Drag.Pin(p.Name);
        }
        s.Caption = s_bandCaption;
    }

    /// <summary>Where a drop on the band lands in the store: after the last pin the band shows, or, when the band is full, in the last place it
    /// shows (the newcomer is seen; the pin it displaces stays pinned, past the cap).</summary>
    int BandDropSlot()
    {
        var pins = Sidebar.Pins;
        int max = Math.Max(1, _bandPinsMax), shown = 0, last = -1;
        for (int i = 0; i < pins.Count; i++)
        {
            if (!ShownInBand(pins[i])) continue;
            if (++shown == max) return i;
            last = i;
        }
        return last + 1;
    }
}
