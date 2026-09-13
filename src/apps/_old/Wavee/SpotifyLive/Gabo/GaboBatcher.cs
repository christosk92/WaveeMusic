using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Google.Protobuf;
using Wavee.Backend;
using Wavee.Backend.Spotify;
using Wavee.Protocol.EventSender;

namespace Wavee.SpotifyLive.Gabo;

/// <summary>Buffers gabo envelopes and POSTs to spclient.wg with gzip + 401 refresh retry.</summary>
public sealed class GaboBatcher : IAsyncDisposable
{
    const int MaxEvents = 100;
    const int MaxUncompressedBytes = 125 * 1024;
    const int HeartbeatMs = 300_000;
    const int MaxRetainedEvents = 500;   // backlog cap during a persistent outage (drop oldest, logged — never silent)

    readonly ITransport _transport;
    readonly GaboContext _ctx;
    readonly Func<CancellationToken, Task<string>>? _refreshTokens;
    readonly WaveeLogger _log;
    readonly byte[] _batchSequenceId = RandomNumberGenerator.GetBytes(20);
    readonly System.Threading.Channels.Channel<GaboWorkItem> _channel;
    readonly Task _worker;
    readonly CancellationTokenSource _cts = new();
    readonly Action<long>? _persistSequence;
    long _globalSequence;
    readonly List<EventEnvelope> _pending = new();
    int _pendingBytes;
    Timer? _heartbeat;

    public GaboBatcher(ITransport transport, GaboContext ctx, long initialSequenceNumber = 0,
        Func<CancellationToken, Task<string>>? refreshTokens = null, Action<long>? persistSequence = null, WaveeLogger log = default,
        IWaveeLog? structuredLog = null)
    {
        _transport = transport;
        _ctx = ctx;
        _refreshTokens = refreshTokens;
        _persistSequence = persistSequence;
        _log = log;
        _globalSequence = initialSequenceNumber;
        _channel = System.Threading.Channels.Channel.CreateBounded<GaboWorkItem>(new BoundedChannelOptions(512)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest,
        });
        _worker = Task.Run(WorkerAsync);
        _heartbeat = new Timer(_ => TryEnqueueFlush(), null, HeartbeatMs, HeartbeatMs);
    }

    public long GlobalSequence => Interlocked.Read(ref _globalSequence);

    public void Enqueue(string eventName, byte[] payload)
    {
        var seq = Interlocked.Increment(ref _globalSequence);
        try { _persistSequence?.Invoke(seq); }
        catch (Exception ex) { _log.Warn("gabo persist sequence failed: " + ex.Message, ex); }
        var env = GaboEnvelopeFactory.Build(eventName, payload, _ctx, _batchSequenceId, seq);
        _channel.Writer.TryWrite(new GaboWorkItem(env, payload.Length));
    }

    void TryEnqueueFlush() => _channel.Writer.TryWrite(GaboWorkItem.Flush);

    async Task WorkerAsync()
    {
        try
        {
            await foreach (var item in _channel.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                if (item.IsFlush)
                {
                    try { await FlushAsync().ConfigureAwait(false); }
                    catch (OperationCanceledException) when (_cts.IsCancellationRequested) { break; }
                    catch (Exception ex) { _log.Warn("gabo flush trigger failed; worker continuing: " + ex.Message, ex); }
                    continue;
                }
                if (item.Envelope is not { } envelope)
                {
                    _log.Warn("gabo dropped malformed event work item with no envelope");
                    continue;
                }
                _pending.Add(envelope);
                _pendingBytes += item.PayloadBytes;
                if (_pending.Count >= MaxEvents || _pendingBytes >= MaxUncompressedBytes)
                {
                    try { await FlushAsync().ConfigureAwait(false); }
                    catch (OperationCanceledException) when (_cts.IsCancellationRequested) { break; }
                    catch (Exception ex) { _log.Warn("gabo threshold flush failed; worker continuing: " + ex.Message, ex); }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log.Warn("gabo worker fault: " + ex.Message, ex); }
        finally { try { await FlushAsync().ConfigureAwait(false); } catch (Exception ex) { _log.Warn("gabo final flush failed: " + ex.Message, ex); } }
    }

    async Task FlushAsync()
    {
        if (_pending.Count == 0) return;
        var req = new PublishEventsRequest();
        req.Event.AddRange(_pending);
        var body = HttpCompression.Gzip(req.ToByteArray());
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Content-Type"] = "application/x-protobuf",
            ["Content-Encoding"] = "gzip",
        };
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var resp = await _transport.Request(Backend.Channel.SpclientWg, "/gabo-receiver-service/v3/events/", body, _cts.Token,
                method: "POST", headers: headers).ConfigureAwait(false);
            if (resp.Status == 401 && _refreshTokens is not null)
            {
                try { await _refreshTokens(_cts.Token).ConfigureAwait(false); }
                catch (Exception ex) { _log.Warn("gabo token refresh failed: " + ex.Message, ex); }
                continue;
            }
            if (resp.Ok)
            {
                _pending.Clear();
                _pendingBytes = 0;
                _log.Event(WaveeLogLevel.Debug, "gabo.flush.ok", "play-registration events flushed",
                    fields: [WaveeLogField.Of("events", req.Event.Count)]);
                _log.Info($"gabo flush ok ({req.Event.Count} events)");
                return;
            }
            _log.Warn($"gabo flush failed status={resp.Status} attempt={attempt + 1}");
        }

        // All 3 attempts failed: retain for the next flush trigger, but bound the backlog so a persistent outage can't
        // grow it unbounded. Drop the OLDEST events (least valuable) and log exactly how many — never silently.
        _log.Event(WaveeLogLevel.Warning, "gabo.flush.failed", "play-registration flush exhausted retries; events retained",
            fields: [WaveeLogField.Of("retained", _pending.Count)]);
        if (_pending.Count > MaxRetainedEvents)
        {
            int drop = _pending.Count - MaxRetainedEvents;
            _pending.RemoveRange(0, drop);
            _pendingBytes = 0;
            for (int i = 0; i < _pending.Count; i++) _pendingBytes += _pending[i].CalculateSize();
            _log.Event(WaveeLogLevel.Error, "gabo.flush.overflow", "play-registration backlog capped; oldest events dropped",
                fields: [WaveeLogField.Of("dropped", drop), WaveeLogField.Of("cap", MaxRetainedEvents)]);
        }
        _log.Warn($"gabo flush exhausted retries — {_pending.Count} event(s) retained for retry");
    }

    public async ValueTask DisposeAsync()
    {
        _heartbeat?.Dispose();
        _channel.Writer.TryComplete();
        _cts.Cancel();
        try { await _worker.ConfigureAwait(false); } catch { }
        _cts.Dispose();
    }

    readonly struct GaboWorkItem
    {
        public static GaboWorkItem Flush => new(isFlush: true);
        public readonly EventEnvelope? Envelope;
        public readonly int PayloadBytes;
        public readonly bool IsFlush;
        GaboWorkItem(bool isFlush) { Envelope = null; PayloadBytes = 0; IsFlush = isFlush; }
        public GaboWorkItem(EventEnvelope env, int payloadBytes)
        {
            ArgumentNullException.ThrowIfNull(env);
            Envelope = env;
            PayloadBytes = payloadBytes;
            IsFlush = false;
        }
    }
}
