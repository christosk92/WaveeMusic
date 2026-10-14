// ── Entities/Recents.Facts.cs ──────────────────────────────────────────────────────────────────────────────────────
// what a Recents target says: the live facts off the target's columns, the playlist byline, a member's cover, the table a
// row's slot subscribes to
//
// Role: CORE
// Owner: P
// Wave: 5
// Spec: ch 16 §2 W5-W7, W23 · §3.1 · §9.2 (the artwork setting)
//
// T10c2: the rows, the day headers, the drawer and the calendar are the structure's (`Themes\Classic\Structure\Views\Pages\ClassicRecents*.cs`).
// What stays here is what reads the entity tables: a view may not name `Entities` (WVTH002), so the row slots reach these through `IRecentsHost`.

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using Wavee.Theming.Contracts;

namespace Wavee;

public readonly partial struct Recents
{
    /// <summary>A target's facts, read LIVE off its columns (no copied strings); until <see cref="RecentsFacts.Known"/> holds the row
    /// paints geometry, never an invented title.</summary>
    internal static RecentsFacts FactsOf(EntityRef t)
    {
        var s = Entities.Current;
        int slot = t.Slot;
        switch (t.Kind)
        {
            case EntityKind.Collection:
                return new RecentsFacts(true, Loc.Get(Strings.Detail.LikedSongs), null, null);
            case EntityKind.Playlist when slot > 0 && s.Playlists.Knows(slot, (uint)PlaylistFields.Identity):
            {
                var p = new Playlist(slot);
                return new RecentsFacts(true, Text(p.TitleId), OwnerOf(p), Controls.ArtUrl(p.ImageId));
            }
            case EntityKind.Album when slot > 0 && s.Albums.Knows(slot, (uint)AlbumFields.Title):
            {
                var a = new Album(slot);
                var artists = a.ArtistSlots;
                return new RecentsFacts(true, a.Title, artists.Length > 0 ? NonEmpty(new Artist(artists[0]).Name) : null,
                                        Controls.ArtUrl(a.ImageId));
            }
            case EntityKind.Artist when slot > 0 && s.Artists.Knows(slot, (uint)ArtistFields.Name):
                return new RecentsFacts(true, new Artist(slot).Name, null, Controls.ArtUrl(new Artist(slot).ImageId));
            case EntityKind.Show when slot > 0 && s.Shows.Knows(slot, (uint)ShowFields.Title):
            {
                var sh = new Show(slot);
                return new RecentsFacts(true, sh.Title, NonEmpty(Text(sh.PublisherId)), Controls.ArtUrl(sh.ImageId));
            }
            case EntityKind.Episode when slot > 0 && s.Episodes.Knows(slot, (uint)EpisodeFields.Title):
            {
                var e = new Episode(slot);
                return new RecentsFacts(true, e.Title, NonEmpty(e.Show.Title), Controls.ArtUrl(e.ImageId));
            }
            case EntityKind.Track when slot > 0 && s.Tracks.Knows(slot, (uint)TrackFields.Title):
            {
                var tr = new Track(slot);
                return new RecentsFacts(true, tr.Title, NonEmpty(Text(tr.ArtistLineId)), Controls.ArtUrl(tr.ImageId));
            }
        }
        return RecentsFacts.Unknown;
    }

    /// <summary>A playlist's byline through <see cref="RecentsView.OwnerSubtitle"/>: never a raw base62 id.</summary>
    static string? OwnerOf(Playlist p)
    {
        var owner = p.Owner;
        if (!owner.IsValid || !owner.Knows(UserFields.Identity)) return null;
        var id = owner.Id;
        string? raw = id.Form == EntityForm.Text ? NonEmpty(Text(id.TextId)) : null;
        return RecentsView.OwnerSubtitle(NonEmpty(Text(owner.NameId)), raw, null);
    }

    /// <summary>A member's cover for the Saved stack, straight off the image column (an empty column is the neutral tile).</summary>
    internal static string? CoverOf(EntityRef t) => t.Kind switch
    {
        EntityKind.Track when t.Slot > 0 => Controls.ArtUrl(new Track(t.Slot).ImageId),
        EntityKind.Episode when t.Slot > 0 => Controls.ArtUrl(new Episode(t.Slot).ImageId),
        _ => null,
    };

    static string Text(StringId id) => Entities.Strings.Resolve(id);
    static string? NonEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    static Table? TableOf(Scope scope, EntityKind kind) => kind switch
    {
        EntityKind.Track => scope.Tracks,
        EntityKind.Episode => scope.Episodes,
        EntityKind.Album => scope.Albums,
        EntityKind.Artist => scope.Artists,
        EntityKind.Playlist => scope.Playlists,
        EntityKind.Show => scope.Shows,
        _ => null,
    };

    /// <summary>Subscribe the calling slot to what row <paramref name="r"/> paints — the target's table (identity landing), the users (a playlist's
    /// owner byline) and the tracks (a Saved row's member stack) — and to the scope epoch.</summary>
    internal static void SubscribeRow(RecentsShape shape, int r)
    {
        _ = Entities.ScopeEpoch.Value;
        var scope = Entities.Current;
        var target = RecentsView.EntitySlotOf(shape.Rows, r);
        _ = TableOf(scope, target.Kind)?.Changed.Value;
        if (target.Kind == EntityKind.Playlist) _ = scope.Users.Changed.Value;
        if (shape.Rows.Rows[r].Why == RecentsReason.Saved) _ = scope.Tracks.Changed.Value;
    }

    /// <summary>The dormant shared-element key of a target (Design.MorphKeys).</summary>
    static string? MorphKeyOf(EntityKind kind, string uri) => Design.MorphKeys.For(kind, uri);

    /// <summary>The accent before any cover grades, with washes off, or with no source row: byte-identical to what every consumer painted before the page
    /// had a dynamic accent (§4.1).</summary>
    internal static Design.PageAccent FallbackAccent() => new(Tok.AccentTextPrimary, Tok.AccentDefault, "");

    /// <summary>The heat's paint: the accent INK at A_subtle + (ink.A − A_subtle)·level/5 (§4.3).</summary>
    static ColorF DensityFillOf(int level, ColorF ink) => RecentsLayout.DensityFill(level, ink, Tok.AccentSubtle.A);

    /// <summary>The app-wide track-artwork policy (a reactive read).</summary>
    static bool ShowsTrackArt() => !Prefs.Appearance.TrackArtworkHidden();
}
