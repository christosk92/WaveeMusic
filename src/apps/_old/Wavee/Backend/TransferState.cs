using System.Collections.Generic;
using Wavee.Core;

namespace Wavee.Backend;

public readonly record struct TransferTrackRef(
    string Uri,
    string Uid,
    byte[] Gid,
    IReadOnlyDictionary<string, string> Metadata);

public readonly record struct TransferWireState(
    string ContextUri,
    string ContextUrl,
    IReadOnlyDictionary<string, string> ContextMetadata,
    string CurrentUid,
    TransferTrackRef CurrentTrack,
    IReadOnlyList<TransferTrackRef> Queue,
    bool IsPlayingQueue,
    long TimestampMs,
    long PositionMs,
    double Speed,
    bool Paused,
    bool Shuffle,
    RepeatMode Repeat,
    // bug 8: TransferPlayerOptions.modes["video_persistence"] == "VIDEO" (capture-verified — see transfer_state.proto).
    // Folded alongside the per-track track_player/media.type check (MediaSwitchLogic.HasVideoMetadata) so a sender's
    // video hand-off is honored even when the transferred CURRENT track's own metadata didn't carry the claim.
    bool VideoPersistence = false);

/// <summary>Proto-free boundary implemented by the SpotifyLive protobuf adapter.</summary>
public interface ITransferStateDecoder
{
    bool TryDecode(ReadOnlyMemory<byte> payload, out TransferWireState state);
}
