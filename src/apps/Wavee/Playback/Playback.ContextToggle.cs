// ── Playback.ContextToggle.cs — the container play affordance that pauses what it is already playing ──────────────────
//
// A card / hero ▶ on an album, playlist, artist, show or collection used to RESTART its context on every press, even
// while that very context was the one playing — the playing row's ▶ pauses, the card's did not. The decision is the pure
// ContextPlayRules below (the card seam's own `CardRelation.Relates` + the row verb's fault gate); PlayOrToggleContext
// only performs it.

using FluentGpu.Foundation;

namespace Wavee;

/// <summary>What a container's play button does.</summary>
public enum ContextPlayAction : byte
{
    /// <summary>Start the context from its head (<see cref="Playback.PlayContext(EntityId, EntityId, int)"/>).</summary>
    Start,
    /// <summary>The context is the one on deck: pause it if playing, resume it if paused.</summary>
    Toggle,
}

/// <summary>PURE: identities and a fault in, a verb out. Same relation as the equalizer pill (<see cref="CardRelation"/>):
/// a context card is compared with the playing context, a playable with the playing item. Same fault gate as a list row
/// (<c>Shell.PlayerBarRules.RowVerb</c>): the reducer's Resume returns while a fault stands, so a toggle there is a click
/// that does nothing — the context is started afresh instead, which heals the fault through <c>PutOnDeck</c>.</summary>
public static class ContextPlayRules
{
    public static ContextPlayAction For(EntityId target, EntityId context, EntityId current, Playback.Fault error)
        => CardRelation.Relates(target, context, current) && error == Playback.Fault.None
            ? ContextPlayAction.Toggle
            : ContextPlayAction.Start;
}

public static partial class Playback
{
    /// <summary>A container's ▶: pause/resume when <paramref name="target"/> is what plays, else start it. UI thread; a
    /// peek — this is a click, not a render.</summary>
    public static void PlayOrToggleContext(EntityId target, string cause = "card.play")
    {
        if (target.IsEmpty) return;
        if (ContextPlayRules.For(target, ContextUri.Peek(), CurrentId.Peek(), Error.Peek()) == ContextPlayAction.Toggle)
            TogglePlay(cause);
        else
            PlayContext(target);
    }

    /// <summary><see cref="PlayOrToggleContext(EntityId, string)"/> for a context named by its uri text (UI thread: the
    /// parse interns). An unparseable uri does nothing.</summary>
    public static void PlayOrToggleContext(string uri, string cause = "card.play")
    {
        if (string.IsNullOrEmpty(uri)) return;
        var id = EntityId.Parse(uri.AsSpan());
        if (id.IsEmpty) return;
        PlayOrToggleContext(id, cause);
    }
}
