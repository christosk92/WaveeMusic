using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Microsoft.UI.Dispatching;
using Serilog.Core;
using Serilog.Events;

namespace Wavee.UI.WinUI.Services;

/// <summary>
/// A log entry captured by <see cref="InMemorySink"/>.
/// </summary>
public sealed class LogEntry
{
    public DateTimeOffset Timestamp { get; init; }
    public LogEventLevel Level { get; init; }
    public string Category { get; init; } = "";
    public string Message { get; init; } = "";
    public string? Exception { get; init; }

    public string TimeString => Timestamp.LocalDateTime.ToString("HH:mm:ss.fff");

    public string LevelShort => Level switch
    {
        LogEventLevel.Verbose => "VRB",
        LogEventLevel.Debug => "DBG",
        LogEventLevel.Information => "INF",
        LogEventLevel.Warning => "WRN",
        LogEventLevel.Error => "ERR",
        LogEventLevel.Fatal => "FTL",
        _ => "???"
    };
}

/// <summary>
/// Serilog sink that captures log events to an in-memory ring buffer.
/// Entries are dispatched to the UI thread for safe ObservableCollection binding.
/// </summary>
public sealed class InMemorySink : ILogEventSink
{
    private const int MaxEntries = 2000;

    private readonly ObservableCollection<LogEntry> _entries = [];
    private readonly DispatcherQueue? _dispatcherQueue;
    private readonly object _pendingLock = new();
    private readonly List<LogEntry> _pendingEntries = [];
    private bool _flushQueued;

    public ObservableCollection<LogEntry> Entries => _entries;

    public InMemorySink(DispatcherQueue? dispatcherQueue = null)
    {
        _dispatcherQueue = dispatcherQueue;
    }

    public void Emit(LogEvent logEvent)
    {
        if (!AppFeatureFlags.DiagnosticsEnabled)
            return;

        var entry = new LogEntry
        {
            Timestamp = logEvent.Timestamp,
            Level = logEvent.Level,
            Category = logEvent.Properties.TryGetValue("SourceContext", out var ctx)
                ? SimplifyCategory(ctx.ToString().Trim('"'))
                : "",
            Message = logEvent.RenderMessage(),
            Exception = logEvent.Exception?.ToString()
        };

        if (_dispatcherQueue == null)
        {
            AddEntry(entry);
            return;
        }

        lock (_pendingLock)
        {
            _pendingEntries.Add(entry);
            if (_pendingEntries.Count > MaxEntries)
                _pendingEntries.RemoveRange(0, _pendingEntries.Count - MaxEntries);

            if (_flushQueued)
                return;

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
            _dispatcherQueue.TryEnqueue(_entries.Clear);
        else
            _entries.Clear();
    }

    private void FlushPendingEntries()
    {
        List<LogEntry> batch;
        lock (_pendingLock)
        {
            batch = [.. _pendingEntries];
            _pendingEntries.Clear();
            _flushQueued = false;
        }

        foreach (var entry in batch)
            AddEntry(entry);
    }

    private void AddEntry(LogEntry entry)
    {
        _entries.Add(entry);
        while (_entries.Count > MaxEntries)
            _entries.RemoveAt(0);
    }

    private static string SimplifyCategory(string category)
    {
        var lastDot = category.LastIndexOf('.');
        return lastDot >= 0 ? category[(lastDot + 1)..] : category;
    }
}
