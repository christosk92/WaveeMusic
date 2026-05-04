using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.UI.Xaml.Input;

namespace Wavee.UI.WinUI.Controls.ContextMenu;

public enum ContextMenuItemType
{
    Item,
    Toggle,
    Separator
}

/// <summary>
/// Flat view model for a context-menu entry. Items with <see cref="IsPrimary"/> become
/// icon-only buttons in the top row of a <see cref="Microsoft.UI.Xaml.Controls.CommandBarFlyout"/>;
/// the rest render as labeled rows in the dropdown. Nested <see cref="Items"/> become a sub-flyout.
/// Pattern adapted from Files' ContextMenuFlyoutItemViewModel.
/// </summary>
public sealed class ContextMenuItemModel
{
    public string? Text { get; init; }
    public string? Glyph { get; init; }
    public string? GlyphFontFamilyName { get; init; }

    /// <summary>
    /// Optional resource key for an <c>AccentIcon</c> style (e.g. <c>App.AccentIcons.Media.Play</c>).
    /// When set, primary rows render a two-layer colored icon instead of a monochrome
    /// <see cref="Glyph"/>. Glyph still populates so fallback surfaces render something.
    /// </summary>
    public string? AccentIconStyleKey { get; init; }

    public ICommand? Command { get; init; }
    public object? CommandParameter { get; init; }
    public Action? Invoke { get; init; }

    public KeyboardAccelerator? KeyboardAccelerator { get; init; }
    public string? KeyboardAcceleratorTextOverride { get; init; }

    public ContextMenuItemType ItemType { get; init; } = ContextMenuItemType.Item;

    /// <summary>
    /// When true, this item is rendered as an icon-only button in the top row.
    /// </summary>
    public bool IsPrimary { get; init; }

    public bool IsChecked { get; init; }
    public bool IsEnabled { get; init; } = true;
    public bool ShowItem { get; init; } = true;

    /// <summary>
    /// When true, the item gets a destructive visual treatment (red on hover).
    /// </summary>
    public bool IsDestructive { get; init; }

    /// <summary>
    /// Sub-menu children. When non-null the item renders with a chevron and opens a nested flyout.
    /// </summary>
    public IReadOnlyList<ContextMenuItemModel>? Items { get; init; }

    /// <summary>
    /// Optional lazy loader. If set, it runs the first time the sub-menu is opened so callers can
    /// populate children from a service (e.g. the user's playlists) on demand.
    /// </summary>
    public Func<Task<IReadOnlyList<ContextMenuItemModel>>>? LoadSubMenuAsync { get; init; }

    public static ContextMenuItemModel Separator { get; } = new()
    {
        ItemType = ContextMenuItemType.Separator
    };
}
