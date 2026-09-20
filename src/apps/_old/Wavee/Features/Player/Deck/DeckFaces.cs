using FluentGpu.Dsl;
using Wavee.Features.Player.Deck.Model;

namespace Wavee;

/// <summary>
/// Preset id -> the deck's PIXELS. The mirror of <see cref="DeckModels"/>: physics on one side, geometry on the
/// other, and nothing in between knows both.
///
/// <para><b>THE FACE CONTRACT.</b> Every face is a static builder in <c>Features/Player/Deck/Faces/</c> with the
/// signature below. It is a plain function, not a component, because a face must be built EXACTLY ONCE per mount —
/// its leaf nodes capture bind thunks over <paramref name="sig"/> at that moment and are never re-rendered by the
/// 30 Hz path.</para>
/// <code>
/// static Element Build(in NpvPlayerCatalog.Preset preset, IAppSettings? settings, float side,
///                      DeckSignals sig, PlaybackBridge bridge, DeckHost host);
/// // RecordDeck additionally takes a trailing `RecordVariant variant` (one face, four skins).
/// </code>
///
/// <para><b>What a face may read.</b> Its OPTIONS through <c>NpvPlayerPrefs.ChoiceSlug(settings, preset, "rpm")</c>
/// (the host re-renders — and therefore rebuilds the face — on the prefs epoch, so an option flip restyles in
/// place), the live track off <paramref name="bridge"/>, and <paramref name="host"/> for the shared
/// <c>DeckGesture</c> and the needle-drop thump. <paramref name="side"/> is the deck's square edge in DIP: every
/// dimension inside a face is a FRACTION of it, never a literal, so the deck scales with the rail.</para>
/// </summary>
static class DeckFaces
{
    public static Element Create(NpvPlayerCatalog.Preset preset, IAppSettings? settings, float side,
                                 DeckSignals sig, PlaybackBridge bridge, DeckHost host)
        => preset.Id switch
        {
            NpvPlayerCatalog.Record => RecordDeck.Build(in preset, settings, side, sig, bridge, host, RecordVariant.Record),
            NpvPlayerCatalog.Turntable => RecordDeck.Build(in preset, settings, side, sig, bridge, host, RecordVariant.Turntable),
            NpvPlayerCatalog.Zune => RecordDeck.Build(in preset, settings, side, sig, bridge, host, RecordVariant.Zune),
            NpvPlayerCatalog.Picture => RecordDeck.Build(in preset, settings, side, sig, bridge, host, RecordVariant.Picture),
            NpvPlayerCatalog.Cassette => CassetteDeck.Build(in preset, settings, side, sig, bridge, host),
            NpvPlayerCatalog.Reel => ReelDeck.Build(in preset, settings, side, sig, bridge, host),
            NpvPlayerCatalog.Cd => CdDeck.Build(in preset, settings, side, sig, bridge, host),
            NpvPlayerCatalog.Ipod => IpodDeck.Build(in preset, settings, side, sig, bridge, host),
            NpvPlayerCatalog.Winamp => WinampDeck.Build(in preset, settings, side, sig, bridge, host),
            NpvPlayerCatalog.Vu => VuDeck.Build(in preset, settings, side, sig, bridge, host),
            NpvPlayerCatalog.Wmp => WmpDeck.Build(in preset, settings, side, sig, bridge, host),
            NpvPlayerCatalog.Canvas => CanvasDeck.Build(in preset, settings, side, sig, bridge, host),
            // An id that is a valid preset but has no face yet would be a blank square rather than a crash — and the
            // catalog's own test pins that every preset id is one of the twelve above.
            _ => new BoxEl { Width = side, Height = side },
        };
}
