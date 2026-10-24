// ── Theming/Contracts/FriendsProps.cs — the props of `Panel.Friends` (themes plan 6.0, T6b) ──────────────────────────────────
//
// THE FRIENDS BODY. The friend-activity feed is core: the rows are `Edges.Friends` under one synthetic parent, the coarse state
// (idle, loading, ready, offline, error) and the two host seams (the panel is visible, force a re-seed) are `Rail.Friends`, and
// which face the panel shows for a row count and a state is `Rail.Friends.SurfaceFor` ("rows win whenever there are any": stale rows
// stay visible through a refresh or a transient error). The view draws the face: the keyed rows in a scroller (every push-driven
// upsert enters, exits and FLIPs into place), the skeleton, the offline and error vacancies (Retry only when the host can retry), and
// the empty vacancy.
//
// A view asks nothing of `Entities`, `Playback` or `Shell`: the rows and the feed's state come through <see cref="IFriendsFeed"/>
// (which subscribes the tables the rows read), the clock is a Func, and navigation is a verb.

using FluentGpu.Foundation;

namespace Wavee.Theming.Contracts;

/// <summary>The feed as a view reads it.</summary>
public interface IFriendsFeed
{
    /// <summary>Subscribe what the rows read (the scope epoch, the friends table, the users and the tracks) and answer the rows and the
    /// coarse state. The span is a slice into the table: the caller never holds it across a UI drain (read it inside the render).</summary>
    ReadOnlySpan<FriendEdge> Read(out Rail.Friends.FeedState state);
}

/// <summary>The props of <see cref="ViewKeys.PanelFriends"/>.</summary>
public sealed record FriendsProps
{
    /// <summary>The feed: the rows and the coarse state.</summary>
    public required IFriendsFeed Feed { get; init; }

    /// <summary>The panel shows friends (true on mount) or stopped showing them (false on unmount): drives the feed's watchdog.</summary>
    public required Action<bool> SetActive { get; init; }

    /// <summary>The wall clock as unix milliseconds: a DATE against a server epoch, not a motion clock.</summary>
    public required Func<long> NowMs { get; init; }

    /// <summary>The retry verb (a force re-seed), or null when the host cannot retry; read per render (the host attaches late).</summary>
    public required Func<Action?> Refresh { get; init; }

    /// <summary>Navigate to an entity under a display name (the context, album or artist a row opens).</summary>
    public required Action<EntityUri, string> GoTo { get; init; }

    /// <summary>The verb that opens a friend's profile, or null when the user has no profile route.</summary>
    public required Func<User, Action?> OpenProfile { get; init; }

    /// <summary>Resolve an interned string.</summary>
    public required Func<StringId, string> ResolveString { get; init; }
}
