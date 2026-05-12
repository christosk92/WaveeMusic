using System;
using Microsoft.UI.Xaml.Controls;
using Wavee.UI.WinUI.Data.DTOs;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Views;

public sealed partial class LikedSongsView : UserControl, IDisposable
{
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

        // Load is idempotent (guarded in the VM); called once on first creation.
        _ = ViewModel.LoadCommand.ExecuteAsync(null);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        TrackGrid.DateAddedFormatter = null;
    }
}
