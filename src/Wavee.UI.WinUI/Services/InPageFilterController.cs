using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Wavee.UI.WinUI.Controls.InPageFilter;

namespace Wavee.UI.WinUI.Services;

/// <summary>
/// Singleton orchestrator behind the shared Ctrl+F in-page filter overlay.
/// ShellPage calls <see cref="RequestFilter"/> on Ctrl+F (passing the
/// currently active page); the overlay control binds to
/// <see cref="IsActive"/> and <see cref="Query"/>.
///
/// The controller does NOT debounce — typing pushes into
/// <see cref="IInPageFilterable.FilterQuery"/> synchronously and each page's
/// VM debounces internally (the canonical 200 ms <c>DispatcherTimer</c>
/// already in <see cref="Wavee.UI.WinUI.ViewModels.LikedSongsViewModel"/>
/// and friends). Two debouncers would race.
/// </summary>
public sealed class InPageFilterController : INotifyPropertyChanged
{
    private IInPageFilterable? _currentTarget;
    private bool _isActive;
    private string _query = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised when the bar transitions from hidden to visible so
    /// the overlay can move keyboard focus into its input. Subscribe in the
    /// overlay's Loaded handler, unsubscribe in Unloaded.</summary>
    public event EventHandler? RequestFocusInput;

    public IInPageFilterable? CurrentTarget
    {
        get => _currentTarget;
        private set => SetField(ref _currentTarget, value);
    }

    /// <summary>Drives the overlay's <c>Visibility</c>. Bound one-way from
    /// the overlay's root border.</summary>
    public bool IsActive
    {
        get => _isActive;
        private set => SetField(ref _isActive, value);
    }

    /// <summary>Two-way bound to the overlay's <c>AutoSuggestBox.Text</c>.
    /// Setter pushes into <see cref="CurrentTarget"/>.<see cref="IInPageFilterable.FilterQuery"/>.</summary>
    public string Query
    {
        get => _query;
        set
        {
            value ??= string.Empty;
            if (_query == value) return;
            _query = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Query)));
            if (_currentTarget is { } t) t.FilterQuery = value;
        }
    }

    /// <summary>Called by ShellPage when navigation lands on a new page.
    /// Switches the bound target and hides the bar if it was open. The
    /// previous page's <c>FilterQuery</c> is NOT cleared — when the user
    /// returns to that page via back-nav (LRU cache keeps the VM alive),
    /// the previous filter is still applied and a fresh Ctrl+F press
    /// reopens the bar with that query pre-filled.</summary>
    public void OnPageChanged(IInPageFilterable? newTarget)
    {
        if (ReferenceEquals(_currentTarget, newTarget) && newTarget is not null) return;

        CurrentTarget = newTarget;
        if (_isActive)
        {
            _query = string.Empty;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Query)));
            IsActive = false;
        }
    }

    /// <summary>Ctrl+F handler. Reveals the overlay against the currently
    /// active page (already pushed via <see cref="OnPageChanged"/>) and
    /// asks the overlay to focus its input. No-op if the active page
    /// isn't filterable.</summary>
    public void RequestFilter()
    {
        if (_currentTarget is null || !_currentTarget.CanFilter) return;
        if (!_isActive)
        {
            _query = _currentTarget.FilterQuery ?? string.Empty;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Query)));
            IsActive = true;
        }
        RequestFocusInput?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Dismiss the bar (Esc, X, page nav). Clears the query and
    /// invokes <see cref="IInPageFilterable.OnFilterClosed"/> on the
    /// current target.</summary>
    public void Hide()
    {
        if (_currentTarget is { } t) t.OnFilterClosed();
        _query = string.Empty;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Query)));
        IsActive = false;
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (System.Collections.Generic.EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
