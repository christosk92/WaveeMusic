// ── Playback.CardSeam.cs — the card playback seam (Controls.NowPlaying), wired from the deck's own signals ────────────
//
// Every media surface asks ONE seam whether it relates to what is playing (the equalizer pill) and whether it OWNS it (a
// press on its play button pauses instead of starting). The seam was declared with the card rework but never installed,
// so no card ever lit up (#160). The decision is the pure CardRelation below; Install wires it to Playback's signals.

using FluentGpu.Foundation;
using FluentGpu.Signals;

namespace Wavee;

/// <summary>Which card relates to the deck. PURE (identities in, a bool out) so it is tested without a deck.
/// <para>A PLAYABLE card (a track or an episode row) is compared with the playable on deck; every other card (album,
/// playlist, artist, show, collection) with the context it plays from. Kind-split on purpose: a context card then
/// subscribes to the context alone — which changes when the user starts something else — and a skip re-renders only the
/// rows, never every album and playlist card on screen.</para></summary>
public static class CardRelation
{
    /// <summary>The coarse gate: is anything on deck at all?</summary>
    public static bool Active(EntityId context, EntityId current) => !context.IsEmpty || !current.IsEmpty;

    /// <summary>Is the card a playable (compared with the deck's current item) rather than a context?</summary>
    public static bool IsPlayable(EntityKind kind) => kind is EntityKind.Track or EntityKind.Episode;

    /// <summary>The card IS what plays: its playable is on deck, or its context is the one playing.</summary>
    public static bool Relates(EntityId card, EntityId context, EntityId current)
        => !card.IsEmpty && (IsPlayable(card.Kind) ? card.Equals(current) : card.Equals(context));
}

public static partial class Playback
{
    /// <summary>Is anything on deck — the seam's coarse gate. Written once per drain beside <see cref="ContextUri"/>; an
    /// equality-gated signal, so an idle card that reads it first never joins the per-skip identity fan-out.</summary>
    public static readonly Signal<bool> HasCardContext = new(false);

    static readonly Func<string, bool> s_cardRelates = CardRelates;

    /// <summary>Install <see cref="Controls.NowPlaying"/>. Relation and ownership are the same identity test: a card that
    /// IS the playing context or item may light up and may pause it.</summary>
    public static void InstallCardSeam()
        => Controls.NowPlaying = new Controls.PlaybackSeam(HasCardContext, IsPlaying, s_cardRelates, s_cardRelates);

    /// <summary>A subscribing read of exactly the signal the card's kind compares with (see <see cref="CardRelation"/>).</summary>
    static bool CardRelates(string uri)
    {
        if (string.IsNullOrEmpty(uri)) return false;
        var card = EntityId.Parse(uri);
        if (card.IsEmpty) return false;
        return CardRelation.IsPlayable(card.Kind)
            ? CardRelation.Relates(card, default, CurrentId.Value)
            : CardRelation.Relates(card, ContextUri.Value, default);
    }
}
