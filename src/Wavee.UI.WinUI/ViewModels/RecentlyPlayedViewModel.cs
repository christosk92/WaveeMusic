using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using Wavee.UI.WinUI.Services;

namespace Wavee.UI.WinUI.ViewModels;

/// <summary>
/// Thin VM for the dedicated Recently Played page. Wraps
/// <see cref="RecentlyPlayedService"/>'s items (already
/// <see cref="HomeSectionItem"/>-typed so the same ContentCard binding works)
/// in an <see cref="ObservableCollection{T}"/> that the page binds against.
/// The service publishes <see cref="RecentlyPlayedService.ItemsChanged"/>
/// every time the Home GraphQL response refreshes or a live-play bumps a
/// context to position 0; this VM mirrors that into the observable collection.
/// </summary>
public sealed partial class RecentlyPlayedViewModel : ObservableObject, IDisposable
{
    private readonly RecentlyPlayedService _service;
    private readonly DispatcherQueue _dispatcher;
    private bool _disposed;

    public ObservableCollection<HomeSectionItem> Items { get; } = new();

    [ObservableProperty]
    private bool _hasItems;

    public RecentlyPlayedViewModel(RecentlyPlayedService service)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _dispatcher = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("RecentlyPlayedViewModel must be constructed on a UI thread.");

        _service.ItemsChanged += OnItemsChanged;
        Sync();
    }

    private void OnItemsChanged()
    {
        if (_disposed) return;
        if (!_dispatcher.HasThreadAccess) _dispatcher.TryEnqueue(Sync);
        else Sync();
    }

    private void Sync()
    {
        if (_disposed) return;
        Items.Clear();
        foreach (var item in _service.Items)
            Items.Add(item);
        HasItems = Items.Count > 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _service.ItemsChanged -= OnItemsChanged;
    }
}
