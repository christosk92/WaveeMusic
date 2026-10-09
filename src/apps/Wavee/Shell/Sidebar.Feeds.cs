// ── Shell/Sidebar.Feeds.cs ─────────────────────────────────────────────────────────────────────────────────────────
// the two feed sections' data: Recently played (resolved synchronously from the play log) and New releases (the What's
// New feed), plus which feeds the active layout demands
//
// Role: CORE (the rules) + SHELL (SidebarResidentPeek, the one entity read)
// Spec: sidebar-rework-implementation.md §P3.6 · design P.3

using FluentGpu.Foundation;

namespace Wavee;

/// <summary>Which feed the active layout shows — the binder reads, kicks and folds a feed only while its section is
/// visible (design blocking item 3: "who fetches New releases, when, on what demand").</summary>
[System.Flags]
public enum SidebarFeedDemand : byte
{
    None = 0,
    /// <summary>Recently played: the play log (always folded) + the resident tables a peek reads.</summary>
    Recent = 1,
    /// <summary>New releases: the What's New feed (<c>Notify.Items</c>), refreshed at most every 30 minutes.</summary>
    NewReleases = 2,
}

public static class SidebarFeedDemands
{
    public static SidebarFeedDemand Of(SidebarLayoutDoc doc)
    {
        var d = SidebarFeedDemand.None;
        if (doc.Find(SidebarSectionKind.Recent) is { Hidden: false }) d |= SidebarFeedDemand.Recent;
        if (doc.Find(SidebarSectionKind.NewReleases) is { Hidden: false }) d |= SidebarFeedDemand.NewReleases;
        return d;
    }
}

/// <summary>A played context, newest first, with the title the play log recorded at play time (design P.3 step 1).</summary>
public readonly record struct SidebarPlayedContext(string Uri, SidebarEntryKind Kind, long PlayedAtMs, string? Title = null)
{
    public bool IsTrack => Kind == SidebarEntryKind.Track;
}

/// <summary>A resident row's identity WITHOUT a fetch: a played context was loaded to play, so its row is usually
/// resident (design P.3 step 2). An interface so the rule is tested without Entities.</summary>
public interface ISidebarEntityPeek
{
    bool TryPeek(string uri, out SidebarLibraryEntry entry);
}

/// <summary>RECENTLY PLAYED, the honest path (design P.3): each context resolves SYNCHRONOUSLY, in order — the library
/// projection, then a peek into the resident tables, then the play log's own title with the kind glyph — else it is
/// SKIPPED. Nothing arrives later, so there is no skeleton and rows never jump; a context nobody can name is invisible,
/// never a grey bone (D9).</summary>
public static class SidebarRecentsRules
{
    public static int Resolve(IReadOnlyList<SidebarPlayedContext>? contexts, SidebarSourceIndex index, ISidebarEntityPeek? peek,
                              int max, List<SidebarLibraryEntry> into)
    {
        into.Clear();
        if (contexts is null || max <= 0) return 0;
        for (int i = 0; i < contexts.Count && into.Count < max; i++)
        {
            var c = contexts[i];
            if (c.Uri.Length == 0 || c.IsTrack) continue;    // a bare track play is not a context
            string? id = EntityUri.IsLikedCollection(c.Uri) ? SidebarCatalogue.LikedRoute : SidebarPinId.FromUri(c.Uri);
            if (id is null || Contains(into, id)) continue;
            if (index.TryGet(id, out var known) && known.Name.Length > 0)
            {
                into.Add(known with { SortStamp = c.PlayedAtMs, SourceOrder = into.Count });
                continue;
            }
            if (peek is not null && peek.TryPeek(c.Uri, out var resident) && resident.Name.Length > 0)
            {
                into.Add(resident with { Id = id, Uri = c.Uri, SortStamp = c.PlayedAtMs, SourceOrder = into.Count });
                continue;
            }
            if (c.Title is { Length: > 0 } title)
                into.Add(new SidebarLibraryEntry(id, id == SidebarCatalogue.LikedRoute ? SidebarEntryKind.AppRoute : c.Kind,
                    c.Uri, title, "", StringId.Empty, null, ChildCount: 0, AddedAtMs: 0, SortStamp: c.PlayedAtMs,
                    LastVisitedTicksUtc: 0, SourceOrder: into.Count, Depth: 0, Circular: c.Kind == SidebarEntryKind.Artist,
                    Flavor: SidebarPlaylistFlavor.None)
                { FolderId = "", FolderName = "", FirstArtistName = "", IdentityKnown = true });
        }
        return into.Count;
    }

    static bool Contains(List<SidebarLibraryEntry> list, string id)
    {
        for (int i = 0; i < list.Count; i++) if (string.Equals(list[i].Id, id, System.StringComparison.Ordinal)) return true;
        return false;
    }
}

/// <summary>NEW RELEASES from the What's New feed the notification centre already fetches (<c>Home.Feeds.Refresh</c> →
/// <c>Notify.Items</c>). The section never fetches on its own schedule: it asks the feed owner at most every
/// <see cref="RefreshEveryMs"/> while it is shown, and reads whatever the feed holds.</summary>
public static class SidebarNewReleasesRules
{
    public const long RefreshEveryMs = 30L * 60L * 1000L;

    /// <summary>Ask the feed owner now? Never while it is loading; at once when it has never answered; then every 30 min.</summary>
    public static bool ShouldRefresh(long lastAskedTicks, long nowTicks, Notify.FeedState state)
    {
        if (state == Notify.FeedState.Loading) return false;
        if (lastAskedTicks == 0 || state == Notify.FeedState.Idle) return true;
        return nowTicks - lastAskedTicks >= RefreshEveryMs;
    }

    /// <summary>The feed's release rows, newest first, as entries: a release the projection knows is its real entry
    /// (stamped with the release time); one it does not is built from the feed row's own title, image and creator; a row
    /// with neither a known entry nor a title is skipped.</summary>
    public static int Fill(IReadOnlyList<Notification>? feed, SidebarSourceIndex index, List<SidebarLibraryEntry> into, int max)
    {
        into.Clear();
        if (feed is null || max <= 0) return 0;
        for (int i = 0; i < feed.Count && into.Count < max; i++)
        {
            var n = feed[i];
            if (n.Category != NotifyCategory.NewRelease || !n.Subject.IsValid) continue;
            string uri = n.Subject.Text;
            if (SidebarPinId.FromUri(uri) is not { } id) continue;
            if (index.TryGet(id, out var known) && known.Name.Length > 0)
            {
                into.Add(known with { SortStamp = n.TimestampMs, SourceOrder = into.Count });
                continue;
            }
            if (n.Title.Length == 0) continue;
            var kind = n.ReleaseKind == NewReleaseKind.Episode ? SidebarEntryKind.Show : SidebarEntryKind.Album;
            into.Add(new SidebarLibraryEntry(id, kind, uri, n.Title, n.Creator ?? "",
                n.ImageUrl is { Length: > 0 } url ? Entities.Strings.Intern(url) : StringId.Empty, null,
                ChildCount: 0, AddedAtMs: 0, SortStamp: n.TimestampMs, LastVisitedTicksUtc: 0, SourceOrder: into.Count,
                Depth: 0, Circular: false, Flavor: SidebarPlaylistFlavor.None)
            { FolderId = "", FolderName = "", FirstArtistName = n.Creator ?? "", IdentityKnown = true });
        }
        return into.Count;
    }

    /// <summary>How many releases arrived since the user last looked (the section's InfoBadge).</summary>
    public static int NewSince(IReadOnlyList<SidebarLibraryEntry> releases, long seenMs)
    {
        int n = 0;
        for (int i = 0; i < releases.Count; i++) if (releases[i].SortStamp > seenMs) n++;
        return n;
    }
}

/// <summary>The production peek: the current scope's tables, read only when the row's identity already landed. Never an
/// Ensure, never a slot allocation (<c>TryGetSlot</c>).</summary>
public sealed class SidebarResidentPeek : ISidebarEntityPeek
{
    public static readonly SidebarResidentPeek Instance = new();

    public bool TryPeek(string uri, out SidebarLibraryEntry entry)
    {
        entry = default;
        var scope = Entities.Current;
        switch (EntityUri.KindOf(uri))
        {
            case EntityKind.Playlist when scope.Playlists.TryGetSlot(uri.AsSpan(), out int ps):
            {
                var p = new Playlist(ps);
                if (!p.Knows(PlaylistFields.Identity)) return false;
                entry = Make(SidebarEntryKind.Playlist, uri, Entities.Strings.Resolve(p.TitleId), Entities.Strings.Resolve(p.Owner.NameId), p.ImageId, false);
                return true;
            }
            case EntityKind.Album when scope.Albums.TryGetSlot(uri.AsSpan(), out int als):
            {
                var a = new Album(als);
                if (!a.Knows(AlbumFields.Identity)) return false;
                entry = Make(SidebarEntryKind.Album, uri, a.Title, "", a.ImageId, false);
                return true;
            }
            case EntityKind.Artist when scope.Artists.TryGetSlot(uri.AsSpan(), out int ars):
            {
                var ar = new Artist(ars);
                if (!ar.Knows(ArtistFields.Identity)) return false;
                entry = Make(SidebarEntryKind.Artist, uri, ar.Name, "", ar.ImageId, true);
                return true;
            }
            case EntityKind.Show when scope.Shows.TryGetSlot(uri.AsSpan(), out int ss):
            {
                var s = new Show(ss);
                if (!s.Knows(ShowFields.Identity)) return false;
                entry = Make(SidebarEntryKind.Show, uri, s.Title, "", s.ImageId, false);
                return true;
            }
            default:
                return false;
        }
    }

    static SidebarLibraryEntry Make(SidebarEntryKind kind, string uri, string name, string creator, StringId cover, bool circular)
        => new("", kind, uri, name, creator, cover, null, ChildCount: 0, AddedAtMs: 0, SortStamp: 0, LastVisitedTicksUtc: 0,
            SourceOrder: 0, Depth: 0, Circular: circular, Flavor: SidebarPlaylistFlavor.None)
        { FolderId = "", FolderName = "", FirstArtistName = "", IdentityKnown = true };
}
