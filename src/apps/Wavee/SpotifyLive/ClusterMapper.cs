using System;
using System.Collections.Generic;
using Google.Protobuf;
using Wavee.Backend;
using Wavee.Backend.Realtime;
using Wavee.Core;
using P = Wavee.Protocol.Player;

namespace Wavee.SpotifyLive;

// The cluster wire boundary: maps the Cluster / ClusterUpdate protos → the proto-free ClusterDelta the Backend projection
// folds (mirrors CollectionWireMapper). ClusterIngest subscribes the dealer cluster topic + the announce-response echo.
public static class ClusterMapper
{
    public static ClusterDelta Map(P.Cluster cluster, string ourDeviceId)
    {
        var ps = cluster.PlayerState;
        bool hasTrack = ps?.Track is not null && !string.IsNullOrEmpty(ps.Track.Uri);
        var track = hasTrack ? MapTrack(ps!.Track, ps.Duration) : default;

        var opts = ps?.Options;
        var repeat = opts is null ? RepeatMode.Off
            : opts.RepeatingTrack ? RepeatMode.Track
            : opts.RepeatingContext ? RepeatMode.Context
            : RepeatMode.Off;

        var devices = new List<ConnectDeviceRow>(cluster.Device.Count);
        foreach (var kv in cluster.Device)
        {
            var d = kv.Value;
            string id = string.IsNullOrEmpty(d.DeviceId) ? kv.Key : d.DeviceId;
            devices.Add(new ConnectDeviceRow(id, d.Name ?? "", MapKind(d.DeviceType, id == ourDeviceId),
                id == cluster.ActiveDeviceId, (int)d.Volume));
        }

        var next = new List<RemoteTrack>();
        if (ps?.NextTracks is { Count: > 0 } nt)
            foreach (var t in nt) if (!string.IsNullOrEmpty(t.Uri)) next.Add(MapTrack(t, 0));
        var prev = new List<RemoteTrack>();
        if (ps?.PrevTracks is { Count: > 0 } pt)
            foreach (var t in pt) if (!string.IsNullOrEmpty(t.Uri)) prev.Add(MapTrack(t, 0));

        var r = ps?.Restrictions;
        bool noNext = r is not null && r.DisallowSkippingNextReasons.Count > 0;
        bool noPrev = r is not null && r.DisallowSkippingPrevReasons.Count > 0;
        bool noSeek = r is not null && r.DisallowSeekingReasons.Count > 0;
        int ourVol = cluster.Device.TryGetValue(ourDeviceId, out var ourDev) ? (int)ourDev.Volume : -1;
        int activeVol = !string.IsNullOrEmpty(cluster.ActiveDeviceId) && cluster.Device.TryGetValue(cluster.ActiveDeviceId, out var actDev)
            ? (int)actDev.Volume : ourVol;   // the slider follows the ACTIVE device; fall back to ours when nobody's active

        return new ClusterDelta(
            cluster.ActiveDeviceId ?? "",
            hasTrack, track,
            string.IsNullOrEmpty(ps?.ContextUri) ? null : ps!.ContextUri,
            ps?.IsPlaying ?? false, ps?.IsPaused ?? false, ps?.IsBuffering ?? false,
            ps?.PositionAsOfTimestamp ?? 0, ps?.Timestamp ?? 0, cluster.ServerTimestampMs, ps?.Duration ?? 0,
            opts?.ShufflingContext ?? false, repeat,
            devices, next,
            noPrev, noNext, noSeek, ourVol,
            ps?.PlaybackSpeed ?? 1.0,
            activeVol,
            ps?.QueueRevision ?? "",
            prev);
    }

    static RemoteTrack MapTrack(P.ProvidedTrack t, long fallbackDuration)
    {
        var m = t.Metadata;
        string Get(string k) => m.TryGetValue(k, out var v) ? v ?? "" : "";
        long dur = long.TryParse(Get("duration"), out var d) ? d : fallbackDuration;
        string img = Get("image_xlarge_url");
        if (img.Length == 0) img = Get("image_large_url");
        if (img.Length == 0) img = Get("image_url");
        string artistUri = !string.IsNullOrEmpty(t.ArtistUri) ? t.ArtistUri : Get("artist_uri");
        string albumUri = !string.IsNullOrEmpty(t.AlbumUri) ? t.AlbumUri : Get("album_uri");
        return new RemoteTrack(t.Uri, Get("title"), Get("artist_name"), artistUri, Get("album_title"), albumUri,
            img.Length == 0 ? null : img, dur, t.Uid, t.Provider,
            m.Count == 0 ? null : new Dictionary<string, string>(m, StringComparer.Ordinal));
    }

    static DeviceKind MapKind(P.DeviceType type, bool isUs)
    {
        if (isUs) return DeviceKind.ThisDevice;
        return type switch
        {
            P.DeviceType.Computer => DeviceKind.Computer,
            P.DeviceType.Smartphone or P.DeviceType.Tablet => DeviceKind.Phone,
            P.DeviceType.Speaker or P.DeviceType.Avr or P.DeviceType.AudioDongle or P.DeviceType.CastAudio => DeviceKind.Speaker,
            P.DeviceType.Tv or P.DeviceType.CastVideo or P.DeviceType.GameConsole => DeviceKind.Tv,
            _ => DeviceKind.Computer,
        };
    }
}

// Subscribes the dealer cluster topic + the announce-response echo, parses the proto, and folds into the projection/roster.
public sealed class ClusterIngest : IDisposable
{
    readonly NowPlayingProjection _projection;
    readonly LiveConnectDevices _devices;
    readonly string _ourDeviceId;
    readonly WaveeLogger _log;
    readonly Action<long>? _onServerTimestamp;   // feeds the server-clock estimator a free passive sample per cluster
    readonly IDisposable _sub;

    public ClusterIngest(ITransport transport, NowPlayingProjection projection, LiveConnectDevices devices,
        string ourDeviceId, WaveeLogger log = default, Action<long>? onServerTimestamp = null)
    {
        _projection = projection;
        _devices = devices;
        _ourDeviceId = ourDeviceId;
        _log = log;
        _onServerTimestamp = onServerTimestamp;
        _sub = transport.Events("hm://connect-state/v1/cluster").Subscribe(Observers.From<WireEvent>(OnEvent));
    }

    // Dealer cluster pushes are a ClusterUpdate (wraps the Cluster).
    void OnEvent(WireEvent e)
    {
        try
        {
            var update = P.ClusterUpdate.Parser.ParseFrom(e.Payload);
            if (update.Cluster is not null) Apply(update.Cluster);
        }
        catch (Exception ex) { _log.Info("cluster parse failed: " + ex.Message); }
    }

    /// <summary>The PUT-state announce RESPONSE body is a Cluster (not a ClusterUpdate) — re-injected here so the
    /// announce-response and the live pushes share one fold path.</summary>
    public void OnAnnounceResponse(byte[] clusterBytes)
    {
        try { Apply(P.Cluster.Parser.ParseFrom(clusterBytes)); }
        catch (Exception ex) { _log.Info("announce-response cluster parse failed: " + ex.Message); }
    }

    void Apply(P.Cluster cluster)
    {
        var delta = ClusterMapper.Map(cluster, _ourDeviceId);
        _onServerTimestamp?.Invoke(delta.ServerTimestampMs);   // refresh the clock BEFORE the fold reads its offset
        _devices.Update(delta.Devices);
        _projection.OnCluster(delta);
    }

    public void Dispose() => _sub.Dispose();
}
