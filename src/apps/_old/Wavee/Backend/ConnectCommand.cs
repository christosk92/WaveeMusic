using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Wavee.Backend;

public enum ConnectCmd
{
    Unknown, Play, Pause, Resume, SeekTo, SkipNext, SkipPrev,
    SetShufflingContext, SetRepeatingContext, SetRepeatingTrack,
    Transfer, AddToQueue, SetQueue, UpdateContext, SetOptions,
}

public enum ConnectCommandOutcome { Applied, NoOp, Superseded, Failed }

/// <summary>
/// Parsed Dealer REQUEST envelope. The complete payload remains available for typed command-specific parsing; the
/// frequently needed routing and correlation values are extracted once at the boundary.
/// </summary>
public readonly record struct ConnectCommand(
    ConnectCmd Kind, string Endpoint, string Key, int MessageId, string SenderDeviceId,
    long SeekToMs, bool BoolArg, byte[] Payload,
    string TrackUri = "", string TrackUid = "",
    string SessionId = "", string CommandId = "")
{
    public static bool TryParse(in WireRequest req, out ConnectCommand cmd)
    {
        cmd = default;
        var parts = req.MessageIdent.Split('/');
        if (parts.Length < 5 || req.Command is null || req.Command.Length == 0) return false;

        // The URL's own last segment, known before the body is ever parsed. Stashed on `cmd` even on a later
        // failure so OnRequest can still ask "was this at least a known endpoint?" (findings §3) without redoing
        // this parse — a garbled BODY on a real endpoint must not read the same as a genuinely unsupported one.
        string urlEndpoint = parts[^1].ToLowerInvariant();
        cmd = cmd with { Endpoint = urlEndpoint };
        try
        {
            using var doc = JsonDocument.Parse(req.Command);
            var root = doc.RootElement;
            int messageId = root.TryGetProperty("message_id", out var mid) ? IntLoose(mid) : 0;
            string sender = root.TryGetProperty("sent_by_device_id", out var sd) && sd.ValueKind == JsonValueKind.String
                ? sd.GetString() ?? "" : "";

            JsonElement inner = root;
            string endpoint;
            if (urlEndpoint == "command" && parts.Length >= 6 && parts[^2] == "player")
            {
                if (!root.TryGetProperty("command", out inner) || !inner.TryGetProperty("endpoint", out var ep)
                    || ep.ValueKind != JsonValueKind.String) return false;
                endpoint = ep.GetString()?.ToLowerInvariant() ?? "";
            }
            else endpoint = urlEndpoint;

            var kind = Map(endpoint);
            cmd = cmd with { Endpoint = endpoint };
            if (kind == ConnectCmd.Unknown) return false;

            // From here the endpoint is KNOWN — a malformed inner field (a float where a string was expected, a
            // nested object gone missing) must skip just that field, not the whole command: a phone that sends one
            // odd payload must not have this reply DeviceDoesNotSupportCommand and get itself cached out of ever
            // sending the endpoint again (OnRequest). So this second try is scoped tightly around the optional
            // per-kind reads, and swallows into the safe defaults already declared below rather than aborting.
            long seekMs = 0;
            bool boolArg = false;
            string trackUri = "", trackUid = "";
            string sessionId = "", commandId = "";
            try
            {
                switch (kind)
                {
                    case ConnectCmd.SeekTo:
                        if (inner.TryGetProperty("position", out var pos)) seekMs = LongLoose(pos);
                        else if (inner.TryGetProperty("value", out var val)) seekMs = LongLoose(val);
                        break;
                    case ConnectCmd.SetShufflingContext:
                    case ConnectCmd.SetRepeatingContext:
                    case ConnectCmd.SetRepeatingTrack:
                        if (inner.TryGetProperty("value", out var bv) &&
                            bv.ValueKind is JsonValueKind.True or JsonValueKind.False)
                            boolArg = bv.GetBoolean();
                        break;
                    case ConnectCmd.SkipNext:
                        if (inner.TryGetProperty("track", out var trk) && trk.ValueKind == JsonValueKind.Object)
                        {
                            if (trk.TryGetProperty("uri", out var tu) && tu.ValueKind == JsonValueKind.String) trackUri = tu.GetString() ?? "";
                            if (trk.TryGetProperty("uid", out var td) && td.ValueKind == JsonValueKind.String) trackUid = td.GetString() ?? "";
                        }
                        break;
                }

                if (inner.TryGetProperty("session_id", out var sid) && sid.ValueKind == JsonValueKind.String)
                    sessionId = sid.GetString() ?? "";
                else if (inner.TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Object
                         && options.TryGetProperty("session_id", out sid) && sid.ValueKind == JsonValueKind.String)
                    sessionId = sid.GetString() ?? "";

                if (inner.TryGetProperty("logging_params", out var logging) && logging.ValueKind == JsonValueKind.Object
                    && logging.TryGetProperty("command_id", out var cid) && cid.ValueKind == JsonValueKind.String)
                    commandId = cid.GetString() ?? "";
            }
            catch { /* known endpoint, odd field — fall through with whatever defaults above were already collected */ }

            cmd = new ConnectCommand(kind, endpoint, req.RequestId, messageId, sender, seekMs, boolArg, req.Command,
                trackUri, trackUid, sessionId, commandId);
            return true;
        }
        catch { return false; }   // the envelope itself (JSON, url shape) is structurally unusable
    }

    internal static ConnectCmd Map(string endpoint) => endpoint switch
    {
        "play" => ConnectCmd.Play,
        "pause" => ConnectCmd.Pause,
        "resume" => ConnectCmd.Resume,
        "seek_to" => ConnectCmd.SeekTo,
        "skip_next" or "next_track" => ConnectCmd.SkipNext,
        "skip_prev" => ConnectCmd.SkipPrev,
        "set_shuffling_context" => ConnectCmd.SetShufflingContext,
        "set_repeating_context" => ConnectCmd.SetRepeatingContext,
        "set_repeating_track" => ConnectCmd.SetRepeatingTrack,
        "transfer" => ConnectCmd.Transfer,
        "add_to_queue" => ConnectCmd.AddToQueue,
        "set_queue" => ConnectCmd.SetQueue,
        "update_context" => ConnectCmd.UpdateContext,
        "set_options" => ConnectCmd.SetOptions,
        _ => ConnectCmd.Unknown,
    };

    // message_id is uint32 ON THE WIRE (up to ~4.29B) and routinely exceeds int.MaxValue — GetInt32() threw for
    // those, discarding the whole command via the outer catch. Read wide, then clamp; a genuinely non-numeric shape
    // (a float text token, whatever) falls back to the string parse, and failing that, 0.
    static int IntLoose(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Number)
        {
            long wide = element.TryGetInt64(out var l) ? l : (long)element.GetDouble();
            return (int)Math.Clamp(wide, int.MinValue, int.MaxValue);
        }
        return int.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
    }

    // `position` has been observed as a JSON float (e.g. a phone that serializes ms as a double) — GetInt64() throws
    // on that shape, so try the integer read first and round a float rather than discarding the whole seek.
    static long LongLoose(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Number)
            return element.TryGetInt64(out var l) ? l : (long)Math.Round(element.GetDouble());
        return long.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
    }
}

/// <summary>
/// Owns the inbound Connect command queue. A Dealer ACK confirms validation and queue admission, not audible completion.
/// The single worker preserves command order and observes every handler task.
/// </summary>
public sealed class ConnectCommandRouter : IDisposable
{
    const int DedupeCapacity = 1024;
    static readonly long DedupeTicks = (long)(Stopwatch.Frequency * TimeSpan.FromMinutes(10).TotalSeconds);

    readonly ITransport _transport;
    readonly Func<ConnectCommand, CancellationToken, Task<ConnectCommandOutcome>> _dispatch;
    readonly Func<int, CancellationToken, Task<ConnectCommandOutcome>>? _volumeDispatch;
    readonly Action<uint>? _onVolumeMessageId;
    readonly WaveeLogger _log;
    readonly IDisposable _requestSub;
    readonly IDisposable _volumeSub;
    readonly System.Threading.Channels.Channel<ConnectWork> _queue;
    readonly CancellationTokenSource _cts = new();
    readonly Task _worker;
    readonly object _dedupeGate = new();
    readonly Dictionary<string, long> _seen = new(StringComparer.Ordinal);
    readonly Queue<(string Key, long At)> _seenOrder = new();

    public ConnectCommandRouter(ITransport transport, Action<ConnectCommand> dispatch, WaveeLogger log = default)
        : this(transport, (command, _) =>
        {
            dispatch(command);
            return Task.FromResult(ConnectCommandOutcome.Applied);
        }, null, log)
    {
    }

    public ConnectCommandRouter(
        ITransport transport,
        Func<ConnectCommand, CancellationToken, Task<ConnectCommandOutcome>> dispatch,
        Func<int, CancellationToken, Task<ConnectCommandOutcome>>? volumeDispatch = null,
        WaveeLogger log = default,
        int capacity = 256,
        // Fires (synchronously, before volumeDispatch is awaited) with the sender's OWN message_id off an inbound
        // connect/volume MESSAGE — a separate hook rather than widening volumeDispatch's signature, so existing
        // callers/tests that only care about the plain (volume, ct) shape keep compiling untouched. Wired by the
        // composition root to attribute the resulting PutState to that message id (findings §9), so the phone that
        // sent the slider move sees its own id echoed back instead of fighting an unattributed optimistic update.
        Action<uint>? onVolumeMessageId = null)
    {
        _transport = transport;
        _dispatch = dispatch;
        _volumeDispatch = volumeDispatch;
        _onVolumeMessageId = onVolumeMessageId;
        _log = log;
        _queue = System.Threading.Channels.Channel.CreateBounded<ConnectWork>(new BoundedChannelOptions(Math.Max(1, capacity))
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
        _worker = Task.Run(WorkerAsync);
        _requestSub = transport.Requests("hm://connect-state/v1/")
            .Subscribe(Observers.From<WireRequest>(OnRequest));
        _volumeSub = transport.Events("hm://connect-state/v1/connect/volume")
            .Subscribe(Observers.From<WireEvent>(OnVolume));
    }

    void OnRequest(WireRequest request)
    {
        RequestResult result;
        if (!ConnectCommand.TryParse(request, out var command))
        {
            // TryParse still stashes its best-effort Endpoint even on failure (a garbled BODY on a real endpoint,
            // vs. an endpoint the wire never taught us). Only the latter is truly DeviceDoesNotSupportCommand —
            // controllers cache that reply per device, so misreporting the former makes one malformed frame turn
            // off an otherwise-working command for the rest of the session (findings §3).
            bool knownEndpoint = ConnectCommand.Map(command.Endpoint) != ConnectCmd.Unknown;
            result = knownEndpoint ? RequestResult.UpstreamError : RequestResult.DeviceDoesNotSupportCommand;
            _log.Info((knownEndpoint ? "connect command parse failed on known endpoint: " : "connect command unsupported: ")
                + request.MessageIdent);
        }
        else
        {
            string dedupeKey = DedupeKey(command);
            if (dedupeKey.Length > 0 && IsDuplicate(dedupeKey))
            {
                result = RequestResult.Success;
                _log.Event(WaveeLogLevel.Debug, "connect.command.duplicate", "exact Connect command replay ignored",
                    fields:
                    [
                        WaveeLogField.Of("endpoint", command.Endpoint),
                        WaveeLogField.Of("messageId", command.MessageId),
                        WaveeLogField.Of("sender", Fingerprint(command.SenderDeviceId)),
                    ]);
            }
            else if (_queue.Writer.TryWrite(ConnectWork.ForCommand(command, Stopwatch.GetTimestamp(), dedupeKey)))
            {
                // Remembered right here at admission — an exact replay arriving before the worker has drained this
                // one must still be caught by IsDuplicate above (findings §1's replay window). WorkerAsync forgets
                // the key on a Failed outcome instead, so a command that goes on to FAIL is not permanently poisoned
                // as "already seen": the controller's own retry for the same (sender, message_id, endpoint) still
                // dispatches. ACK semantics are untouched: admission still ACKs Success regardless of how the
                // handler later resolves.
                if (dedupeKey.Length > 0) Remember(dedupeKey);
                result = RequestResult.Success;
                _log.Event(WaveeLogLevel.Info, "connect.command.received", "Connect command accepted",
                    fields:
                    [
                        WaveeLogField.Of("endpoint", command.Endpoint),
                        WaveeLogField.Of("messageId", command.MessageId),
                        WaveeLogField.Of("sender", Fingerprint(command.SenderDeviceId)),
                        WaveeLogField.Of("commandId", Fingerprint(command.CommandId)),
                        WaveeLogField.Of("session", Fingerprint(command.SessionId)),
                        WaveeLogField.Of("payloadBytes", command.Payload?.Length ?? 0),
                    ]);
            }
            else
            {
                result = RequestResult.ContextPlayerError;
                _log.Warn($"connect command queue full: endpoint={command.Endpoint}");
            }
        }
        _ = _transport.Reply(request.RequestId, result);
    }

    void OnVolume(WireEvent wire)
    {
        if (_volumeDispatch is null) return;
        if (!TryParseSetVolume(wire.Payload, out int volume, out uint messageId))
        {
            _log.Warn("connect volume MESSAGE had an invalid SetVolumeCommand body");
            return;
        }
        if (!_queue.Writer.TryWrite(ConnectWork.ForVolume(volume, messageId, Stopwatch.GetTimestamp())))
            _log.Warn("connect command queue full: inbound volume dropped");
    }

    async Task WorkerAsync()
    {
        try
        {
            await foreach (var work in _queue.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                var outcome = ConnectCommandOutcome.NoOp;
                Exception? error = null;
                try
                {
                    if (work.IsVolume && work.VolumeMessageId != 0) _onVolumeMessageId?.Invoke(work.VolumeMessageId);
                    outcome = work.IsVolume
                        ? (_volumeDispatch is null ? ConnectCommandOutcome.NoOp
                            : await _volumeDispatch(work.Volume, _cts.Token).ConfigureAwait(false))
                        : await _dispatch(work.Command, _cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_cts.IsCancellationRequested) { break; }
                catch (Exception ex) { outcome = ConnectCommandOutcome.Failed; error = ex; }

                // Admission already Remember()ed the key so a replay racing in before this drains is still caught
                // (findings §1's replay window). A Failed handler must not leave that Remember() standing, or the
                // controller's own retry for the same (sender, message_id, endpoint) would be dropped as an "exact
                // replay" of a command that never actually applied — so undo it here, on the one outcome that means
                // the command needs to be retriable.
                if (work.DedupeKey.Length > 0 && outcome == ConnectCommandOutcome.Failed) Forget(work.DedupeKey);

                long durationMs = (long)Stopwatch.GetElapsedTime(work.ReceivedAt).TotalMilliseconds;
                if (work.IsVolume)
                {
                    _log.Event(error is null ? WaveeLogLevel.Info : WaveeLogLevel.Warning,
                        "connect.volume.completed", "inbound Connect volume completed", elapsedMs: durationMs, ex: error,
                        fields:
                        [
                            WaveeLogField.Of("volume", work.Volume),
                            WaveeLogField.Of("messageId", work.VolumeMessageId),
                            WaveeLogField.Of("outcome", outcome.ToString()),
                        ]);
                }
                else
                {
                    _log.Event(error is null ? WaveeLogLevel.Info : WaveeLogLevel.Warning,
                        "connect.command.completed", "Connect command completed", elapsedMs: durationMs, ex: error,
                        fields:
                        [
                            WaveeLogField.Of("endpoint", work.Command.Endpoint),
                            WaveeLogField.Of("messageId", work.Command.MessageId),
                            WaveeLogField.Of("sender", Fingerprint(work.Command.SenderDeviceId)),
                            WaveeLogField.Of("commandId", Fingerprint(work.Command.CommandId)),
                            WaveeLogField.Of("outcome", outcome.ToString()),
                        ]);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log.Warn("connect command worker fault: " + ex.Message, ex); }
    }

    bool IsDuplicate(string key)
    {
        lock (_dedupeGate)
        {
            PruneSeen(Stopwatch.GetTimestamp());
            return _seen.ContainsKey(key);
        }
    }

    void Remember(string key)
    {
        lock (_dedupeGate)
        {
            long now = Stopwatch.GetTimestamp();
            PruneSeen(now);
            if (_seen.ContainsKey(key)) return;
            _seen[key] = now;
            _seenOrder.Enqueue((key, now));
            while (_seenOrder.Count > DedupeCapacity)
            {
                var old = _seenOrder.Dequeue();
                if (_seen.TryGetValue(old.Key, out long at) && at == old.At) _seen.Remove(old.Key);
            }
        }
    }

    // Undoes admission's Remember() for a command whose handler FAILED — the stale entry left in _seenOrder is
    // harmless (its own eventual dequeue is a no-op once _seen no longer has the key, same as any pruned entry).
    void Forget(string key)
    {
        lock (_dedupeGate) _seen.Remove(key);
    }

    void PruneSeen(long now)
    {
        while (_seenOrder.TryPeek(out var first) && now - first.At > DedupeTicks)
        {
            _seenOrder.Dequeue();
            if (_seen.TryGetValue(first.Key, out long at) && at == first.At) _seen.Remove(first.Key);
        }
    }

    // Endpoint is part of the key: message_id is a small per-sender counter that a phone recycles across DIFFERENT
    // endpoints (a fresh set_shuffling_context can land on the same id as an earlier seek_to), and without the
    // endpoint term that recycled id looked like an exact replay of the unrelated command and got silently dropped.
    static string DedupeKey(in ConnectCommand command) =>
        command.MessageId == 0 || string.IsNullOrEmpty(command.SenderDeviceId)
            ? ""
            : command.SenderDeviceId + "\n" + command.MessageId.ToString(CultureInfo.InvariantCulture) + "\n" + command.Endpoint;

    static string Fingerprint(string value) =>
        string.IsNullOrEmpty(value) ? "-" : WaveeLogRedaction.HashLike(value);

    /// <summary>SetVolumeCommand field 1 (varint) is the level; field 2 is the nested CommandOptions message whose own
    /// field 1 (varint) is the sender's message_id (findings §9) — reading it lets an inbound Connect volume
    /// attribute its resulting PutState the same way a JSON command does, instead of leaving the field blank and
    /// making the sending phone's slider fight its own un-attributed echo. Scans the WHOLE payload (rather than
    /// returning the instant field 1 is found, as before) so a message_id that follows the volume field is not
    /// missed — 24/24 captured bodies had it that way.</summary>
    internal static bool TryParseSetVolume(ReadOnlySpan<byte> payload, out int volume, out uint messageId)
    {
        volume = 0;
        messageId = 0;
        bool haveVolume = false;
        int offset = 0;
        while (offset < payload.Length)
        {
            if (!TryReadVarint(payload, ref offset, out ulong key)) break;
            int field = (int)(key >> 3);
            int wire = (int)(key & 7);
            if (field == 1 && wire == 0)
            {
                if (!TryReadVarint(payload, ref offset, out ulong raw) || raw > int.MaxValue) break;
                volume = Math.Clamp((int)raw, 0, 65535);
                haveVolume = true;
            }
            else if (field == 2 && wire == 2)
            {
                if (!TryReadVarint(payload, ref offset, out ulong length) || length > int.MaxValue || offset + (int)length > payload.Length)
                    break;
                int end = offset + (int)length;
                TryReadNestedMessageId(payload[offset..end], out messageId);
                offset = end;
            }
            else if (!SkipField(payload, ref offset, wire)) break;
        }
        return haveVolume;
    }

    static bool TryReadNestedMessageId(ReadOnlySpan<byte> bytes, out uint messageId)
    {
        messageId = 0;
        int offset = 0;
        while (offset < bytes.Length)
        {
            if (!TryReadVarint(bytes, ref offset, out ulong key)) return false;
            int field = (int)(key >> 3);
            int wire = (int)(key & 7);
            if (field == 1 && wire == 0)
            {
                if (!TryReadVarint(bytes, ref offset, out ulong raw)) return false;
                messageId = unchecked((uint)raw);
                return true;
            }
            if (!SkipField(bytes, ref offset, wire)) return false;
        }
        return false;
    }

    static bool TryReadVarint(ReadOnlySpan<byte> bytes, ref int offset, out ulong value)
    {
        value = 0;
        for (int shift = 0; shift < 64 && offset < bytes.Length; shift += 7)
        {
            byte b = bytes[offset++];
            value |= (ulong)(b & 0x7f) << shift;
            if ((b & 0x80) == 0) return true;
        }
        return false;
    }

    static bool SkipField(ReadOnlySpan<byte> bytes, ref int offset, int wire)
    {
        switch (wire)
        {
            case 0: return TryReadVarint(bytes, ref offset, out _);
            case 1: offset += 8; return offset <= bytes.Length;
            case 2:
                if (!TryReadVarint(bytes, ref offset, out ulong length) || length > int.MaxValue) return false;
                offset += (int)length;
                return offset <= bytes.Length;
            case 5: offset += 4; return offset <= bytes.Length;
            default: return false;
        }
    }

    public void Dispose()
    {
        _requestSub.Dispose();
        _volumeSub.Dispose();
        _queue.Writer.TryComplete();
        try
        {
            if (!_worker.Wait(TimeSpan.FromSeconds(2)))
            {
                _cts.Cancel();
                _worker.Wait(TimeSpan.FromSeconds(1));
            }
        }
        catch { }
        _cts.Dispose();
    }

    // DedupeKey rides along so Remember(...) can happen in WorkerAsync, after the outcome is known, rather than at
    // admission (findings §1) — "" for volume work, which has no message_id-based dedupe.
    readonly record struct ConnectWork(ConnectCommand Command, int Volume, uint VolumeMessageId, bool IsVolume, long ReceivedAt, string DedupeKey)
    {
        public static ConnectWork ForCommand(ConnectCommand command, long at, string dedupeKey) => new(command, 0, 0, false, at, dedupeKey);
        public static ConnectWork ForVolume(int volume, uint messageId, long at) => new(default, volume, messageId, true, at, "");
    }
}
