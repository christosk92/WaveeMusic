using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.Json;
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
        if (string.IsNullOrEmpty(body))
            return evt;

        body = TrimLargeJsonFields(body);
        if (body.Length <= MaxJsonBodyChars && ReferenceEquals(body, evt.JsonBody))
            return evt;

        if (body.Length <= MaxJsonBodyChars)
            return evt with { JsonBody = body };

        return evt with
        {
            JsonBody = body[..MaxJsonBodyChars]
                       + $"{Environment.NewLine}{Environment.NewLine}... [truncated, original was {body.Length:N0} chars]"
        };
    }

    private static string TrimLargeJsonFields(string body)
    {
        if (!body.Contains("transferData", StringComparison.Ordinal)
            && !body.Contains("transfertransferData", StringComparison.Ordinal))
        {
            return body;
        }

        try
        {
            var utf8 = Encoding.UTF8.GetBytes(body);
            var reader = new Utf8JsonReader(utf8, new JsonReaderOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });

            using var stream = new MemoryStream(Math.Min(utf8.Length, MaxJsonBodyChars));
            using (var writer = new Utf8JsonWriter(stream))
            {
                while (reader.Read())
                    CopyToken(ref reader, writer);
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }
        catch
        {
            return body;
        }
    }

    private static void CopyToken(ref Utf8JsonReader reader, Utf8JsonWriter writer)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.StartObject:
                writer.WriteStartObject();
                break;
            case JsonTokenType.EndObject:
                writer.WriteEndObject();
                break;
            case JsonTokenType.StartArray:
                writer.WriteStartArray();
                break;
            case JsonTokenType.EndArray:
                writer.WriteEndArray();
                break;
            case JsonTokenType.PropertyName:
                var propertyName = reader.GetString() ?? string.Empty;
                writer.WritePropertyName(propertyName);
                if (ShouldTrimJsonField(propertyName) && reader.Read())
                    WriteTrimmedJsonValue(ref reader, writer, propertyName);
                break;
            case JsonTokenType.String:
                writer.WriteStringValue(reader.GetString());
                break;
            case JsonTokenType.Number:
                if (reader.TryGetInt64(out var longValue))
                    writer.WriteNumberValue(longValue);
                else if (reader.TryGetDecimal(out var decimalValue))
                    writer.WriteNumberValue(decimalValue);
                else
                    writer.WriteNumberValue(reader.GetDouble());
                break;
            case JsonTokenType.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonTokenType.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonTokenType.Null:
                writer.WriteNullValue();
                break;
        }
    }

    private static bool ShouldTrimJsonField(string propertyName)
    {
        return propertyName.Equals("transferData", StringComparison.OrdinalIgnoreCase)
               || propertyName.Equals("transfertransferData", StringComparison.OrdinalIgnoreCase);
    }

    private static void WriteTrimmedJsonValue(ref Utf8JsonReader reader, Utf8JsonWriter writer, string propertyName)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var originalBytes = reader.HasValueSequence
                ? reader.ValueSequence.Length
                : reader.ValueSpan.Length;
            writer.WriteStringValue($"[trimmed {propertyName}, original {FormatBytes(originalBytes)}]");
            return;
        }

        if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
            reader.Skip();

        writer.WriteStringValue($"[trimmed {propertyName}]");
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes:N0} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024d:0.0} KB";
        return $"{bytes / 1024d / 1024d:0.0} MB";
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
