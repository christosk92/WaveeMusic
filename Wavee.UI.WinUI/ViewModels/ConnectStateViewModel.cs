using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wavee.Connect.Diagnostics;
using Wavee.UI.WinUI.Services;

namespace Wavee.UI.WinUI.ViewModels;

public sealed partial class ConnectStateViewModel : ObservableObject, IDisposable
{
    private readonly RemoteStateRecorder _recorder;
    private bool _disposed;

    public ObservableCollection<ConnectStateEventRow> FilteredEvents { get; } = [];

    [ObservableProperty] private bool _showPutStateRequest = true;
    [ObservableProperty] private bool _showPutStateResponse = true;
    [ObservableProperty] private bool _showClusterUpdate = true;
    [ObservableProperty] private bool _showVolumeCommand = true;
    [ObservableProperty] private bool _showDealerCommand = true;
    [ObservableProperty] private bool _showDealerReply = true;
    [ObservableProperty] private bool _showDealerLifecycle = true;
    [ObservableProperty] private bool _showConnectionLifecycle = true;
    [ObservableProperty] private bool _showSubscriptions = true;
    [ObservableProperty] private bool _showSelfEcho;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private bool _isPaused;

    public ConnectStateViewModel(RemoteStateRecorder recorder)
    {
        _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
        _recorder.Entries.CollectionChanged += OnSourceChanged;
        Rebuild();
    }

    partial void OnIsPausedChanged(bool value) => _recorder.Paused = value;
    partial void OnShowPutStateRequestChanged(bool value) => Rebuild();
    partial void OnShowPutStateResponseChanged(bool value) => Rebuild();
    partial void OnShowClusterUpdateChanged(bool value) => Rebuild();
    partial void OnShowVolumeCommandChanged(bool value) => Rebuild();
    partial void OnShowDealerCommandChanged(bool value) => Rebuild();
    partial void OnShowDealerReplyChanged(bool value) => Rebuild();
    partial void OnShowDealerLifecycleChanged(bool value) => Rebuild();
    partial void OnShowConnectionLifecycleChanged(bool value) => Rebuild();
    partial void OnShowSubscriptionsChanged(bool value) => Rebuild();
    partial void OnShowSelfEchoChanged(bool value) => Rebuild();
    partial void OnSearchTextChanged(string value) => Rebuild();

    [RelayCommand]
    private void Clear()
    {
        _recorder.Clear();
        FilteredEvents.Clear();
    }

    private void OnSourceChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_disposed) return;

        if (e.Action == NotifyCollectionChangedAction.Add
            && e.NewStartingIndex == 0
            && e.NewItems is { Count: > 0 })
        {
            for (var i = e.NewItems.Count - 1; i >= 0; i--)
            {
                if (e.NewItems[i] is not RemoteStateEvent evt) continue;
                if (!MatchesKind(evt) || !MatchesSearch(evt, SearchText.Trim())) continue;
                FilteredEvents.Insert(0, new ConnectStateEventRow(evt, SearchText.Trim()));
            }

            return;
        }

        if (e.Action == NotifyCollectionChangedAction.Remove
            && e.OldItems is { Count: > 0 })
        {
            foreach (var old in e.OldItems)
            {
                if (old is not RemoteStateEvent evt) continue;
                var row = FilteredEvents.FirstOrDefault(r => ReferenceEquals(r.Event, evt));
                if (row != null)
                    FilteredEvents.Remove(row);
            }

            return;
        }

        Rebuild();
    }

    private void Rebuild()
    {
        if (_disposed) return;

        FilteredEvents.Clear();
        var search = SearchText.Trim();

        foreach (var evt in _recorder.Entries)
        {
            if (!MatchesKind(evt)) continue;
            if (!MatchesSearch(evt, search)) continue;
            FilteredEvents.Add(new ConnectStateEventRow(evt, search));
        }
    }

    private bool MatchesKind(RemoteStateEvent evt) => evt.Kind switch
    {
        RemoteStateEventKind.PutStateRequest => ShowPutStateRequest,
        RemoteStateEventKind.PutStateResponse => ShowPutStateResponse,
        RemoteStateEventKind.ClusterUpdate => ShowClusterUpdate,
        RemoteStateEventKind.PutStateResponseEcho => ShowSelfEcho,
        RemoteStateEventKind.VolumeCommand => ShowVolumeCommand,
        RemoteStateEventKind.DealerCommand => ShowDealerCommand,
        RemoteStateEventKind.DealerReply => ShowDealerReply,
        RemoteStateEventKind.DealerLifecycle => ShowDealerLifecycle,
        RemoteStateEventKind.ConnectionIdAcquired => ShowConnectionLifecycle,
        RemoteStateEventKind.ConnectionIdChanged => ShowConnectionLifecycle,
        RemoteStateEventKind.SubscriptionRegistered => ShowSubscriptions,
        _ => true,
    };

    private static bool MatchesSearch(RemoteStateEvent evt, string search)
    {
        if (string.IsNullOrEmpty(search)) return true;

        return Contains(evt.Summary, search)
               || Contains(evt.CorrelationId, search)
               || Contains(evt.JsonBody, search)
               || Contains(evt.Notes, search)
               || (evt.Headers?.Any(kv => Contains(kv.Key, search) || Contains(kv.Value, search)) ?? false);
    }

    private static bool Contains(string? value, string search)
    {
        return value?.Contains(search, StringComparison.OrdinalIgnoreCase) == true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _recorder.Entries.CollectionChanged -= OnSourceChanged;
    }
}

public sealed partial class ConnectStateEventRow : ObservableObject
{
    public ConnectStateEventRow(RemoteStateEvent evt, string searchText)
    {
        Event = evt;
        SearchText = searchText;
    }

    [ObservableProperty] private bool _isExpanded;

    public RemoteStateEvent Event { get; }
    public string SearchText { get; }
    public string TimeString => Event.Timestamp.LocalDateTime.ToString("HH:mm:ss.fff");
    public string KindText => Event.Kind.ToString();
    public string DirectionGlyph => Event.Direction switch
    {
        RemoteStateDirection.Outbound => "OUT",
        RemoteStateDirection.Inbound => "IN",
        _ => "INT",
    };

    public string PayloadBytesDisplay => Event.PayloadBytes is { } bytes
        ? FormatBytes(bytes)
        : string.Empty;

    public string ElapsedDisplay => Event.ElapsedMs is { } elapsed
        ? $"{elapsed} ms"
        : string.Empty;

    public bool HasNotes => !string.IsNullOrWhiteSpace(Event.Notes);
    public bool HasJsonBody => !string.IsNullOrWhiteSpace(Event.JsonBody);
    public bool HasHeaders => Event.Headers is { Count: > 0 };
    public string Summary => Event.Summary;
    public string? Notes => Event.Notes;
    public string? JsonBody => Event.JsonBody;

    public string HeadersText
    {
        get
        {
            if (Event.Headers is not { Count: > 0 } headers)
                return string.Empty;

            var sb = new StringBuilder();
            foreach (var (key, value) in headers.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
                sb.Append(key).Append(": ").AppendLine(value);
            return sb.ToString();
        }
    }

    private static string FormatBytes(int bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024d:0.0} KB";
        return $"{bytes / 1024d / 1024d:0.0} MB";
    }
}
