using System;

namespace Wavee.Features.Player.Deck.Model;

/// <summary>
/// The deck model for faces whose ONLY moving part is progress — the iPod Classic (a click-wheel screen: bar, thumb,
/// clocks) and Canvas drift (whose motion is slab-driven keyframes on the cover, not a per-tick fold).
/// <para>It exists so those two faces are not special-cased in <c>DeckClock</c>: they get the same ticker, the same
/// value-gated writes and the same phase name as every other deck, for the cost of one struct copy per tick.
/// <see cref="IsSettled"/> is always true, so the ticker runs while (and only while) audio is playing.</para>
/// </summary>
public sealed class ProgressModel : IDeckModel
{
    int _coverGen;

    public DeckFrame Tick(in DeckInput input, float dtSec)
    {
        // The cover swap is the one edge these faces DO care about: Canvas keys its whole drift frame on CoverGen and
        // cross-fades, and the iPod's LCD art changes with it. Only a NEW ALBUM counts — a same-album advance keeps
        // the artwork, so bumping there would flash the cover on every track of a record.
        if (input.Boundary is DeckBoundary.NaturalNewAlbum or DeckBoundary.SkipNewAlbum) _coverGen++;
        return new DeckFrame(input.Frac, 0f, 0f, 0f, 1f, 0f, 0f, _coverGen, false, PhaseOf(in input));
    }

    public ReadOnlySpan<float> Bands => default;
    public ReadOnlySpan<float> Peaks => default;

    /// <summary>Always settled: nothing here has inertia, so the ticker's own play/pause gate is the whole story.</summary>
    public bool IsSettled => true;

    static DeckPhaseName PhaseOf(in DeckInput i)
    {
        if (i.Error) return DeckPhaseName.Error;
        if (!i.HasTrack) return DeckPhaseName.Idle;
        if (i.Buffering) return DeckPhaseName.Buffering;
        if (i.QueueEnded) return DeckPhaseName.Stopped;
        if (i.ScrubTargetMs is not null || i.SeekTargetMs is not null) return DeckPhaseName.Seeking;
        return i.Advancing || i.PlayWhenReady ? DeckPhaseName.Playing : DeckPhaseName.Paused;
    }
}
