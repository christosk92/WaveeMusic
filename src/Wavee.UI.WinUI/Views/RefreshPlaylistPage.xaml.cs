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
using Windows.System;

namespace Wavee.UI.WinUI.Views;

public sealed partial class RefreshPlaylistPage : UserControl, IPageHostAware
{
    public RefreshPlaylistViewModel ViewModel { get; }

    public RefreshPlaylistPage()
    {
        ViewModel = Ioc.Default.GetRequiredService<RefreshPlaylistViewModel>();
        InitializeComponent();
    }

    bool IPageHostAware.ShouldCacheInHost => false;   // one-shot wizard — dispose on leave so Teardown runs

    public void OnEntered(object? parameter, PageHostNavigationMode mode)
    {
        if (parameter is RefreshPlaylistParameter p)
            _ = ViewModel.LoadAsync(p);
        Focus(FocusState.Programmatic);
    }

    public void OnLeaving() => ViewModel.Teardown();

    // ── x:Bind function-binding helpers ──
    public Visibility ToVis(bool b) => b ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ToVisObj(object? o) => o is null ? Visibility.Collapsed : Visibility.Visible;
    public string PlayPauseGlyph(SwipePreviewState s) => s == SwipePreviewState.Playing ? FluentGlyphs.Pause : FluentGlyphs.Play;
    public static Visibility KeptVis(SwipeDecision? d) => d == SwipeDecision.Keep ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility RemovedVis(SwipeDecision? d) => d == SwipeDecision.Remove ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CountVis(int n) => n > 0 ? Visibility.Visible : Visibility.Collapsed;

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
