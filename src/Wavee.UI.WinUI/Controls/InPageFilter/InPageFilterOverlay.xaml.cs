using System;
using System.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Wavee.UI.WinUI.Services;
using Windows.System;

namespace Wavee.UI.WinUI.Controls.InPageFilter;

/// <summary>
/// Floating Ctrl+F filter bar. Mounted once in <c>ShellPage</c> and binds
/// to the singleton <see cref="InPageFilterController"/>; the controller
/// is told who the active target is via ShellPage's nav handlers, and the
/// overlay just mirrors the controller's <c>IsActive</c> + <c>Query</c>.
/// </summary>
public sealed partial class InPageFilterOverlay : UserControl, INotifyPropertyChanged
{
    private InPageFilterController? _controller;
    private string _queryText = string.Empty;
    private string _placeholderText = "Filter…";

    public InPageFilterOverlay()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string QueryText
    {
        get => _queryText;
        set
        {
            value ??= string.Empty;
            if (_queryText == value) return;
            _queryText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(QueryText)));
            if (_controller is not null) _controller.Query = value;
        }
    }

    public string PlaceholderText
    {
        get => _placeholderText;
        private set
        {
            if (_placeholderText == value) return;
            _placeholderText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PlaceholderText)));
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_controller is not null) return;
        _controller = Ioc.Default.GetService<InPageFilterController>();
        if (_controller is null) return;
        _controller.PropertyChanged += OnControllerPropertyChanged;
        _controller.RequestFocusInput += OnRequestFocusInput;
        SyncFromController();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_controller is null) return;
        _controller.PropertyChanged -= OnControllerPropertyChanged;
        _controller.RequestFocusInput -= OnRequestFocusInput;
        _controller = null;
    }

    private void OnControllerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(SyncFromController);
    }

    private void SyncFromController()
    {
        if (_controller is null) return;

        // Visibility mirrors controller.IsActive — implicit animations
        // drive the show/hide transition automatically.
        Visibility = _controller.IsActive ? Visibility.Visible : Visibility.Collapsed;

        // Sync query text without re-firing the setter into the controller.
        var incoming = _controller.Query ?? string.Empty;
        if (_queryText != incoming)
        {
            _queryText = incoming;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(QueryText)));
        }

        PlaceholderText = _controller.CurrentTarget?.FilterPlaceholder ?? "Filter…";
    }

    private void OnRequestFocusInput(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            // Visibility flip needs a layout pass before focus can land —
            // post the focus call after the implicit show animation begins.
            QueryInput.Focus(FocusState.Programmatic);
            QueryInput.SelectAll();
        });
    }

    private void OnQueryInputKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            _controller?.Hide();
            e.Handled = true;
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        _controller?.Hide();
    }
}
