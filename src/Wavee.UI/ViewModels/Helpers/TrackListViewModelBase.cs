using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Wavee.UI.ViewModels.Helpers;

/// <summary>
/// Shared selection-state plumbing for the 6 ViewModels that drive a
/// TrackListView surface (PlaylistViewModel, AlbumViewModel,
/// LikedSongsViewModel, ArtistsLibraryViewModel, AlbumsLibraryViewModel,
/// ProfileViewModel). Extracted to remove the ~300 lines of duplicated
/// <c>SelectedItems</c> / <c>SelectedCount</c> / <c>HasSelection</c> /
/// <c>SelectionHeaderText</c> declarations that each carried independently.
///
/// <para>SelectedItems is typed as <see cref="IReadOnlyList{T}"/> of
/// <see cref="object"/> because the WinUI <c>ListView.SelectedItems</c>
/// collection hands back boxed object references — derived VMs use
/// <c>SelectedItems.OfType&lt;TDto&gt;()</c> to materialize typed access.</para>
///
/// <para>Sort-state plumbing is intentionally NOT shared: each surface uses a
/// different sort-column enum (PlaylistSortColumn, AlbumSortColumn, etc.) and
/// abstracting that costs more than it saves.</para>
/// </summary>
[global::WinRT.GeneratedBindableCustomProperty]
public abstract partial class TrackListViewModelBase : ObservableObject
{
    /// <summary>
    /// Currently-selected rows in the bound TrackListView. WinUI hands back
    /// boxed object refs; derived VMs cast back to their specific DTO type.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedCount))]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyPropertyChangedFor(nameof(SelectionHeaderText))]
    public partial IReadOnlyList<object> SelectedItems { get; set; } = Array.Empty<object>();

    /// <summary>Number of rows currently selected.</summary>
    public int SelectedCount => SelectedItems.Count;

    /// <summary>True when at least one row is selected.</summary>
    public bool HasSelection => SelectedItems.Count > 0;

    /// <summary>
    /// Header label for the selection-mode command bar. Default phrasing
    /// matches the existing tracks surface; override for surfaces that use
    /// a different noun (e.g. "episodes selected" on YourEpisodes).
    /// </summary>
    public virtual string SelectionHeaderText => SelectedCount == 1
        ? "1 track selected"
        : $"{SelectedCount} tracks selected";

    /// <summary>
    /// Hook for derived VMs that need to refresh <c>NotifyCanExecuteChanged</c>
    /// on selection-driven commands (Play / Add to queue / Add to playlist /
    /// etc.) when the selection changes. The base wires it via the source-gen
    /// partial method on <see cref="SelectedItems"/>; derived classes override
    /// without needing to know how the change is observed.
    /// </summary>
    protected virtual void OnSelectionChanged() { }

    partial void OnSelectedItemsChanged(IReadOnlyList<object> value)
    {
        OnSelectionChanged();
    }
}
