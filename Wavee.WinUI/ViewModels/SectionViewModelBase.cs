using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Wavee.WinUI.ViewModels;

/// <summary>
/// Base class for all home feed section ViewModels
/// </summary>
public abstract partial class SectionViewModelBase : ObservableObject
{
    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? Subtitle { get; set; }

    [ObservableProperty]
    public partial ObservableCollection<ContentItemViewModel> Items { get; set; }

    /// <summary>
    /// Indicates if this section should use a grid layout with horizontal cards
    /// instead of the default horizontal scrolling layout with vertical cards
    /// </summary>
    [ObservableProperty]
    public partial bool IsGridLayout { get; set; }

    /// <summary>
    /// Indicates if this section should use a uniform grid layout
    /// for displaying baseline section cards
    /// </summary>
    [ObservableProperty]
    public partial bool IsUniformGrid { get; set; }

    protected SectionViewModelBase()
    {
        Items = new();
    }
}
