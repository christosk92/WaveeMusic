using System;
using Wavee.Features.Player.Deck.Model;

namespace Wavee;

/// <summary>
/// Preset id -> the deck's PHYSICS. The one place a preset becomes a model, so <c>DeckHost</c> holds no per-deck
/// knowledge at all and the twelve decks can be built (and unit-tested) independently.
///
/// <para><b>THE CONSTRUCTOR CONTRACT.</b> Every model below is engine-free and lives under
/// <c>Features/Player/Deck/Model/</c>. These are the exact shapes this dispatch compiles against — do not change one
/// without changing this file:</para>
/// <code>
/// RecordModel(in DeckInput seed, RecordVariant variant)   // Record / Turntable / Zune / Picture — RecordModel.cs
/// TapeModel(TapeKind kind)                                // Cassette / Reel                     — TapeMachine.cs
/// DiscModel()                                             // CD / MiniDisc                       — DiscMachine.cs
/// MeterModel(bool ppm, Func&lt;(float rms, float peak)?&gt; levels)               // Hi-fi VU      — MeterBallistics.cs
/// LevelModel(int bands, bool scope, Func&lt;(float rms, float peak)?&gt; levels)   // Winamp / WMP  — LevelSynth.cs
/// ProgressModel()                                         // iPod / Canvas                       — ProgressModel.cs
/// </code>
///
/// <para><b>Why the seed.</b> The record family is the only one with a mount-time DECISION to make: a deck mounted
/// mid-song must show a record that has been playing all along, not replay a 3-second cue sequence. It therefore
/// takes the transport as it stands (<c>DeckClock.Seed</c>); every other model starts from its own rest pose and
/// converges within a tick or two, so they take only their options.</para>
/// </summary>
static class DeckModels
{
    public static IDeckModel Create(NpvPlayerCatalog.Preset preset, IAppSettings? settings, PlaybackBridge bridge, in DeckInput seed)
        => preset.Id switch
        {
            NpvPlayerCatalog.Record => new RecordModel(in seed, RecordVariant.Record),
            NpvPlayerCatalog.Turntable => new RecordModel(in seed, RecordVariant.Turntable),
            NpvPlayerCatalog.Zune => new RecordModel(in seed, RecordVariant.Zune),
            NpvPlayerCatalog.Picture => new RecordModel(in seed, RecordVariant.Picture),
            NpvPlayerCatalog.Cassette => new TapeModel(TapeKind.Cassette),
            NpvPlayerCatalog.Reel => new TapeModel(TapeKind.Reel),
            NpvPlayerCatalog.Cd => new DiscModel(),
            NpvPlayerCatalog.Vu => new MeterModel(NpvPlayerPrefs.ChoiceSlug(settings, preset, "ballistics") == "ppm", Levels(bridge)),
            // Winamp's spectrum window is 19 bars; its oscilloscope option fills the SAME band array with a waveform
            // sample instead, so the face binds one set of signals either way.
            NpvPlayerCatalog.Winamp => new LevelModel(19, NpvPlayerPrefs.ChoiceSlug(settings, preset, "vis") == "scope", Levels(bridge)),
            NpvPlayerCatalog.Wmp => new LevelModel(24, false, Levels(bridge)),
            // iPod and Canvas: progress is the only thing that moves per tick (Canvas's drift is slab keyframes).
            _ => new ProgressModel(),
        };

    /// <summary>
    /// The audio level tap as a pull, not a subscription. The signal is written on the AUDIO PUMP THREAD, so a deck
    /// must <c>Peek</c> it from its own ticker and never subscribe — a reactive edge there would schedule UI work
    /// from the pump. Null (no tap: Connect playback, a remote device, the fake backend) is a real answer the synth
    /// handles by falling back to a level-free envelope.
    /// </summary>
    static Func<(float rms, float peak)?> Levels(PlaybackBridge bridge) => () =>
    {
        if (bridge.Levels is not { } signal) return null;
        var frame = signal.Peek();
        return (frame.Rms, frame.Peak);
    };
}
