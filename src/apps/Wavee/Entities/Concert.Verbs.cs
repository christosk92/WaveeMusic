// ── Entities/Concert.Verbs.cs ──────────────────────────────────────────────────────────────────────────────────────
// what a concert view opens, plays and reads that names core state (T10b3)
//
// Role: CORE
// Owner: N (stream N-C)
//
// A theme's concert views (`Themes\Classic\Structure\Views\Pages\ClassicConcert*.cs`) may not reference `Entities`, `Shell` or `Playback` (WVTH002). What a tile
// opens, what a promo plays, the interned strings a handle holds and the shelf items (a slot with the version its row had) are these static members; the pure
// rules (`ConcertHub`, `ConcertScheduleShaping`, `ConcertOffers`, `ConcertDetailInfo`, `ConcertTime`) and the handles' own commands (`Track.GoToArtist`) are
// called by the views directly.

using FluentGpu.Foundation;
using Wavee.Theming.Contracts;

namespace Wavee;

/// <summary>A playlist promo's facts: its uri and title, the source line (the owner's name, else the description) and the cover.</summary>
public readonly record struct ConcertPromo(string Uri, string Title, string Source, string? Cover);

/// <summary>The verbs and reads of the concert views.</summary>
public static class ConcertVerbs
{
    /// <summary>An interned string's text.</summary>
    public static string Text(StringId id) => Entities.Strings.Resolve(id);

    /// <summary>Opens a concert's page.</summary>
    public static void OpenConcert(int slot) => Shell.GoTo(Concert.DetailRoute(new Concert(slot)));

    /// <summary>Opens a concert's page by its uri (a schedule tile knows the show, not the slot).</summary>
    public static void OpenConcertByUri(string uri) => OpenConcert(Entities.Current.Concerts.Slot(uri.AsSpan()));

    /// <summary>Opens the Concerts hub.</summary>
    public static void OpenHub() => Shell.GoTo(Concert.HubRoute);

    /// <summary>Opens a promo playlist's page.</summary>
    public static void OpenPlaylist(int slot)
    {
        var pl = new Playlist(slot);
        Shell.GoTo(Shell.For(pl.Uri, Entities.Strings.Resolve(pl.TitleId)));
    }

    /// <summary>Plays the playlist's context, or pauses / resumes it while it is the one playing.</summary>
    public static void PlayPlaylist(string uri) => Playback.PlayOrToggleContext(uri);

    /// <summary>A promo playlist's facts.</summary>
    public static ConcertPromo PromoOf(int slot)
    {
        var pl = new Playlist(slot);
        var s = Entities.Strings;
        string source = pl.Owner.IsValid ? s.Resolve(pl.Owner.NameId) : s.Resolve(pl.DescriptionId);
        return new ConcertPromo(pl.Uri.Text, s.Resolve(pl.TitleId), source, Controls.ArtUrl(pl.ImageId));
    }

    /// <summary>Event shelf items over concert slots: at most <paramref name="cap"/>, each with its row's version.</summary>
    public static ConcertShelfItem[] ConcertItems(ReadOnlySpan<int> slots, int cap)
    {
        var items = new ConcertShelfItem[Math.Min(slots.Length, cap)];
        var t = Entities.Current.Concerts;
        for (int i = 0; i < items.Length; i++) items[i] = new ConcertShelfItem(slots[i], t.Version[slots[i]]);
        return items;
    }

    /// <summary>Promo shelf items over playlist slots: at most <paramref name="cap"/>, each with its row's version.</summary>
    public static ConcertShelfItem[] PlaylistItems(ReadOnlySpan<int> slots, int cap)
    {
        var items = new ConcertShelfItem[Math.Min(slots.Length, cap)];
        var playlists = Entities.Current.Playlists;
        for (int i = 0; i < items.Length; i++) items[i] = new ConcertShelfItem(slots[i], playlists.Version[slots[i]]);
        return items;
    }
}
