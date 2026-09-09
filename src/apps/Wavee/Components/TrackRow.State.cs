namespace Wavee;

// TrackRow's ONE engine-free fragment — split out of TrackRow.cs (which pulls in FluentGpu.Dsl/Controls/Animation
// throughout) so Wavee.Tests can source-include just this file, exactly like MembershipDiff.cs / DetailTrackProjection.cs.
// RowPresentation.cs (Features/Detail) carries a State field and needs this type without pulling in the whole
// (GPU-bound) cell-builder class it names.
internal static partial class TrackRow
{
    // The per-row playback state the cell reflects (now-playing equalizer / buffer spinner / top-track star / saved heart).
    internal readonly record struct State(bool IsNow, bool IsPlaying, bool IsBuffering, bool IsTop, bool Saved);
}
