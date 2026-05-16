using System.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Xaml;
using Wavee.UI.Services.AddToPlaylist;

namespace Wavee.UI.WinUI.Views;

/// <summary>
/// Partial-class extension for <see cref="ShellPage"/> that nudges the
/// floating <c>NotificationToast</c> upward while an
/// <see cref="IAddToPlaylistSession"/> is active. Both surfaces share the
/// same bottom-anchored Row=2 slot — without the lift, the toast renders
/// behind the (taller, higher Canvas.ZIndex) add-to-playlist bar.
/// </summary>
public sealed partial class ShellPage
{
    // Default toast margin: matches the XAML "0,0,0,16" — keep in sync.
    private static readonly Thickness ToastDefaultMargin = new(0, 0, 0, 16);
    // Lifted: bar minHeight (~56) + bar bottom margin (16) + a small gap.
    private static readonly Thickness ToastLiftedMargin = new(0, 0, 0, 88);

    private IAddToPlaylistSession? _addToPlaylistSession;
    private bool _isAddToPlaylistSessionHooked;

    /// <summary>Called once from <c>ShellPage_Loaded</c>. Idempotent.</summary>
    private void HookAddToPlaylistSessionForToast()
    {
        if (_isAddToPlaylistSessionHooked) return;
        _addToPlaylistSession = Ioc.Default.GetService<IAddToPlaylistSession>();
        if (_addToPlaylistSession is null) return;
        _addToPlaylistSession.PropertyChanged += OnAddToPlaylistSessionPropertyChanged;
        _isAddToPlaylistSessionHooked = true;
        ApplyToastMargin();
    }

    private void OnAddToPlaylistSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(IAddToPlaylistSession.IsActive)) return;
        if (DispatcherQueue?.HasThreadAccess == true)
            ApplyToastMargin();
        else
            DispatcherQueue?.TryEnqueue(ApplyToastMargin);
    }

    private void ApplyToastMargin()
    {
        if (NotificationToastControl is null || _addToPlaylistSession is null) return;
        NotificationToastControl.Margin = _addToPlaylistSession.IsActive
            ? ToastLiftedMargin
            : ToastDefaultMargin;
    }
}
