// ── Theming/Contracts/StyleKeys.LibraryPlaylistHead.cs — the library playlists reader's head (contract 1.62, audit B5a) ───────────────────────
//
// The library's playlists master-detail (`LibraryTierPolicy.PlaylistsMasterDetail`) draws the picked playlist beside the list: its head (the cover,
// the name, the owner and count line, play · shuffle · open playlist) over the embedded table. The core pane keeps the demand, the verbs and the
// table; the head is a PICTURE a theme may draw in its own voice (Z-Library-Playlists: a 40 light title, the verbs as rings with their words). A theme
// that registers none gets the core's neutral head.

using FluentGpu.Dsl;
using FluentGpu.Theming;

namespace Wavee.Theming.Contracts;

public static partial class StyleKeys
{
    /// <summary>The library playlists reader's head (<see cref="LibraryPlaylistHeadStyle"/>). Optional: the core draws its own without one.</summary>
    public static readonly StyleKey<LibraryPlaylistHeadStyle> LibraryPlaylistHead = new("Library.PlaylistHead");
}

/// <summary>The library playlists reader's head, as a theme draws it.</summary>
/// <param name="Head">The head for <see cref="LibraryPlaylistHeadArgs"/>: a pure builder.</param>
public sealed record LibraryPlaylistHeadStyle(Func<LibraryPlaylistHeadArgs, Element> Head);

/// <summary>What the head shows and does.</summary>
/// <param name="CoverAt">The playlist's cover (or a folder's mosaic) at an edge the theme picks.</param>
/// <param name="Name">The playlist's name, as its owner wrote it (never re-cased).</param>
/// <param name="Meta">The owner and count line ("christos · 42 songs"); empty when nothing is known.</param>
/// <param name="Play">Plays the playlist, or pauses it when it is the playing context.</param>
/// <param name="Shuffle">Shuffle-plays it.</param>
/// <param name="Open">Opens its page.</param>
public sealed record LibraryPlaylistHeadArgs(Func<float, Element> CoverAt, string Name, string Meta, Action Play, Action Shuffle, Action Open);
