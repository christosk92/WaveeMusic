using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

using Wavee.Sdk;

namespace Wavee.Module.Clx;

/// <summary>
/// CLX (Clockworks, github.com/ClockworksTech/CLX-Spec, Apache-2.0) for #154: the app's playback timeline as UDP datagrams on
/// the spec's multicast group <c>239.0.0.1:3650</c>, TTL 1 (this network only). Every datagram is a packet-type byte and ONE
/// MessagePack map. Deck <c>0x01</c> at 60 Hz for deck 0 (A), its <c>Position</c> extrapolated from the host's anchor (one QPC
/// for the machine, so the stdio delay drops out); Meta <c>0x02</c> when the item changes and every 5 s (a late joiner gets
/// the title: the spec's <c>0x09</c> resync request has no published layout to answer); Control <c>0x00</c> once a second.
/// While nothing plays the Deck packets go on with the position held (the spec sends continuously).
/// The wire format is implemented from the spec's README, not from its code.
/// </summary>
public sealed class ClxModule : WaveeModule
{
    private static readonly IPEndPoint Group = new(IPAddress.Parse("239.0.0.1"), 3650);
    private static readonly TimeSpan DeckPeriod = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 60);

    private readonly MsgPackWriter _writer = new();
    private readonly object _sendGate = new();
    private Socket? _socket;
    private TimelineState? _state;
    private string? _metaUri;
    private bool _metaPending;
    private CancellationTokenSource? _loop;
    private long _deckPackets, _metaPackets, _controlPackets, _sendFailures;
    private byte _ord;
    private string _bound = "";

    /// <inheritdoc/>
    public override ValueTask<ResolvedPlayable> ResolveAsync(string playableId, CancellationToken ct)
        => throw new ModuleException(ModuleErrorCode.Unsupported, "The CLX module plays nothing; it observes the timeline.");

    /// <inheritdoc/>
    public override ValueTask OnTimelineAsync(TimelineState state, CancellationToken ct)
    {
        TimelineState? previous = Volatile.Read(ref _state);
        Volatile.Write(ref _state, state);
        if (!string.Equals(previous?.Uri, state.Uri, StringComparison.Ordinal)) Volatile.Write(ref _metaPending, true);
        if (_loop is null) Start();
        return default;
    }

    private void Start()
    {
        lock (_sendGate)
        {
            if (_loop is not null) return;
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);
            socket.Connect(Group);
            _bound = socket.LocalEndPoint?.ToString() ?? "";
            _socket = socket;
            _loop = new CancellationTokenSource();
            if (HasHost) Host.Log(ModuleLogLevel.Info, "clx sending to " + Group + " ttl=1 from " + _bound);
            _ = RunAsync(_loop.Token);
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(DeckPeriod);
        long lastControl = 0, lastMeta = 0;
        long second = Stopwatch.Frequency;
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                long now = Stopwatch.GetTimestamp();
                TimelineState? s = Volatile.Read(ref _state);
                if (Volatile.Read(ref _metaPending) || now - lastMeta >= 5 * second)
                {
                    Volatile.Write(ref _metaPending, false);
                    SendMeta(s);
                    lastMeta = now;
                }
                if (now - lastControl >= second)
                {
                    SendControl();
                    lastControl = now;
                }
                SendDeck(s, now);
            }
        }
        catch (OperationCanceledException) { }
    }

    private void SendDeck(TimelineState? s, long now)
    {
        double position = 0, length = 0, rate = 1, bpm = 0;
        if (s is { Uri: not null })
        {
            double micros = s.PositionMicros;
            if (s.Playing && s.TicksPerSecond > 0) micros += (now - s.AnchorTicks) * 1e6 / s.TicksPerSecond * s.Rate;
            position = Math.Max(0, micros / 1e6);
            length = s.DurationMs / 1000.0;
            rate = s.Rate;
            bpm = s.Bpm ?? 0;
        }
        lock (_sendGate)
        {
            _writer.Begin(0x01, 12);
            _writer.Float64("Pitch", (rate - 1.0) * 100.0);       // percent off the original speed (0 for music)
            _writer.Float64("Position", position);
            _writer.Float64("Position2", 0);                      // no beat grid in 0.4
            _writer.Float64("NormalizedPosition", length > 0 ? Math.Clamp(position / length, 0, 1) : 0);
            _writer.Float64("BPM", bpm);
            _writer.Float64("Length", length);
            _writer.Float32("EQLow", 0f);
            _writer.Float32("EQMid", 0f);
            _writer.Float32("EQHigh", 0f);
            _writer.UInt8("Deck", 0);
            _writer.UInt8("Beat", 1);
            _writer.UInt8("Ord", _ord++);
            Send(ref _deckPackets);
        }
    }

    private void SendMeta(TimelineState? s)
    {
        lock (_sendGate)
        {
            _metaUri = s?.Uri;
            _writer.Begin(0x02, 5);
            _writer.UInt8("Deck", 0);
            _writer.String("Title", s?.Uri is null ? "" : s.Title);
            _writer.String("Artist", s?.Uri is null ? "" : s.Artist);
            _writer.String("Album", s?.Uri is null ? "" : s.Album);
            _writer.String("FilePath", "");
            Send(ref _metaPackets);
        }
    }

    private void SendControl()
    {
        lock (_sendGate)
        {
            _writer.Begin(0x00, 7);
            _writer.Float64("UpfaderA", 1.0);                     // one deck, fully up
            _writer.Float64("UpfaderB", 0.0);
            _writer.Float64("UpfaderC", 0.0);
            _writer.Float64("UpfaderD", 0.0);
            _writer.Float64("Crossfader", 0.0);                   // hard to deck A
            _writer.UInt8("Active", 0);                           // deck A (the spec does not say index or flag; 0 reads as A either way)
            _writer.String("AppState", "Connected");
            Send(ref _controlPackets);
        }
    }

    private void Send(ref long counter)
    {
        try
        {
            _socket?.Send(_writer.Written.Span);
            counter++;
        }
        catch (SocketException) { _sendFailures++; }
    }

    /// <inheritdoc/>
    public override ValueTask<DiagnosticsReport> GetDiagnosticsAsync(CancellationToken ct)
    {
        var inv = CultureInfo.InvariantCulture;
        return new(new DiagnosticsReport(
        [
            new DiagnosticsSection("CLX",
            [
                ["Destination", Group + " (multicast, TTL 1)"],
                ["Bound", _bound],
                ["Deck packets", Interlocked.Read(ref _deckPackets).ToString(inv)],
                ["Meta packets", Interlocked.Read(ref _metaPackets).ToString(inv)],
                ["Control packets", Interlocked.Read(ref _controlPackets).ToString(inv)],
                ["Send failures", Interlocked.Read(ref _sendFailures).ToString(inv)],
                ["Now", _metaUri ?? "nothing plays"],
            ]),
        ]));
    }

    /// <inheritdoc/>
    public override ValueTask ShutdownAsync(CancellationToken ct)
    {
        _loop?.Cancel();
        lock (_sendGate) { _socket?.Dispose(); _socket = null; }
        return default;
    }
}
