using System;
using System.Security.Cryptography;
using Wavee.Backend.Spotify;

namespace Wavee.Backend.Playlists;

/// <summary>The client-minted identifiers Spotify's playlist service accepts verbatim (verified 2026-08-15: the 50
/// <c>item_id</c>s desktop minted in capture A 046 came back byte-identical on the very next full GET, and the keyed
/// MOV in A 148 addressed rows by those same ids).
/// <para>Minting on the client is what makes keyed ops possible at all: an optimistically inserted row has a stable
/// identity the instant it appears, so a later remove/reorder can name it without waiting for a server round-trip, and
/// a replayed echo of our own ADD is idempotent by id (invariant I6).</para></summary>
public static class SpotifyIds
{
    /// <summary>A membership row id — 8 random bytes as 16 lowercase hex chars (the domain carries item ids as hex;
    /// <see cref="PlaylistWireMapper"/> converts to the 8 raw wire bytes).</summary>
    public static string NewItemId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));

    /// <summary>A playlist id — 16 random bytes as base62/22, the same alphabet and width every Spotify id uses.</summary>
    public static string NewPlaylistId() => Base62.Encode(RandomNumberGenerator.GetBytes(16));

    /// <summary>A rootlist folder id — 8 random bytes as 16 lowercase hex chars, the shape desktop mints for the
    /// <c>spotify:start-group:{id}:{name}</c> / <c>spotify:end-group:{id}</c> marker pair.</summary>
    public static string NewGroupId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
}
