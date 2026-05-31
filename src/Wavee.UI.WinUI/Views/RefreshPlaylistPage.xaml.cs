using System;
using System.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Wavee.UI.Services.Playlists;
using Wavee.UI.WinUI.Controls.PageHost;
using Wavee.UI.WinUI.Controls.Swipe;
using Wavee.UI.WinUI.Data.Parameters;
using Wavee.UI.WinUI.Styles;
using Wavee.UI.WinUI.ViewModels;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.System;

namespace Wavee.UI.WinUI.Views;

public sealed partial class RefreshPlaylistPage : UserControl, IPageHostAware
{
    private MediaPlayer? _canvasPlayer;

    public RefreshPlaylistViewModel ViewModel { get; }

    public RefreshPlaylistPage()
    {
        ViewModel = Ioc.Default.GetRequiredService<RefreshPlaylistViewModel>();
        InitializeComponent();
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
    }

    bool IPageHostAware.ShouldCacheInHost => false;   // one-shot wizard — dispose on leave so Teardown runs

    public void OnEntered(object? parameter, PageHostNavigationMode mode)
    {
        if (parameter is RefreshPlaylistParameter p)
            _ = ViewModel.LoadAsync(p);
        Focus(FocusState.Programmatic);
    }

    public void OnLeaving()
    {
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        TeardownCanvas();
        ViewModel.Teardown();
    }

    // ── x:Bind function-binding helpers ──
    public Visibility ToVis(bool b) => b ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ToVisObj(object? o) => o is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility StrVis(string? s) => string.IsNullOrEmpty(s) ? Visibility.Collapsed : Visibility.Visible;
    public string PlayPauseGlyph(SwipePreviewState s) => s == SwipePreviewState.Playing ? FluentGlyphs.Pause : FluentGlyphs.Play;
    public static Visibility KeptVis(SwipeDecision? d) => d == SwipeDecision.Keep ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility RemovedVis(SwipeDecision? d) => d == SwipeDecision.Remove ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CountVis(int n) => n > 0 ? Visibility.Visible : Visibility.Collapsed;

    // ── Artist Canvas video (looping, muted) ──
    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RefreshPlaylistViewModel.SpotlightCanvasUrl))
            UpdateCanvas(ViewModel.SpotlightCanvasUrl);
    }

    private void UpdateCanvas(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            if (_canvasPlayer is not null) { _canvasPlayer.Pause(); _canvasPlayer.Source = null; }
            return;
        }
        try
        {
            if (_canvasPlayer is null)
            {
                _canvasPlayer = new MediaPlayer { IsLoopingEnabled = true, IsMuted = true };
                CanvasVideo.SetMediaPlayer(_canvasPlayer);
            }
            _canvasPlayer.Source = MediaSource.CreateFromUri(new Uri(url));
            _canvasPlayer.Play();
        }
        catch { /* Canvas is decorative — never let a bad URL break the page */ }
    }

    private void TeardownCanvas()
    {
        if (_canvasPlayer is null) return;
        try { _canvasPlayer.Pause(); CanvasVideo?.SetMediaPlayer(null); _canvasPlayer.Dispose(); } catch { }
        _canvasPlayer = null;
    }

    private void Page_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!ViewModel.IsAuditioning || ViewModel.ShowHowItWorks) return;
        switch (e.Key)
        {
            case VirtualKey.Left: e.Handled = true; TopCard.CommitDecision(SwipeDirection.Left); break;
            case VirtualKey.Right: e.Handled = true; TopCard.CommitDecision(SwipeDirection.Right); break;
            case VirtualKey.Space: e.Handled = true; ViewModel.ToggleSnippetCommand.Execute(null); break;
        }
    }

    private void TopCard_DecisionCommitted(object? sender, SwipeDirection dir)
    {
        ViewModel.CommitInDirection(dir);   // advances synchronously on the UI thread
        TopCard.ResetVisual();
        if (ViewModel.IsAuditioning) TopCard.AnimateEnter();
    }

    private void Remove_Click(object sender, RoutedEventArgs e) => TopCard.CommitDecision(SwipeDirection.Left);
    private void Keep_Click(object sender, RoutedEventArgs e) => TopCard.CommitDecision(SwipeDirection.Right);
    private void Finish_Click(object sender, RoutedEventArgs e) => ViewModel.Finish();
    private void Exit_Click(object sender, RoutedEventArgs e) => ViewModel.ViewPlaylistCommand.Execute(null);

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.UndoLast();
        TopCard.ResetVisual();
        if (ViewModel.IsAuditioning) TopCard.AnimateEnter();
    }

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.SkipCurrent();
        TopCard.ResetVisual();
        if (ViewModel.IsAuditioning) TopCard.AnimateEnter();
    }

    private void UnRemove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { CommandParameter: string uri })
            ViewModel.UnRemoveCommand.Execute(uri);
    }
}
