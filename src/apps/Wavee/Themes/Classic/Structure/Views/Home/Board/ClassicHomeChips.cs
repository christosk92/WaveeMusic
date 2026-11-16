// ── Themes/Classic/Structure/Views/Home/Board/ClassicHomeChips.cs — a tile's chips in Edit Home, and its menu (spec 8.3) ────────────────────────
//
// In Edit Home every tile shows its chips (`home:tile:<id>:tools`): the SIZE chip ("Card ▾"; an icon on a speed-dial cell) opens a short radio menu of
// the sizes its source declares (Speed-dial cell, Card, Wide card, Shelf); OPTIONS (when the source has any) opens the tile in place; HIDE (✕) or UNPIN
// removes it; a shelf also has STICK and LOWER (to its group's edges). Every chip is one verb; every chip has its key twin on the tile (Delete,
// Alt+↑/↓, Alt+Shift+↑/↓). The chips draw nothing at rest (the strip and the cell's lane are 0 there, so nothing of them takes focus).
//
// The tile's MENU (right-click, the Menu key, Shift+F10) holds the same verbs and, at rest, "Edit Home": the sidebar's "Section options" shape
// (Size ›, Options, Move up / Move down, Stick to top / Lower to bottom, Hide from Home / Unpin from Home).

using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

/// <summary>A tile's chips and menu.</summary>
internal static class ClassicHomeChips
{
    /// <summary>The chips of <paramref name="f"/>'s tile; <paramref name="cell"/>: a speed-dial cell's two (size as an icon, remove).</summary>
    public static Element Create(HomeTileFrame f, ClassicHomeBoardLayout lay, bool cell)
        => Embed.Comp(new ChipsProps(f, lay, cell), static () => new Chips()) with { Key = HomeBoardKeys.Tools(f.Entry.Id) };

    internal sealed record ChipsProps(HomeTileFrame Frame, ClassicHomeBoardLayout Lay, bool Cell)
    {
        public bool Equals(ChipsProps? o) => o is not null && Frame.Entry.Equals(o.Frame.Entry) && Frame.Form == o.Frame.Form
            && ReferenceEquals(Frame.Verbs, o.Frame.Verbs) && Lay.Equals(o.Lay) && Cell == o.Cell;
        public override int GetHashCode() => Frame.Entry.GetHashCode();
    }

    sealed class Chips : Component
    {
        NodeHandle _size;
        OverlayHandle? _menu;
        ChipsProps? _p;

        public override Element Render()
        {
            var p = UseProps<ChipsProps>();
            _p = p;
            var f = p.Frame;
            if (!f.View.Editing.Value) return new BoxEl();
            var e = f.Entry;
            var verbs = f.Verbs;
            string id = e.Id;
            float box = p.Lay.ChipBox;
            string key = HomeBoardKeys.Tools(id);
            var kids = new List<Element>(5);
            bool full = ClassicHomeBoardRules.SizeOf(e) == HomeSize.Full;
            if (full && !p.Cell)
            {
                kids.Add(Chip(key + ":stick", Loc.Get(Strings.Theme.Classic.Home.Board.Stick), Icons.ChevronUp, () => verbs.MoveToEdge(id, toStart: true), box));
                kids.Add(Chip(key + ":lower", Loc.Get(Strings.Theme.Classic.Home.Board.Lower), Icons.ChevronDown, () => verbs.MoveToEdge(id, toStart: false), box));
            }
            if (e.Source is { } src && src.Sizes.Length > 1 && !e.OnlyHide)
            {
                Element size = p.Cell
                    ? Controls.IconAction(Icons.ViewGrid, SizeMenu, size: box) with { Key = key + ":size", OnRealized = h => _size = h }
                    : Button.Create(ChipWord(ClassicHomeBoardRules.SizeOf(e)), SizeMenu, ButtonAppearance.Subtle, ControlSize.Small, Icons.ChevronDown)
                        with { Key = key + ":size", OnRealized = h => _size = h, MinHeight = box, Shrink = 0f };
                kids.Add(ToolTip.Wrap(size, Loc.Get(Strings.Theme.Classic.Home.Board.Size)));
            }
            if (e.HasOptions && !p.Cell)
                kids.Add(ToolTip.Wrap(Controls.IconAction(Icons.Settings, () => verbs.Open(id), size: box) with { Key = key + ":options" },
                                      Loc.Get(Strings.Theme.Classic.Home.Board.Options)));
            bool unpin = e.UserPin && e.Raw is PinSource;
            kids.Add(ToolTip.Wrap(Controls.IconAction(unpin ? Icons.UnPin : Icons.Cancel, () => verbs.Remove(id), size: box) with { Key = key + ":remove" },
                                  RemoveLabel(e)));
            return new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.XXS, Shrink = 0f,
                Padding = new Edges4(Spacing.XXS, 0f, Spacing.XXS, 0f),
                Children = kids.ToArray(),
            };
        }

        static Element Chip(string key, string label, string glyph, Action act, float box)
            => ToolTip.Wrap(Controls.IconAction(glyph, act, size: box) with { Key = key }, label);

        void SizeMenu()
        {
            if (_menu is { IsOpen: true } open) { open.Close(); return; }
            if (_p?.Frame is not { Overlay: { } overlay } f) return;
            ToolTip.CloseOpen();
            var items = SizeItems(f);
            _menu = overlay.Open(() => _size, () => MenuFlyout.Create(items, () => _menu?.Close()), FlyoutPlacement.BottomEdgeAlignedRight,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss));
            _menu.ClosedAction = () => _menu = null;
        }
    }

    /// <summary>The size chip's word: "Cell", "Card", "Wide", "Shelf".</summary>
    static string ChipWord(HomeSize s) => Loc.Get(s switch
    {
        HomeSize.S => Strings.Theme.Classic.Home.Board.ChipS,
        HomeSize.M => Strings.Theme.Classic.Home.Board.ChipM,
        HomeSize.L => Strings.Theme.Classic.Home.Board.ChipL,
        _ => Strings.Theme.Classic.Home.Board.ChipFull,
    });

    /// <summary>"Speed-dial cell", "Card", "Wide card", "Shelf".</summary>
    public static string SizeLabel(HomeSize s) => Loc.Get(s switch
    {
        HomeSize.S => Strings.Theme.Classic.Home.Board.SizeS,
        HomeSize.M => Strings.Theme.Classic.Home.Board.SizeM,
        HomeSize.L => Strings.Theme.Classic.Home.Board.SizeL,
        _ => Strings.Theme.Classic.Home.Board.SizeFull,
    });

    /// <summary>The sizes the source declares as radio rows (Classic's 36 rows; the current one marked).</summary>
    static IReadOnlyList<MenuFlyoutItem> SizeItems(HomeTileFrame f)
    {
        var e = f.Entry;
        var cur = ClassicHomeBoardRules.SizeOf(e);
        var items = new List<MenuFlyoutItem>(4);
        if (e.Source is not { } src) return items;
        string id = e.Id;
        var verbs = f.Verbs;
        foreach (var size in src.Sizes)
        {
            var s = size;
            items.Add(MenuFlyoutItem.RadioItem(SizeLabel(s), s == cur, () => verbs.SetSize(id, s)));
        }
        return items;
    }

    /// <summary>What the remove chip and row say: "Hide from Home", "Unpin from Home", "Hide this shortcut".</summary>
    static string RemoveLabel(BoardEntry e) => Loc.Get(e.Kind switch
    {
        BoardEntryKind.FillCell => Strings.Theme.Classic.Home.Board.HideShortcut,
        _ when e.UserPin && e.Raw is PinSource => Strings.Theme.Classic.Home.Board.Unpin,
        _ => Strings.Theme.Classic.Home.Board.Hide,
    });

    /// <summary>The tile's menu: at rest "Edit Home" first; Size ›, Options, Pause live updates, the moves, the remove row.</summary>
    public static IReadOnlyList<MenuFlyoutItem> MenuRows(HomeTileFrame f)
    {
        var e = f.Entry;
        var verbs = f.Verbs;
        string id = e.Id;
        var items = new List<MenuFlyoutItem>(12);
        if (!f.View.Editing.Peek())
        {
            items.Add(new MenuFlyoutItem(Loc.Get(Strings.Theme.Classic.Home.Board.EditHome), Icons.Edit, Invoke: () => ClassicHomeBoard.Live?.SetEditing(true, id)));
            items.Add(MenuFlyoutItem.Separator);
        }
        if (e.Source is { } src && src.Sizes.Length > 1 && !e.OnlyHide)
            items.Add(MenuFlyoutItem.SubMenu(Loc.Get(Strings.Theme.Classic.Home.Board.Size), SizeItems(f)));
        if (e.HasOptions) items.Add(new MenuFlyoutItem(Loc.Get(Strings.Theme.Classic.Home.Board.Options), Icons.Settings, Invoke: () => verbs.Open(id)));
        if (e.Live) items.Add(MenuFlyoutItem.Toggle(Loc.Get(Strings.Theme.Classic.Home.Board.PauseLive), e.Paused, () => verbs.ToggleLive(id)));
        if (items.Count > 0 && items[^1].Kind != MenuItemKind.Separator) items.Add(MenuFlyoutItem.Separator);
        items.Add(new MenuFlyoutItem(Loc.Get(Strings.Theme.Classic.Home.Board.MoveUp), Invoke: () => verbs.MoveBy(id, -1)));
        items.Add(new MenuFlyoutItem(Loc.Get(Strings.Theme.Classic.Home.Board.MoveDown), Invoke: () => verbs.MoveBy(id, 1)));
        items.Add(new MenuFlyoutItem(Loc.Get(Strings.Theme.Classic.Home.Board.StickToTop), Invoke: () => verbs.MoveToEdge(id, toStart: true)));
        items.Add(new MenuFlyoutItem(Loc.Get(Strings.Theme.Classic.Home.Board.LowerToBottom), Invoke: () => verbs.MoveToEdge(id, toStart: false)));
        items.Add(MenuFlyoutItem.Separator);
        bool unpin = e.UserPin && e.Raw is PinSource;
        items.Add(new MenuFlyoutItem(RemoveLabel(e), unpin ? Icons.UnPin : Icons.Cancel, Invoke: () => verbs.Remove(id)));
        return items;
    }
}
