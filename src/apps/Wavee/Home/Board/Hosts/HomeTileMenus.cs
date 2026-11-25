// ── Home/Board/Hosts/HomeTileMenus.cs — one ⋯ per card: a board tile's Home rows joined to its content's own menu ─────────────────────
//
// A board tile whose content already carries a ⋯ of its own (the daylist card's action row, a section header) shows ONE menu: the content's rows
// (the playlist's, the album's ...), a separator, then the tile's Home rows (sizes, move, remove ...) that the theme's tile menu builds. The theme's tile
// chrome hands the tile down through `Current` (`Host`); content that carries a ⋯ reads it and opens `Merge(...)` from that ⋯ and from a right-click.
// The board's own keys (the Menu key or Shift+F10 on a focused tile) reach the same menu through `TryOpen`, which the content's ⋯ registers; a
// tile's own ⋯ (a chapter header's) opens the content's menu that `Item` names over the tile's rows. Every theme's tile chrome hosts it (WinUI's
// card, Classic's frame, Zune's tile), each with its own rows: WinUI's and Classic's tile menus, Zune's way into customize.

using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using Wavee.Theming.Contracts;

namespace Wavee;

/// <summary>The board tile a piece of content stands in, handed down by the theme's tile chrome. One instance per mounted tile: the chrome refreshes
/// <see cref="Frame"/> on every render, so the rows built at open time are the tile's as it is now.</summary>
public sealed class HomeTileMenuSlot
{
    internal HomeTileMenuSlot(HomeTileFrame frame, Func<HomeTileFrame, IReadOnlyList<MenuFlyoutItem>> rows) { Frame = frame; Rows = rows; }

    /// <summary>The tile's frame (its entry, verbs, overlay) at its last render.</summary>
    public HomeTileFrame Frame { get; internal set; }

    /// <summary>The theme's tile menu rows for a frame.</summary>
    public Func<HomeTileFrame, IReadOnlyList<MenuFlyoutItem>> Rows { get; internal set; }

    /// <summary>Opens the merged menu at the content's ⋯ (set by the content that carries it; null: none mounted). A paged body (the release
    /// spotlight) sets it to open the menu of the card on screen.</summary>
    public Action? Open { get; set; }

    /// <summary>The content's own menu (the album of the card on screen, read when the menu opens; null: none), which the tile's own ⋯ (a chapter
    /// header's) opens with the tile's rows after it.</summary>
    public Func<ContextMenuModel?>? Item { get; set; }

    /// <summary>The tile's Home rows now.</summary>
    public IReadOnlyList<MenuFlyoutItem> TileRows() => Rows(Frame);
}

/// <summary>The merged card menu's plumbing (see the file head).</summary>
public static class HomeTileMenus
{
    /// <summary>The tile the content stands in (null outside the pinned board).</summary>
    public static readonly Context<HomeTileMenuSlot?> Current = new(null);

    /// <summary>Hint #12 (settings design 6.3): the daylist tile's options row carries a one-time dot in the menu opening now (the first time the
    /// daylist rolled over while Home was on screen; `Hints`, surface <see cref="HintSurfaces.DaylistMenu"/>). A theme's tile rows ask it when they
    /// build, at open time, and set <c>MenuFlyoutItem.Attention</c> on their options row; a yes records the show. Every other tile: false.</summary>
    public static bool OptionsAttention(HomeTileFrame frame)
        => frame.Entry.Source?.Id == HomeSourceRegistry.Daylist && Hints.BadgeOnOpen(HintSurfaces.DaylistMenu);

    /// <summary>A theme's options row was chosen (it opens the tile's options): for the daylist, the dot has done its job for this session.</summary>
    public static void OptionsChosen(HomeTileFrame frame)
    {
        if (frame.Entry.Source?.Id == HomeSourceRegistry.Daylist) Hints.BadgeFollowed(HintSurfaces.DaylistMenu);
    }

    static readonly Dictionary<string, HomeTileMenuSlot> s_live = new(StringComparer.Ordinal);

    /// <summary><paramref name="content"/> as the content of <paramref name="frame"/>'s tile, whose Home rows are <paramref name="rows"/>: the content's ⋯
    /// opens one menu with them. The host only provides the slot, so a loading tile's placeholder derives <paramref name="content"/> itself (the
    /// proxy): the skeleton deriver cannot run a component's render and derives a bare one to a single 160×10 bar, which left every pending tile
    /// on a cold Home 10 high, to grow to its loaded height when its answer landed and push the board under it down.</summary>
    public static Element Host(HomeTileFrame frame, Func<HomeTileFrame, IReadOnlyList<MenuFlyoutItem>> rows, Element content)
        => Embed.Comp(new HostProps(frame, rows, content), static () => new SlotHost()) with { SkeletonProxy = () => content };

    /// <summary>Open the merged menu of the tile <paramref name="id"/> at its content's ⋯ (the board's Menu key / Shift+F10). False: no content of that
    /// tile carries one.</summary>
    public static bool TryOpen(string id)
    {
        if (!s_live.TryGetValue(id, out var slot) || slot.Open is not { } open) return false;
        open();
        return true;
    }

    /// <summary>Open the tile <paramref name="id"/>'s own rows at <paramref name="anchor"/> (the board's Menu key on a tile whose content carries no
    /// menu of its own, Classic's). False: no tile of that id is hosted.</summary>
    public static bool OpenRows(string id, NodeHandle anchor)
    {
        if (!s_live.TryGetValue(id, out var slot) || slot.Frame.Overlay is not { } overlay || anchor.IsNull) return false;
        OpenAt(overlay, anchor, () => slot.TileRows() is { Count: > 0 } rows ? new ContextMenuModel([], rows) : null);
        return true;
    }

    /// <summary>The content's own menu (<paramref name="item"/>, null: none) joined at open time to the Home rows of the tile it stands in (read from
    /// <paramref name="tile"/> when the menu opens). Null when there is neither.</summary>
    public static Func<ContextMenuModel?>? Merge(Func<ContextMenuModel?>? item, HomeTileMenuSlot? tile)
    {
        if (tile is null) return item;
        return () => Merged(item?.Invoke(), tile.TileRows());
    }

    /// <summary>The content's model with the tile's rows after a separator. A content row that says what a tile row says ("Unpin from Home" on a pin) gives
    /// way to the tile's: one home per verb.</summary>
    public static ContextMenuModel? Merged(ContextMenuModel? item, IReadOnlyList<MenuFlyoutItem>? tile)
    {
        if (tile is null || tile.Count == 0) return item;
        if (item is not { } m) return new ContextMenuModel(tile);
        var rows = new List<MenuFlyoutItem>(m.Rows.Count + tile.Count + 1);
        foreach (var row in m.Rows)
        {
            if (row.IsSeparator ? rows.Count == 0 || rows[^1].IsSeparator : Offers(tile, row.Label)) continue;
            rows.Add(row);
        }
        while (rows.Count > 0 && rows[^1].IsSeparator) rows.RemoveAt(rows.Count - 1);
        if (rows.Count > 0 || m.Primary.Count > 0) rows.Add(MenuFlyoutItem.Separator);
        rows.AddRange(tile);
        return m with { Rows = rows };
    }

    static bool Offers(IReadOnlyList<MenuFlyoutItem> rows, string? label)
    {
        if (string.IsNullOrEmpty(label)) return false;
        foreach (var r in rows)
            if (!r.IsSeparator && string.Equals(r.Label, label, StringComparison.CurrentCultureIgnoreCase)) return true;
        return false;
    }

    /// <summary>Open <paramref name="factory"/>'s menu at <paramref name="anchor"/> as a context request would (the structure's sheet where its policy says
    /// so); <paramref name="keyboard"/> focuses the first row.</summary>
    public static void OpenAt(IOverlayService overlay, NodeHandle anchor, Func<ContextMenuModel?> factory, bool keyboard = true)
    {
        if (ContextSheets.SheetNow()) { ContextSheets.Open(overlay, factory); return; }
        var args = new ContextRequestEventArgs
        {
            Trigger = keyboard ? ContextRequestTrigger.Keyboard : ContextRequestTrigger.Invoke, Node = anchor, Source = anchor,
        };
        ContextMenu.Handler(null, overlay, factory)(args);
    }

    internal sealed record HostProps(HomeTileFrame Frame, Func<HomeTileFrame, IReadOnlyList<MenuFlyoutItem>> Rows, Element Content);

    /// <summary>Provides the tile's slot to its content and keeps it registered for the board's keys while it is mounted.</summary>
    sealed class SlotHost : Component
    {
        HomeTileMenuSlot? _slot;

        public override Element Render()
        {
            var p = UseProps<HostProps>();
            // One slot per mount (the context value never changes, so no content re-renders for it); its frame follows the tile's.
            var slot = _slot ??= new HomeTileMenuSlot(p.Frame, p.Rows);
            slot.Frame = p.Frame;
            slot.Rows = p.Rows;
            string id = p.Frame.Entry.Id;
            UseLayoutEffect(() =>
            {
                s_live[id] = slot;
                return () => { if (s_live.TryGetValue(id, out var x) && ReferenceEquals(x, slot)) s_live.Remove(id); };
            }, DepKey.From(id.GetHashCode()));
            return Ctx.Provide(Current, slot, p.Content);
        }
    }
}
