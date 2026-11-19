// ── Themes/WinUi/Structure/Views/Discovery/Home/Board/WinUiHomeTileMenu.cs — a card's ⋯ menu (spec 8.1; concept `tileMenu`, winui arm) ─────────
//
// Windows 11's widget menu: the sizes the source declares as radio rows (the stock pill mark, `MenuRadioMark.Pill`, WinUI's menu style), then
// "Customize widget" when the source has options, "Pause live updates" for a live source, then Start's "Move to front" / "Move left" / "Move right"
// (with the Alt+Shift chord as their accelerator text), then the remove row named for what it does. Every row is one verb. Merged under an item's own
// rows (a pin's album, the daylist's playlist: one menu per card), the sizes fold into a "Size" submenu so the joined menu stays one screen tall.

using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Localization;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.WinUi;

/// <summary>The rows of a card's menu.</summary>
internal static class WinUiHomeTileMenu
{
    /// <summary>The menu of <paramref name="f"/>'s card.</summary>
    public static IReadOnlyList<MenuFlyoutItem> Items(HomeTileFrame f) => Items(f, sizeSubmenu: false);

    /// <summary>The card's rows under its item's own (<see cref="HomeTileMenus"/>): the sizes in a "Size" submenu.</summary>
    public static IReadOnlyList<MenuFlyoutItem> MergedItems(HomeTileFrame f) => Items(f, sizeSubmenu: true);

    static IReadOnlyList<MenuFlyoutItem> Items(HomeTileFrame f, bool sizeSubmenu)
    {
        var e = f.Entry;
        var verbs = f.Verbs;
        string id = e.Id;
        var items = new List<MenuFlyoutItem>(14);
        var cur = WinUiHomeBoardRules.SizeOf(e);
        if (e.Source is { } src && src.Sizes.Length > 1 && !e.OnlyHide)
        {
            var sizes = sizeSubmenu ? new List<MenuFlyoutItem>(src.Sizes.Length) : items;
            foreach (var size in src.Sizes)
            {
                var s = size;
                sizes.Add(MenuFlyoutItem.RadioItem(SizeLabel(s), s == cur, () => verbs.SetSize(id, s)));
            }
            if (sizeSubmenu) items.Add(MenuFlyoutItem.SubMenu(Loc.Get(Strings.Theme.Winui.Home.Board.Size), sizes));
            else items.Add(MenuFlyoutItem.Separator);
        }
        bool tail = false;
        if (e.HasOptions)
        {
            // Hint #12: "Customize widget" wears WinUI's InfoBadge dot the first time the daylist rolls over on Home.
            items.Add(new MenuFlyoutItem(Loc.Get(Strings.Theme.Winui.Home.Board.Customize), Icons.Edit, Invoke: () => { HomeTileMenus.OptionsChosen(f); verbs.Open(id); })
            {
                Attention = HomeTileMenus.OptionsAttention(f),
            });
            tail = true;
        }
        if (e.Live)
        {
            items.Add(MenuFlyoutItem.Toggle(Loc.Get(Strings.Theme.Winui.Home.Board.PauseLive), e.Paused, () => verbs.ToggleLive(id)));
            tail = true;
        }
        if (tail) items.Add(MenuFlyoutItem.Separator);
        items.Add(new MenuFlyoutItem(Loc.Get(Strings.Theme.Winui.Home.Board.MoveFront), Invoke: () => verbs.MoveToEdge(id, toStart: true)));
        items.Add(new MenuFlyoutItem(Loc.Get(Strings.Theme.Winui.Home.Board.MoveLeft), Invoke: () => verbs.MoveBy(id, -1))
            { AcceleratorText = Loc.Get(Strings.Theme.Winui.Home.Board.MoveLeftKeys) });
        items.Add(new MenuFlyoutItem(Loc.Get(Strings.Theme.Winui.Home.Board.MoveRight), Invoke: () => verbs.MoveBy(id, 1))
            { AcceleratorText = Loc.Get(Strings.Theme.Winui.Home.Board.MoveRightKeys) });
        // The remove row only where something brings the tile back (a pin: Pin to Home); with the add surface off a widget or a section has no
        // way back (HomeBoardVerbs.CanRemove).
        if (HomeBoardVerbs.CanRemove(e))
        {
            items.Add(MenuFlyoutItem.Separator);
            items.Add(new MenuFlyoutItem(RemoveLabel(e), Icons.UnPin, Invoke: () => verbs.Remove(id)));
        }
        return items;
    }

    /// <summary>The menu of a tile whose thing is gone (or unknown to this build): only its removal (offered only where
    /// <see cref="HomeBoardVerbs.CanRemove"/> allows it: the tile then shows no menu, <see cref="WinUiHomeTiles.MenuOf"/>).</summary>
    public static IReadOnlyList<MenuFlyoutItem> RemoveOnly(HomeTileFrame f)
    {
        var verbs = f.Verbs;
        string id = f.Entry.Id;
        return [new MenuFlyoutItem(RemoveLabel(f.Entry), Icons.UnPin, Invoke: () => verbs.Remove(id))];
    }

    /// <summary>"Small", "Medium", "Large", "Full row".</summary>
    public static string SizeLabel(HomeSize s) => Loc.Get(s switch
    {
        HomeSize.S => Strings.Theme.Winui.Home.Board.SizeS,
        HomeSize.M => Strings.Theme.Winui.Home.Board.SizeM,
        HomeSize.L => Strings.Theme.Winui.Home.Board.SizeL,
        _ => Strings.Theme.Winui.Home.Board.SizeFull,
    });

    /// <summary>What the remove row says: "Unpin widget", "Unpin from Home", "Hide section", "Hide this shortcut".</summary>
    static string RemoveLabel(BoardEntry e) => Loc.Get(e.Kind switch
    {
        BoardEntryKind.FillCell => Strings.Theme.Winui.Home.Board.HideShortcut,
        BoardEntryKind.FeedSection or BoardEntryKind.Fold => Strings.Theme.Winui.Home.Board.HideSection,
        _ when e.Raw is SpotifySectionSource => Strings.Theme.Winui.Home.Board.HideSection,
        _ when e.Raw is PinSource => Strings.Theme.Winui.Home.Board.UnpinFromHome,
        _ => Strings.Theme.Winui.Home.Board.UnpinWidget,
    });
}
