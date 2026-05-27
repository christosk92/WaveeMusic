using System;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using Wavee.UI.Contracts;
using Wavee.UI.WinUI.Services;

namespace Wavee.UI.WinUI.ViewModels.Home;

/// <summary>
/// Owns the greeting band's text + user identity state: time-of-day greeting,
/// canned subtitle, and the resolved current-user display name / avatar URL
/// pulled from <see cref="IAuthState"/>.
///
/// <para>This child does NOT own the hero card / featured item — that lives on
/// <see cref="HomeRecommendationsViewModel"/> because it's driven by the
/// recently-played service. Hero palette / backdrop brushes live on the parent
/// (they cross greeting + featured concerns and feed the carousel-driven page
/// bleed too).</para>
/// </summary>
public sealed partial class HomeGreetingViewModel : ObservableObject, IDisposable
{
    private readonly IAuthState? _authState;
    private readonly DispatcherQueue _dispatcherQueue;
    private bool _disposed;
    private bool _attached;

    [ObservableProperty]
    private string _text = AppLocalization.GetString("Home_Greeting_Morning");

    [ObservableProperty]
    private string _subtitle = AppLocalization.GetString("Home_Greeting_Subtitle");

    public HomeGreetingViewModel(IAuthState? authState)
    {
        _authState = authState;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
    }

    /// <summary>Resolved current-user display name. Mirrors <see cref="IAuthState.DisplayName"/>
    /// with a fallback to <see cref="IAuthState.Username"/>.</summary>
    public string? CurrentUserName => _authState?.DisplayName ?? _authState?.Username;

    /// <summary>Resolved current-user avatar URL — surfaces
    /// <see cref="IAuthState.ProfileImageUrl"/>.</summary>
    public string? CurrentUserAvatarUrl => _authState?.ProfileImageUrl;

    /// <summary>
    /// Wire up to <see cref="IAuthState.PropertyChanged"/> so the bound
    /// greeting band re-paints when the user signs in / changes avatar / etc.
    /// Idempotent — safe to call multiple times.
    /// </summary>
    public void AttachAuthListener()
    {
        if (_attached || _authState is null) return;
        _attached = true;
        _authState.PropertyChanged += OnAuthStatePropertyChanged;
    }

    public void DetachAuthListener()
    {
        if (!_attached || _authState is null) return;
        _attached = false;
        _authState.PropertyChanged -= OnAuthStatePropertyChanged;
    }

    private void OnAuthStatePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed) return;
        if (e.PropertyName is nameof(IAuthState.CurrentUser)
                           or nameof(IAuthState.DisplayName)
                           or nameof(IAuthState.Username)
                           or nameof(IAuthState.ProfileImageUrl))
        {
            _dispatcherQueue.TryEnqueue(() =>
            {
                if (_disposed) return;
                OnPropertyChanged(nameof(CurrentUserName));
                OnPropertyChanged(nameof(CurrentUserAvatarUrl));
            });
        }
    }

    /// <summary>
    /// Set <see cref="Text"/> to a time-of-day greeting unless an upstream
    /// home-feed snapshot already supplied one. Called from parent load
    /// orchestration paths when the server greeting is empty.
    /// </summary>
    public void UpdateGreetingFromTimeOfDay()
    {
        var hour = DateTime.Now.Hour;
        Text = hour switch
        {
            < 12 => AppLocalization.GetString("Home_Greeting_Morning"),
            < 18 => AppLocalization.GetString("Home_Greeting_Afternoon"),
            _ => AppLocalization.GetString("Home_Greeting_Evening")
        };
    }

    /// <summary>
    /// Apply a greeting string sourced from the home-feed snapshot. Falls back
    /// to the current <see cref="Text"/> when the snapshot value is null/empty.
    /// </summary>
    public void ApplyGreetingFromSnapshot(string? snapshotGreeting)
    {
        if (!string.IsNullOrEmpty(snapshotGreeting))
            Text = snapshotGreeting;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DetachAuthListener();
    }
}
