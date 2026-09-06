using System;

namespace Wavee;

// W4 — the Slot subtitle GRAMMAR ("Kind · detail"), as a SHAPE rather than text, so it can be unit-tested without the
// engine (Loc/Icons/Tok are all engine-bound and this file must stay engine-free — Data/*.cs is source-included by
// Wavee.Tests without a FluentGpu.Engine reference). `Pane/SidebarPaneText.Format` is the ONLY place that turns a
// shape into a rendered string; everything here decides WHAT the row says, never HOW the words are spelled.
//
// Cluster style keeps `SidebarPaneText.ClusterSubtitleOf` (the landed per-kind table, byte-identical) — this file and
// its `SidebarPaneText.SubtitleOf(in e, SidebarRowStyle)` overload are Slot-only.

/// <summary>The subtitle's leading word — the row's KIND, in the Slot grammar. <see cref="None"/> means the row has
/// no kind word at all (a track's/route's bare creator text, or nothing).</summary>
public enum SidebarSubtitleKind : byte { None = 0, Playlist = 1, Album = 2, Artist = 3, Show = 4, Folder = 5 }

/// <summary>What follows the kind word (if any). <see cref="None"/> ⇒ the bare kind, no separator, no detail.</summary>
public enum SidebarSubtitleDetail : byte { None = 0, Text = 1, SongCount = 2, ItemCount = 3 }

/// <summary>The Slot subtitle as data: a kind word and an optional detail. <see cref="SidebarPaneText.Format"/> is the
/// only consumer that turns this into a localized string ("Playlist · 12 songs", "Album · Daft Punk", a bare
/// "Artist", or a kindless creator name).</summary>
public readonly record struct SidebarSubtitleShape(SidebarSubtitleKind Kind, SidebarSubtitleDetail Detail, string Text, int Count)
{
    /// <summary>No subtitle at all — the row renders none.</summary>
    public static readonly SidebarSubtitleShape Empty = new(SidebarSubtitleKind.None, SidebarSubtitleDetail.None, "", 0);

    /// <summary>True for <see cref="Empty"/> and for anything constructed to the same (Kind=None, Detail=None) shape —
    /// e.g. <see cref="Of(SidebarSubtitleKind, string?)"/> called with <see cref="SidebarSubtitleKind.None"/> and an
    /// empty string, which is exactly what a kindless row with no text collapses to.</summary>
    public bool IsEmpty => Kind == SidebarSubtitleKind.None && Detail == SidebarSubtitleDetail.None;

    /// <summary>The bare kind, no detail ("Artist").</summary>
    public static SidebarSubtitleShape Of(SidebarSubtitleKind kind) => new(kind, SidebarSubtitleDetail.None, "", 0);

    /// <summary>Kind plus free text ("Album · Daft Punk"). An EMPTY <paramref name="text"/> collapses to the bare kind
    /// — never a dangling "Album · " — which is also how a kindless row with nothing to say collapses all the way to
    /// <see cref="Empty"/> (<see cref="Of(SidebarSubtitleKind)"/> with <see cref="SidebarSubtitleKind.None"/> IS
    /// <see cref="Empty"/>).</summary>
    public static SidebarSubtitleShape Of(SidebarSubtitleKind kind, string? text)
        => text is { Length: > 0 } ? new(kind, SidebarSubtitleDetail.Text, text, 0) : Of(kind);

    /// <summary>Kind plus a song count ("Playlist · 12 songs").</summary>
    public static SidebarSubtitleShape Songs(SidebarSubtitleKind kind, int n) => new(kind, SidebarSubtitleDetail.SongCount, "", n);

    /// <summary>Kind plus an item count ("Folder · 4 items").</summary>
    public static SidebarSubtitleShape Items(SidebarSubtitleKind kind, int n) => new(kind, SidebarSubtitleDetail.ItemCount, "", n);
}

/// <summary>The Slot per-kind subtitle table (W4's redraw of <c>SidebarPaneText.ClusterSubtitleOf</c>'s §3.1.3 rules) —
/// same facts, different grammar: every row now leads with its KIND, and a playlist's line depends on whose it is.</summary>
public static class SidebarSubtitleRules
{
    /// <summary>The one system route with a Slot subtitle. Ordinal, like every other route-key comparison in the
    /// sidebar (route keys are identifiers, never user text).</summary>
    public const string LikedRouteKey = "liked";

    /// <summary>The projected-entry table. A LIBRARY playlist leads with its song count (mine) or its owner
    /// (someone else's — <see cref="ShowsOwner"/>); an album's detail is its billed artist (a LIBRARY album carries
    /// it in <c>FirstArtistName</c>, a FEED album — a new release — only in <c>Creator</c>); an artist/show/folder
    /// follow the landed table verbatim (bare / publisher / item count); a track and a non-Liked app route have no
    /// KIND word at all — just their creator text (a concert's venue rides <c>Creator</c>, unchanged from Cluster).</summary>
    public static SidebarSubtitleShape For(in SidebarLibraryEntry e) => e.Kind switch
    {
        SidebarEntryKind.Playlist => ShowsOwner(in e)
            ? SidebarSubtitleShape.Of(SidebarSubtitleKind.Playlist, e.OwnerName)
            : SidebarSubtitleShape.Songs(SidebarSubtitleKind.Playlist, e.TrackCount),
        SidebarEntryKind.Album => SidebarSubtitleShape.Of(SidebarSubtitleKind.Album,
            e.FirstArtistName.Length > 0 ? e.FirstArtistName : e.Creator),
        SidebarEntryKind.Artist => SidebarSubtitleShape.Of(SidebarSubtitleKind.Artist),
        SidebarEntryKind.Show => SidebarSubtitleShape.Of(SidebarSubtitleKind.Show, e.Publisher),
        SidebarEntryKind.Folder => SidebarSubtitleShape.Items(SidebarSubtitleKind.Folder, e.ChildCount),
        SidebarEntryKind.Track => SidebarSubtitleShape.Of(SidebarSubtitleKind.None, e.Creator),
        // A concert's venue rides Creator (§C1.8.5), same as Cluster; every other route has no subtitle at all.
        SidebarEntryKind.AppRoute => string.Equals(e.Id, LikedRouteKey, StringComparison.Ordinal)
            ? SidebarSubtitleShape.Of(SidebarSubtitleKind.Playlist)
            : SidebarSubtitleShape.Of(SidebarSubtitleKind.None, e.Creator),
        _ => SidebarSubtitleShape.Empty,
    };

    /// <summary>Someone else's playlist leads with its owner ("Playlist · Spotify"); mine leads with its song count.
    /// <c>IsOwner</c> is the authority (a stale/unknown <c>Flavor</c> never overrides it); <c>ByYou</c> is the second
    /// signal for a feed-projected row that has no <c>IsOwner</c> bit at all; an owner name that resolved empty falls
    /// back to the song count rather than an empty "Playlist · " line.</summary>
    public static bool ShowsOwner(in SidebarLibraryEntry e)
        => e.Kind == SidebarEntryKind.Playlist && !e.IsOwner && e.Flavor != SidebarPlaylistFlavor.ByYou
           && e.OwnerName.Length > 0;

    /// <summary>The system-route table (<c>SidebarPaneSlot.RouteRow</c>, Slot style only). Only Liked Songs has a Slot
    /// subtitle at all — a live count when the library stats resolved it, else the bare "Playlist" kind word — every
    /// other route (Home, Search, Albums, …) stays silent, matching Cluster's route rows exactly.</summary>
    public static SidebarSubtitleShape ForRoute(string? routeKey, int? likedSongs)
        => string.Equals(routeKey, LikedRouteKey, StringComparison.Ordinal)
            ? likedSongs is { } n
                ? SidebarSubtitleShape.Songs(SidebarSubtitleKind.Playlist, n)
                : SidebarSubtitleShape.Of(SidebarSubtitleKind.Playlist)
            : SidebarSubtitleShape.Empty;
}
