using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Wavee.UI.WinUI.Data.Contracts;

namespace Wavee.UI.WinUI.ViewModels;

/// <summary>
/// Generic lazy-loading wrapper for items that may be loaded asynchronously.
/// Shows a shimmer placeholder until populated with real data.
/// Reusable across artist top tracks, album tracks, playlist tracks, etc.
/// </summary>
/// <typeparam name="T">The data model type (e.g. ArtistTopTrackVm, AlbumTrackDto).</typeparam>
public partial class LazyItemVm<T> : ObservableObject where T : class
{
    /// <summary>Unique key for the item (used by SourceCache keying).</summary>
    public required string Id { get; init; }

    /// <summary>Position index (1-based) for display numbering.</summary>
    [ObservableProperty]
    private int _index;

    /// <summary>Whether the real data has been loaded.</summary>
    [ObservableProperty]
    private bool _isLoaded;

    /// <summary>The real data. Null when not yet loaded (shimmer state).</summary>
    [ObservableProperty]
    private T? _data;

    /// <summary>Populate with real data. Automatically sets IsLoaded = true.</summary>
    public void Populate(T data)
    {
        Data = data;
        IsLoaded = true;
    }

    /// <summary>Create an already-loaded instance.</summary>
    public static LazyItemVm<T> Loaded(string id, int index, T data) => new()
    {
        Id = id,
        Index = index,
        Data = data,
        IsLoaded = true
    };

    /// <summary>Create a placeholder (shimmer) instance that will be populated later.</summary>
    public static LazyItemVm<T> Placeholder(string id, int index) => new()
    {
        Id = id,
        Index = index,
        IsLoaded = false
    };
}

/// <summary>
/// Lazy-loading wrapper for any ITrackItem. Implements ITrackItem itself so it can
/// be used directly in TrackListView's ItemsSource. When unloaded, returns safe defaults
/// and TrackListView shows shimmer rows. When Populate() is called, delegates to real data.
/// </summary>
public sealed partial class LazyTrackItem : ObservableObject, ITrackItem
{
    public required string Id { get; init; }

    [ObservableProperty] private int _index;
    [ObservableProperty] private bool _isLoaded;
    [ObservableProperty] private ITrackItem? _data;

    public void Populate(ITrackItem data)
    {
        Data = data;
        IsLoaded = true;
    }

    // Notify all delegated properties when Data changes so OneWay bindings refresh
    partial void OnDataChanged(ITrackItem? value)
    {
        OnPropertyChanged(nameof(Uri));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(ArtistName));
        OnPropertyChanged(nameof(ArtistId));
        OnPropertyChanged(nameof(AlbumName));
        OnPropertyChanged(nameof(AlbumId));
        OnPropertyChanged(nameof(ImageUrl));
        OnPropertyChanged(nameof(Duration));
        OnPropertyChanged(nameof(IsExplicit));
        OnPropertyChanged(nameof(DurationFormatted));
        OnPropertyChanged(nameof(IsLiked));
    }

    // ITrackItem — delegate to Data when loaded, safe defaults when not
    public string Uri => Data?.Uri ?? "";
    public string Title => Data?.Title ?? "";
    public string ArtistName => Data?.ArtistName ?? "";
    public string ArtistId => Data?.ArtistId ?? "";
    public string AlbumName => Data?.AlbumName ?? "";
    public string AlbumId => Data?.AlbumId ?? "";
    public string? ImageUrl => Data?.ImageUrl;
    public TimeSpan Duration => Data?.Duration ?? TimeSpan.Zero;
    public bool IsExplicit => Data?.IsExplicit ?? false;
    public string DurationFormatted => Data?.DurationFormatted ?? "";
    public int OriginalIndex => Index;
    public bool IsLiked
    {
        get => Data?.IsLiked ?? false;
        set { if (Data != null) Data.IsLiked = value; }
    }

    // Extra properties for custom columns (not on ITrackItem, accessed via reflection)
    public string PlayCountFormatted =>
        Data is Data.DTOs.AlbumTrackDto album ? album.PlayCountFormatted :
        Data is ArtistTopTrackVm artist ? artist.PlayCountFormatted : "";

    public static LazyTrackItem Loaded(string id, int index, ITrackItem data) => new()
    {
        Id = id, Index = index, Data = data, IsLoaded = true
    };

    public static LazyTrackItem Placeholder(string id, int index) => new()
    {
        Id = id, Index = index, IsLoaded = false
    };
}

/// <summary>
/// Non-generic wrapper for LazyItemVm&lt;ArtistReleaseVm&gt; for discography cards.
/// </summary>
public sealed partial class LazyReleaseItem : ObservableObject
{
    public required string Id { get; init; }

    [ObservableProperty] private int _index;
    [ObservableProperty] private bool _isLoaded;
    [ObservableProperty] private ArtistReleaseVm? _data;

    public void Populate(ArtistReleaseVm data)
    {
        Data = data;
        IsLoaded = true;
    }

    public static LazyReleaseItem Loaded(string id, int index, ArtistReleaseVm data) => new()
    {
        Id = id, Index = index, Data = data, IsLoaded = true
    };

    public static LazyReleaseItem Placeholder(string id, int index) => new()
    {
        Id = id, Index = index, IsLoaded = false
    };
}
