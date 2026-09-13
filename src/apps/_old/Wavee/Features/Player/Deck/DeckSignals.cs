using FluentGpu.Signals;
using Wavee.Features.Player.Deck.Model;

namespace Wavee;

/// <summary>
/// The ONE channel between a deck's physics and its pixels: a fixed slab of signals the face's leaf nodes BIND
/// (<c>Transform</c> / <c>Opacity</c>) at mount and never re-render for, and that <c>DeckClock</c> diff-writes once
/// per tick inside a single <c>Runtime.Batch</c>.
///
/// <para><b>Why a fixed slab rather than a per-deck shape.</b> A face is built ONCE (its bind thunks capture these
/// instances at mount) and the 30 Hz path must not allocate, re-render or reconcile anything. Fixed fields mean the
/// clock's write loop is the same code for all twelve decks, the quantization table lives in one place, and adding a
/// deck adds no signal, no bind and no wake. The per-deck MEANING of each field is documented on
/// <see cref="DeckFrame"/> — that record and this class are two views of the same tuple.</para>
///
/// <para>Resting values are the RECORD family's "nothing has happened yet" pose (arm parked at −34°, cue lever up,
/// record still in its sleeve) because that is the default preset; every other face overrides what it needs on its
/// first tick, which happens before the first paint (the host seeds the model, then folds once at mount).</para>
/// </summary>
sealed class DeckSignals
{
    /// <summary>The number of analyser bands the slab carries. 24 covers the widest deck (WMP); Winamp binds the
    /// first 19 and leaves the rest at rest — one slab, no per-deck allocation.</summary>
    public const int BandCount = 24;

    public readonly FloatSignal Frac = new(0f);
    public readonly FloatSignal Angle0 = new(0f);
    public readonly FloatSignal Angle1 = new(-34f);
    public readonly FloatSignal Lift = new(1f);
    public readonly FloatSignal Slide = new(1f);
    public readonly FloatSignal Aux0 = new(0f);
    public readonly FloatSignal Aux1 = new(0f);

    /// <summary>Analyser band levels 0..1 (spectrum/scope faces). Untouched by every other deck.</summary>
    public readonly FloatSignal[] Bands = Make(BandCount);

    /// <summary>Analyser peak caps 0..1, paired with <see cref="Bands"/>.</summary>
    public readonly FloatSignal[] Peaks = Make(BandCount);

    /// <summary>Bumps at the instant the artwork should change (mid-sleeve, mid-eject) — a face keys its cover
    /// sub-tree on it and cross-fades. NOT a track change: a same-album advance keeps the same cover.</summary>
    public readonly Signal<int> CoverGen = new(0);

    /// <summary>What the deck is doing, medium-agnostically — captions and diagnostics read it.</summary>
    public readonly Signal<DeckPhaseName> Phase = new(DeckPhaseName.Idle);

    static FloatSignal[] Make(int n)
    {
        var a = new FloatSignal[n];
        for (int i = 0; i < n; i++) a[i] = new FloatSignal(0f);
        return a;
    }
}
