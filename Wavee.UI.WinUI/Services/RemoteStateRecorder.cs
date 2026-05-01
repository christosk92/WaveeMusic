using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Microsoft.UI.Dispatching;
using Wavee.Connect.Diagnostics;

namespace Wavee.UI.WinUI.Services;

public sealed class RemoteStateRecorder : IRemoteStateRecorder
{
    private const int MaxEntries = 500;
    private const int MaxJsonBodyChars = 200_000;

    private readonly ObservableCollection<RemoteStateEvent> _entries = [];
    private readonly DispatcherQueue? _dispatcherQueue;
    private readonly object _pendingLock = new();
    private readonly List<RemoteStateEvent> _pendingEntries = [];
    private bool _flushQueued;

    public ObservableCollection<RemoteStateEvent> Entries => _entries;

    public bool Paused { get; set; }

    public RemoteStateRecorder(DispatcherQueue? dispatcherQueue = null)
    {
        _dispatcherQueue = dispatcherQueue;
    }

    public void Record(RemoteStateEvent evt)
    {
        if (Paused) return;

        evt = TrimPayload(evt);

        if (_dispatcherQueue == null)
        {
            AddEntry(evt);
            return;
        }

        lock (_pendingLock)
        {
            _pendingEntries.Add(evt);
            if (_pendingEntries.Count > MaxEntries)
                _pendingEntries.RemoveRange(0, _pendingEntries.Count - MaxEntries);

            if (_flushQueued) return;
            _flushQueued = true;
        }

        if (!_dispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, FlushPendingEntries))
        {
            lock (_pendingLock)
                _flushQueued = false;
        }
    }

    public void Clear()
    {
        lock (_pendingLock)
            _pendingEntries.Clear();

        if (_dispatcherQueue != null)
            _dispatcherQueue.TryEnqueue(() => _entries.Clear());
        else
            _entries.Clear();
    }

    private static RemoteStateEvent TrimPayload(RemoteStateEvent evt)
    {
        var body = evt.JsonBody;
        if (string.IsNullOrEmpty(body) || body.Length <= MaxJsonBodyChars)
            return evt;

        return evt with
        {
            JsonBody = body[..MaxJsonBodyChars]
                       + $"{Environment.NewLine}{Environment.NewLine}... [truncated, original was {body.Length:N0} chars]"
        };
    }

    private void FlushPendingEntries()
    {
        List<RemoteStateEvent> batch;
        lock (_pendingLock)
        {
            batch = [.. _pendingEntries];
            _pendingEntries.Clear();
            _flushQueued = false;
        }

        foreach (var entry in batch)
            AddEntry(entry);
    }

    private void AddEntry(RemoteStateEvent entry)
    {
        _entries.Insert(0, entry);
        while (_entries.Count > MaxEntries)
            _entries.RemoveAt(_entries.Count - 1);
    }
}
