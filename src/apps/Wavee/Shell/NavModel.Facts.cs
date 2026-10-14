// ── Shell/NavModel.Facts.cs ────────────────────────────────────────────────────────────────────────────────────────
// the facts a nav row draws from, decided once: online, the label fallbacks, the age badge and its clock, the shortcut
// count, the liked cover ids, and the persisted folder expansion
//
// Role: CORE · Plan: technical-plan.md §5.1 row C12 (BP X31)
//
// A row reads these instead of reaching into `Spotify`, the entity tables or the wall clock. The Classic view draws them
// (`ClassicNavRow.cs`, `Sidebar.UI.Rows.cs`): the label's words, the number's pixels and the mosaic's tiles are its.

using FluentGpu.Foundation;
using FluentGpu.Localization;

namespace Wavee;

/// <summary>What a row is called, and how much of that is a stand-in: the label, the pin's state, and whether the label is a
/// placeholder noun ("Playlist") a pin shows until something names it.</summary>
public readonly record struct NavRowLabel(string Label, SidebarPinState PinState, bool PlaceholderTitle);

/// <summary>The first four DISTINCT liked cover ids, newest first, with the scope epoch they were read under.</summary>
public readonly record struct NavLikedCovers(uint Epoch, int Found, StringId A, StringId B, StringId C, StringId D);

/// <summary>The pure row facts.</summary>
public static class NavRowFacts
{
    /// <summary>The last resort when nothing names a row: the uri's / key's final segment. Never blank, never a crash — a
    /// hand-edited document must render something the user can right-click and remove.</summary>
    public static string ShortUri(string? uri)
    {
        if (string.IsNullOrEmpty(uri)) return "—";
        int at = uri.LastIndexOf(':');
        return at >= 0 && at + 1 < uri.Length ? uri[(at + 1)..] : uri;
    }

    /// <summary>A release's compact age ("3d" / "2w" / "4mo"): digits + a unit letter (no loc key exists for it). Null for no
    /// stamp or a future one (not an age).</summary>
    public static string? AgeBadge(long epochMs, long nowMs)
    {
        if (epochMs <= 0) return null;
        long days = (nowMs - epochMs) / 86_400_000L;
        if (days < 0) return null;
        if (days < 1) return "1d";
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (days < 7) return days.ToString(inv) + "d";
        if (days < 60) return (days / 7).ToString(inv) + "w";
        return (days / 30).ToString(inv) + "mo";
    }

    /// <summary>The label an entity row shows. A PIN is never a blank row (design D9): while nothing has named it, the kind's noun
    /// shows ("Playlist" is honest where a uri fragment reads as data). An authoritative miss keeps the last-known name, dimmed,
    /// with the warning mark (design Q9). A non-pin row keeps the identity gate (Trap 5) on its uri fallback.</summary>
    public static NavRowLabel LabelOf(in SidebarLibraryEntry entry, bool online)
    {
        bool named = entry.Name.Length > 0;
        bool routeEntry = entry.Kind == SidebarEntryKind.AppRoute;
        bool pinned = entry.IsPinned;
        var pinState = pinned
            ? SidebarPinStateRules.Of(entry.IdentityKnown || named, entry.Missing, online)
            : SidebarPinState.Resolved;
        string label = named ? entry.Name
            : routeEntry ? Shell.Dest(Shell.Parse(entry.Id)).Title
            : pinned ? (SidebarPinStateRules.FallbackTitleKey(entry.Kind) is { } noun ? Loc.Get(noun) : ShortUri(entry.Uri))
            : SidebarProjection.ShouldShowUriFallbackTitle(entry.IsPinned, entry.IdentityKnown)
                ? ShortUri(entry.Uri)
                : "";
        return new NavRowLabel(label, pinState, pinned && !named && !routeEntry);
    }
}

public sealed partial class NavModel
{
    /// <summary>Is the session online? An unavailable pin is "offline" only while it is not.</summary>
    public bool IsOnline => Spotify.Current.IsOnline;

    /// <summary>The wall clock the age badge reads (unix ms). A test replaces it.</summary>
    public Func<long> Clock { get; set; } = static () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>A release's compact age against <see cref="Clock"/>.</summary>
    public string? AgeBadge(long epochMs) => epochMs <= 0 ? null : NavRowFacts.AgeBadge(epochMs, Clock());

    /// <summary>The label of an entity row, against <see cref="IsOnline"/>.</summary>
    public NavRowLabel LabelOf(in SidebarLibraryEntry entry) => NavRowFacts.LabelOf(in entry, IsOnline);

    /// <summary>Persist what a coalesced folder-expansion write deferred (the click frame must not also snapshot and serialize
    /// the account file; the pane drains this on the next frame). Idempotent; a no-op when nothing is pending.</summary>
    public void FlushPendingCommit() => Sidebar.FlushPendingCommit();

    /// <summary>The library shortcut's count stamp: the scope epoch FIRST (G-179 — the welcome switch re-points every table
    /// read below), then the relation's publish counter (the wake), folded into what the badge paints. SUBSCRIBES the caller.</summary>
    public Sidebar.ShortcutCount.Stamp ReadShortcutCount(LibraryEdgeKind kind)
    {
        uint epoch = Entities.ScopeEpoch.Value;
        var relation = User.Relation(kind);
        _ = relation.Changed.Value;
        var me = User.Me;
        return new Sidebar.ShortcutCount.Stamp(epoch, me.State(kind), relation.Total(me.Slot));
    }

    /// <summary>The first four distinct liked cover ids (newest first; four likes off one album are one cover, not a mosaic).
    /// Identity by interned image id: equal text is one id, so this is the url comparison without resolving. SUBSCRIBES the
    /// caller to the liked relation and the track table — read it inside a value gate, so a page's covers streaming in do not
    /// re-compose the mosaic unless one of the four changed.</summary>
    public NavLikedCovers ReadLikedCovers()
    {
        uint epoch = Entities.ScopeEpoch.Value;          // a boot / scope switch is the wake before any table exists
        var scope = Entities.Current;
        if (scope is null) return new NavLikedCovers(epoch, 0, default, default, default, default);
        _ = scope.Edges.Liked.Changed.Value;
        _ = scope.Tracks.Changed.Value;

        var slots = User.Me.LikedTrackSlots;
        StringId a = default, b = default, c = default, d = default;
        int found = 0;
        for (int i = 0; i < slots.Length && found < 4 && i < 64; i++)
        {
            var id = new Track(slots[i]).ImageId;
            if (id.IsEmpty || id == a || id == b || id == c) continue;
            switch (found++)
            {
                case 0: a = id; break;
                case 1: b = id; break;
                case 2: c = id; break;
                default: d = id; break;
            }
        }
        return new NavLikedCovers(epoch, found, a, b, c, d);
    }
}

// The pure half of the library-shortcut count badge lives under the name the tests and the view already use.
public static partial class Sidebar
{
    /// <summary>The PURE half of the library-shortcut count badge (W3-A2): which relation a shortcut route counts, what
    /// the badge shows for a relation state, and the stamp the live badge gates its render on. Public so the rules are
    /// pinned by facts (<c>SidebarWiringTests</c>) without an engine.</summary>
    public static class ShortcutCount
    {
        /// <summary>Exactly what the badge paints: an UNKNOWN state is the pending plate whatever the total says, any other
        /// state is the total. The epoch is part of the value so a scope switch that lands on the same number still
        /// re-points the memo at the new scope's signals (its read of <c>ScopeEpoch</c> is the wake; the equality is what
        /// decides the render).</summary>
        public readonly record struct Stamp(uint Epoch, EdgeState State, int Total);

        /// <summary>The relation a shortcut route counts, or null for a route that carries no count (Local files, Home…).</summary>
        public static LibraryEdgeKind? KindOf(string routeKey) => routeKey switch
        {
            "albums" => LibraryEdgeKind.SavedAlbums,
            "artists" => LibraryEdgeKind.FollowedArtists,
            "liked" => LibraryEdgeKind.Liked,
            "podcasts" => LibraryEdgeKind.SavedShows,
            _ => null,
        };

        /// <summary>The number the badge shows, or null for the pending plate: the server TOTAL while a relation pages in
        /// (so the number never climbs), nothing while nobody has answered.</summary>
        public static int? Shown(EdgeState state, int total) => state == EdgeState.Unknown ? null : total;

        /// <inheritdoc cref="Shown(EdgeState, int)"/>
        public static int? Shown(in Stamp stamp) => Shown(stamp.State, stamp.Total);

        /// <summary>The live badge's reconciler key — one literal per kind, so a route row's trailing slot never allocates
        /// a key string per render.</summary>
        public static string KeyOf(LibraryEdgeKind kind) => kind switch
        {
            LibraryEdgeKind.SavedAlbums => "count:albums",
            LibraryEdgeKind.FollowedArtists => "count:artists",
            LibraryEdgeKind.Liked => "count:liked",
            LibraryEdgeKind.SavedShows => "count:podcasts",
            _ => "count:other",
        };
    }
}
