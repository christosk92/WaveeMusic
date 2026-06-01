using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Wavee.UI.WinUI.Controls.Library;

/// <summary>
/// One declarative action rendered in the shared <see cref="LibraryDetailPanel"/>
/// action row. Replaces the per-tab hand-rolled <c>WrapPanel</c> of buttons —
/// each tab's ViewModel exposes a stable <c>ObservableCollection&lt;LibraryDetailAction&gt;</c>
/// and mutates <see cref="IsChecked"/> / <see cref="IsVisible"/> / <see cref="Label"/>
/// in place (never swaps the collection, to avoid COMException on selection change).
/// </summary>
public sealed partial class LibraryDetailAction : ObservableObject
{
    /// <summary>Visible label (omitted for icon-only actions).</summary>
    [ObservableProperty]
    public partial string? Label { get; set; }

    /// <summary>Segoe Fluent Icons glyph (set from <c>FluentGlyphs</c> constants).</summary>
    [ObservableProperty]
    public partial string? Glyph { get; set; }

    /// <summary>Two-way checked state for toggle actions (Following / Saved only).</summary>
    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    /// <summary>When false the action is hidden without rebuilding the collection.</summary>
    [ObservableProperty]
    public partial bool IsVisible { get; set; } = true;

    /// <summary>Command invoked on click (or on toggle for toggle actions).</summary>
    public ICommand? Command { get; init; }

    /// <summary>Optional parameter; defaults to the action itself for toggle handlers.</summary>
    public object? CommandParameter { get; init; }

    /// <summary>Render as the accent primary button.</summary>
    public bool IsAccent { get; init; }

    /// <summary>Render glyph-only (no label).</summary>
    public bool IsIconOnly { get; init; }

    /// <summary>Render as a toggle (accent fill when checked).</summary>
    public bool IsToggle { get; init; }

    /// <summary>Tint the glyph with the "liked" accent (filled heart).</summary>
    public bool LikeTint { get; init; }

    public string? Tooltip { get; init; }
}

/// <summary>
/// Picks the right action template (accent / pill / icon / toggle) for a
/// <see cref="LibraryDetailAction"/>. Templates are supplied from XAML.
/// </summary>
public sealed partial class LibraryActionTemplateSelector : DataTemplateSelector
{
    public DataTemplate? AccentTemplate { get; set; }
    public DataTemplate? DefaultTemplate { get; set; }
    public DataTemplate? IconTemplate { get; set; }
    public DataTemplate? ToggleTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item)
    {
        if (item is LibraryDetailAction a)
        {
            if (a.IsToggle) return ToggleTemplate;
            if (a.IsIconOnly) return IconTemplate;
            if (a.IsAccent) return AccentTemplate;
            return DefaultTemplate;
        }
        return DefaultTemplate;
    }

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container)
        => SelectTemplateCore(item);
}
