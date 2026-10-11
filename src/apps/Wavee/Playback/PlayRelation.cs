// ── Playback/PlayRelation.cs ───────────────────────────────────────────────────────────────────────────────────────────
// how a media surface relates to what is playing: "does it light up" (the equalizer pill) and "does its ▶ pause" (the
// ShowsPause fact the play buttons name their glyph and label from)
//
// Role: CORE
// Spec: docs/themes/plan/technical-plan.md §5.1 C16 (X45), §5.7 (props stability); issue #160 (the card seam)
//
// ── ONE DECISION, TWO DOORS ──────────────────────────────────────────────────────────────────────────────────────────
//
// `ShowsPause` used to live in `Controls.Cta.cs` (a view file) and `CardRelates` in `Playback.CardSeam.cs`, each reading
// the deck's signals itself. They are the same identity test, so both are here now: the PURE overload takes the deck's
// facts as arguments (tested without a deck), and the SUBSCRIBING overloads read exactly the signals the target's kind
// compares with, coarse gate first:
//
//   · an idle surface reads ONE bool (`Playback.HasCardContext`) and never joins the per-skip identity fan-out;
//   · a context card (album, playlist, artist, show, collection) reads the playing CONTEXT alone;
//   · a playable (a track or an episode row) reads the playing ITEM alone.
//
// A host reads the fact where it renders and hands the bool to its view in props (a play button names Pause or Play
// from it); a view in any theme never reaches into `Playback` for it.

using FluentGpu.Foundation;

namespace Wavee;

public static class PlayRelation
{
    /// <summary>PURE: does a press on <paramref name="target"/>'s ▶ PAUSE right now? True when the target IS the playing
    /// context (or the playing item, for a playable), something is on deck, playback is running and no fault stands (the
    /// same gate <see cref="ContextPlayRules"/> uses to decide Toggle vs Start).</summary>
    public static bool ShowsPause(EntityId target, EntityId context, EntityId current, bool hasContext, Playback.Fault error, bool isPlaying)
        => !target.IsEmpty && hasContext && CardRelation.Relates(target, context, current)
           && error == Playback.Fault.None && isPlaying;

    /// <summary>The <see cref="ShowsPause(EntityId, EntityId, EntityId, bool, Playback.Fault, bool)"/> fact for the live
    /// deck. A SUBSCRIBING read, coarse-first (see the file header): an idle surface reads one bool and a surface that is
    /// not the playing one re-renders on no skip at all.</summary>
    public static bool ShowsPause(EntityId target)
    {
        if (target.IsEmpty || !Playback.HasCardContext.Value) return false;
        bool relates = Relates(target);
        return relates && Playback.Error.Value == Playback.Fault.None && Playback.IsPlaying.Value;
    }

    /// <summary><see cref="ShowsPause(EntityId)"/> for a uri (the parse interns — UI thread, i.e. a Render).</summary>
    public static bool ShowsPause(string uri)
        => !string.IsNullOrEmpty(uri) && ShowsPause(EntityId.Parse(uri));

    /// <summary>A subscribing read of exactly the signal the target's kind compares with (see <see cref="CardRelation"/>):
    /// the playing item for a playable, the playing context for everything else.</summary>
    public static bool Relates(EntityId target)
        => CardRelation.IsPlayable(target.Kind)
            ? CardRelation.Relates(target, default, Playback.CurrentId.Value)
            : CardRelation.Relates(target, Playback.ContextUri.Value, default);

    /// <summary>The card seam's relation for a uri (<c>Controls.PlaybackSeam.RelatesTo</c> / <c>Owns</c>): false for an
    /// empty or unparseable uri, else <see cref="Relates(EntityId)"/>. Installed by <see cref="Playback.InstallCardSeam"/>.</summary>
    public static bool CardRelates(string uri)
    {
        if (string.IsNullOrEmpty(uri)) return false;
        var card = EntityId.Parse(uri);
        if (card.IsEmpty) return false;
        return Relates(card);
    }
}
