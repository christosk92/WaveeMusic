// ── Theming/Contracts/NavEdit.cs — the sidebar's Edit outline as a structure sees it (themes plan 6.1, T5d) ───────────────────
//
// Edit mode swaps the pane's head and list for the Outline: the edit bar (title, undo, redo, Reset, Done) over the sections' Show
// checkboxes, the reorder bands (sections, pins, Collections items), the hint rows and the Settings row. The core pane keeps
// everything that DECIDES: when the pane is editing (expanded only; the rail never draws it), the edit model (`NavEditModel`: the
// Outline read, every op, undo / redo, the notes Done shows, the key dispatch table), the persistence, the validation, the Reset
// menu's rows and the header menu's rows. The structure draws the Outline: the bar, the section blocks, the checkboxes and locks,
// the grips, the reorder lists and their spoken sentences, the drag chip's titles and the key wiring (engine key -> the model's
// table). The structure reaches the pane only through `INavEditHost`.
//
// The props are a mount seed: the host is the pane, and everything live is read from it inside a render.
//
// WVTH006: sealed records with no writable member.

using FluentGpu.Controls;

namespace Wavee.Theming.Contracts;

/// <summary>The pane, as the Edit outline sees it. Implemented by <c>Sidebar.PaneView</c> (<c>Shell\Sidebar.UI.Host.cs</c>).</summary>
internal interface INavEditHost
{
    /// <summary>The edit model: the Outline read, every op, undo / redo and the key tables. The view only calls it.</summary>
    NavEditModel Edit { get; }

    /// <summary>The overlay service the Reset and header menus open on.</summary>
    IOverlayService MenuOverlay { get; }

    /// <summary>The pin store (the pins band's rows and their order).</summary>
    SidebarPinStore Pins { get; }

    /// <summary>Move a pin inside the pins band (a reorder drop).</summary>
    void MovePin(int from, int to);

    /// <summary>Unpin one pin (the pin row's button).</summary>
    void Unpin(string pinId, string name);

    /// <summary>A pin's display name.</summary>
    string PinName(string pinId);

    /// <summary>The projection's resolved row for an entity pin (its cover, its kind), or null until the projection has it.</summary>
    SidebarLibraryEntry? ResolvedPin(string pinId);

    /// <summary>The Reset menu (this layout, recorded; everything, confirmed), built at open time.</summary>
    ContextMenuModel? ResetMenu();

    /// <summary>A section header's menu, or null for a kind with none.</summary>
    Func<ContextMenuModel?>? HeaderMenu(string sectionId);

    /// <summary>The title and glyph of an app route key.</summary>
    (string Title, string Glyph) DestOf(string routeKey);

    /// <summary>The title of the Settings destination (the footer row the Outline carries).</summary>
    string SettingsTitle { get; }

    /// <summary>A section's title (the UI culture's).</summary>
    string SectionName(string sectionId);

    /// <summary>An item's title (the UI culture's).</summary>
    string ItemName(string itemId);
}

/// <summary>The props of <see cref="ViewKeys.ShellNavEdit"/>: the pane that is editing.</summary>
public sealed record NavEditProps
{
    internal NavEditProps(INavEditHost host) => Host = host;

    /// <summary>The pane the Outline edits.</summary>
    internal INavEditHost Host { get; }
}
