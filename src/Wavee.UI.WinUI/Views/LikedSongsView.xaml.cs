using System;
using Microsoft.UI.Xaml.Controls;
using Wavee.UI.Models;
using Wavee.UI.WinUI.Controls.InPageFilter;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Views;

public sealed partial class LikedSongsView : UserControl, IDisposable, IInPageFilterable
{
    // ── IInPageFilterable ───────────────────────────────────────────────
    string IInPageFilterable.FilterQuery
    {
        get => ViewModel?.SearchQuery ?? string.Empty;
        set { if (ViewModel is { } vm) vm.SearchQuery = value ?? string.Empty; }
    }
    string IInPageFilterable.FilterPlaceholder => "Filter liked songs…";
    bool IInPageFilterable.CanFilter => ViewModel is not null;


    public LikedSongsViewModel ViewModel { get; }
    private bool _disposed;

    public LikedSongsView(LikedSongsViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();

        // Without the formatter wired the visible Date Added column on the
        // Liked Songs page renders empty cells.
        TrackGrid.DateAddedFormatter = item =>
            item is LikedSongDto song ? song.AddedAtFormatted : "";
        SelectionBar.Attach(TrackGrid);

        // Load is idempotent (guarded in the VM); called once on first creation.
        _ = ViewModel.LoadCommand.ExecuteAsync(null);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        SelectionBar.Detach();
        TrackGrid.DateAddedFormatter = null;
        TrackGrid.Dispose();
    }
}
