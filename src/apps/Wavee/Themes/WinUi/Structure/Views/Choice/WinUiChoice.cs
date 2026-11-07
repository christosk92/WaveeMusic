// ── Themes/WinUi/Structure/Views/Choice/WinUiChoice.cs — WinUI's choice controls (winui menus/podcasts) ─────────────────────────────────
//
// The two shapes every WinUI sort, filter and scope takes (W-Flyouts, W-Library, W-LibraryList, W-Show):
//
//   · a SORT is a DropDownButton saying the choice in force in sentence case ("Recently added ⌄"), opening a MenuFlyout whose groups are caption
//     headers ("Sort by") over RadioMenuFlyoutItems (the engine's 36 rows, 14 text, 4 backplates, the radio bullet in the 28 check column) on the
//     8-radius acrylic flyout plate; Down / F4 / Enter open it, the arrows rove, Escape closes and focus returns to the button.
//   · a FILTER, MODE or SCOPE is a SelectorBar: sentence-case words on transparent items, the selected one marked by the 3 × 16 accent pill, one tab
//     stop with the arrows moving the selection.
//
// Neither is Zune's word rail (lowercase words, a heavy active run, an underline, a "more ▾" fold): that is Zune's (and Classic's), never WinUI's.
// The button's label and items freeze at mount (DropDownButton's contract), so the button is KEYED on what it says: a new choice is a new button.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;

namespace Wavee.Themes.WinUi;

/// <summary>WinUI's sort button and pivot.</summary>
internal static class WinUiChoice
{
    /// <summary>A sort DropDownButton: <paramref name="label"/> is the choice in force, <paramref name="items"/> the menu (built now: the button is keyed on
    /// <paramref name="state"/>, so a change of choice mounts a fresh button with fresh rows). <paramref name="height"/> above the stock 32 grows the plate
    /// (the Compact 40 target). <paramref name="name"/> is the accessible name the word alone does not give ("Sort by").</summary>
    public static Element Sort(string key, string state, string label, IReadOnlyList<MenuFlyoutItem> items, float height, string name)
    {
        var parts = height > WinUiChoiceRules.ControlH
            ? new TemplateParts { [DropDownButton.PartRoot] = b => b with { Height = height } }
            : null;
        Element button = Embed.Comp(() => new DropDownButton { Label = label, Items = items, Parts = parts }) with { Key = key + ":" + state };
        return new BoxEl { Key = key, Direction = 0, Shrink = 0f, AlignItems = FlexAlign.Center, Children = [Controls.Named(button, name)] };
    }

    /// <summary>A SelectorBar over <paramref name="labels"/> bound to <paramref name="selected"/> (the persisted signal: a click writes it). Items
    /// <paramref name="height"/> tall (the Compact 40 or the stock 40); the first item's text sits on the column edge.</summary>
    public static Element Pivot(IReadOnlyList<string> labels, Signal<int> selected, float height, Action<int>? onChange = null, IReadOnlyList<string?>? keys = null)
        => SelectorBar.Create(labels, selected, onChange, style: PivotStyle(height), itemKeys: keys);

    /// <summary>The pivot's look: the stock item (14 text, primary ink, the accent pill) at <paramref name="height"/>, its text on the column edge.</summary>
    public static SelectorBarStyle PivotStyle(float height) => new()
    {
        ItemHeight = MathF.Max(height, WinUiChoiceRules.PivotItemH), LeadingInset = WinUiChoiceRules.PivotLead,
    };

    /// <summary>A pivot word with its count ("All · 14"), or the bare word while the count is unknown ("").</summary>
    public static string Counted(string word, string count) => count.Length == 0 ? word : word + WinUiChoiceRules.CountJoin + count;
}
