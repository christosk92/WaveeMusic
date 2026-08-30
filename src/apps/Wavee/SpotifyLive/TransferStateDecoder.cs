using System;
using System.Collections.Generic;
using Wavee.Backend;
using Wavee.Core;
using Wavee.Protocol.Transfer;

namespace Wavee.SpotifyLive;

/// <summary>Maps the generated Spotify transfer protobuf onto the portable playback boundary.</summary>
public sealed class ProtoTransferStateDecoder : ITransferStateDecoder
{
    public bool TryDecode(ReadOnlyMemory<byte> payload, out TransferWireState state)
    {
        state = default;
        if (payload.IsEmpty) return false;
        try
        {
            var wire = TransferState.Parser.ParseFrom(payload.Span);
            if (wire.Playback is null && wire.CurrentSession is null) return false;

            var options = wire.Options;
            var playback = wire.Playback;
            var session = wire.CurrentSession;
            var context = session?.Context;
            var queue = new List<TransferTrackRef>(wire.Queue?.Tracks.Count ?? 0);
            if (wire.Queue is { } q)
                for (int i = 0; i < q.Tracks.Count; i++) queue.Add(Map(q.Tracks[i]));

            state = new TransferWireState(
                context?.Uri ?? "",
                context?.Url ?? "",
                Map(context?.Metadata),
                session?.CurrentUid ?? "",
                Map(playback?.CurrentTrack),
                queue,
                wire.Queue?.IsPlayingQueue ?? false,
                playback?.Timestamp ?? 0,
                playback?.PositionAsOfTimestamp ?? 0,
                playback is null || playback.Speed <= 0 ? 1.0 : playback.Speed,
                playback?.Paused ?? false,
                options?.ShufflingContext ?? false,
                options?.RepeatingTrack == true ? RepeatMode.Track
                    : options?.RepeatingContext == true ? RepeatMode.Context : RepeatMode.Off,
                VideoPersistenceOf(options));
            return !string.IsNullOrEmpty(state.ContextUri)
                || !string.IsNullOrEmpty(state.CurrentTrack.Uri)
                || state.CurrentTrack.Gid.Length > 0;
        }
        catch { return false; }
    }

    // bug 8: TransferPlayerOptions.modes["video_persistence"] == "VIDEO" (capture-verified; see transfer_state.proto).
    static bool VideoPersistenceOf(TransferPlayerOptions? options)
    {
        if (options is null) return false;
        foreach (var entry in options.Modes)
            if (entry.Key == "video_persistence" && string.Equals(entry.Value, "VIDEO", StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    static TransferTrackRef Map(TransferContextTrack? track) =>
        track is null
            ? new TransferTrackRef("", "", Array.Empty<byte>(), Empty)
            : new TransferTrackRef(track.Uri ?? "", track.Uid ?? "", track.Gid?.ToByteArray() ?? Array.Empty<byte>(),
                Map(track.Metadata));

    static IReadOnlyDictionary<string, string> Map(
        Google.Protobuf.Collections.MapField<string, string>? source)
    {
        if (source is null || source.Count == 0) return Empty;
        return new Dictionary<string, string>(source, StringComparer.Ordinal);
    }

    static readonly IReadOnlyDictionary<string, string> Empty =
        new Dictionary<string, string>(StringComparer.Ordinal);
}
