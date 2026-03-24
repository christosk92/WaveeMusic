using CommunityToolkit.Mvvm.ComponentModel;

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
/// Non-generic wrapper for LazyItemVm&lt;ArtistTopTrackVm&gt; so XAML x:DataType can reference it.
/// Use this in SourceCache and DataTemplates.
/// </summary>
public sealed partial class LazyTrackItem : ObservableObject
{
    public required string Id { get; init; }

    [ObservableProperty] private int _index;
    [ObservableProperty] private bool _isLoaded;
    [ObservableProperty] private ArtistTopTrackVm? _data;

    public void Populate(ArtistTopTrackVm data)
    {
        Data = data;
        IsLoaded = true;
    }

    public static LazyTrackItem Loaded(string id, int index, ArtistTopTrackVm data) => new()
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
