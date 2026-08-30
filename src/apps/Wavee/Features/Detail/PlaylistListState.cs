namespace Wavee.Features.Detail;

/// <summary>What the detail track list shows in place of (or as) its rows.</summary>
public enum PlaylistRowsState : byte
{
    /// <summary>The header is known but the membership has not been adopted yet — shimmer rows, never "Nothing here yet".</summary>
    Loading,
    /// <summary>The membership is known and it is empty.</summary>
    Empty,
    /// <summary>There are rows, but the live search / filters hide every one of them.</summary>
    NoMatch,
    /// <summary>Real rows.</summary>
    Rows,
}

/// <summary>The PURE decision behind the list's empty branch. Engine-free so <c>PlaylistListStateTests</c> pins it.
/// <para>The old branch had one input — the track count — so a Ready model whose membership had not landed yet (a
/// rootlist-seeded thin header, a decorate reply without rows) was indistinguishable from an empty playlist and the
/// page said "Nothing here yet" for the beat before the snapshot arrived. Membership-known is a separate fact.</para></summary>
public static class PlaylistListState
{
    /// <summary>Shimmer instead of a verdict: nothing is resident AND nobody has told us the list is empty. A model
    /// that already carries rows is never loading, whatever the flag says (rows are proof).</summary>
    public static bool IsLoading(bool membershipLoaded, int total) => !membershipLoaded && total == 0;

    public static PlaylistRowsState For(bool membershipLoaded, int total, int visible)
        => IsLoading(membershipLoaded, total) ? PlaylistRowsState.Loading
         : total == 0 ? PlaylistRowsState.Empty
         : visible == 0 ? PlaylistRowsState.NoMatch
         : PlaylistRowsState.Rows;

    /// <summary>The diagnostics spelling (<c>playlist.open.state state=</c>) — constants, no enum reflection.</summary>
    public static string Name(PlaylistRowsState state) => state switch
    {
        PlaylistRowsState.Loading => "Loading",
        PlaylistRowsState.Empty => "Empty",
        PlaylistRowsState.NoMatch => "NoMatch",
        _ => "Rows",
    };
}
