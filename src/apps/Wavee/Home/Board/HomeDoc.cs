// ── Home/Board/HomeDoc.cs: the pinned Home document, v3, per account (design 1.2.1) ──────────────────────────────────
//
// Home is groups of items; an item is a source, a size and options. Immutable records with shared structure, so an undo
// entry is two references. The wire is HomeDoc.Wire.cs; the file and the live signal are HomeBoardStore.cs; the only way
// a document changes is HomeCommands.Apply.

using System.Collections.Immutable;

namespace Wavee;

public enum HomeSize : byte { Auto, S, M, L, Full }

public readonly record struct SeenRef(SectionRef Ref, long LastSeenMs);

/// <summary>What an item shows. <see cref="Kind"/> is the wire's <c>kind</c>.</summary>
public abstract record HomeSource(string Kind);

/// <summary>A Spotify section the user placed (took control of, kept, added).</summary>
public sealed record SpotifySectionSource(SectionRef Ref, string TitleHint) : HomeSource("spotifySection");

/// <summary>The catch-all: every section of the facet's answer that is not placed or hidden, in Spotify's order.</summary>
public sealed record SpotifyFeedSource(string Facet) : HomeSource("spotifyFeed");

/// <summary>Spotify's shortcuts that are not pinned yet, up to <see cref="Max"/>. <see cref="Of"/> = "spotify-shortcuts".</summary>
public sealed record FillSource(string Of, int Max) : HomeSource("fill");

/// <summary>A pinned entity or page. <see cref="PinId"/> is the canonical pin id (<see cref="HomePinId.Canonical"/>).</summary>
public sealed record PinSource(string PinId, string NameHint, string CoverHint) : HomeSource("pin");

/// <summary>A widget by name ("nowPlaying", "daylist", "recents", "upNext", "releases").</summary>
public sealed record WidgetSource(string Widget, string? Args) : HomeSource("widget");

/// <summary>A source this build does not know: kept byte for byte (<see cref="RawJson"/> is the whole source object)
/// and drawn as nothing.</summary>
public sealed record UnknownSource(string RawKind, string RawJson) : HomeSource(RawKind);

/// <summary>What one Spotify section drew as when its answer was last on hand: its key, the template its tile took (after the fold and the
/// rhythm), how many items it showed, whether it was a member of a fold (a one-card baseline section the projection gathers into one shelf), and
/// whether its header named an entity (the taller header: the entity's cover beside the lead words and its name).
/// A launch draws a section whose answer is on its way as a flat skeleton of this shape, at its place and its loaded size.</summary>
public readonly record struct SeedShape(string Key, SectionTemplate Template, int Count, bool Fold, bool Head = false);

/// <summary>One item. <see cref="Options"/>: "live" = "0" | "1"; unknown keys are kept.</summary>
public sealed record HomeItem(string Id, HomeSource Source, HomeSize Size,
    ImmutableSortedDictionary<string, string> Options)
{
    /// <summary>The shapes the item drew as when the answer was last on hand (the wire's <c>shape</c>): a placed section's one, the feed's every
    /// section it expanded to, in order. Written by <see cref="HomeBoardStore.RecordShapes"/>, never by a command (no undo step); empty until the
    /// first answer (a document written before shapes existed fills on its next answer). An older build keeps the member as an unknown one.</summary>
    public ImmutableArray<SeedShape> Shape { get; init; } = ImmutableArray<SeedShape>.Empty;

    /// <inheritdoc/>
    public bool Equals(HomeItem? o)
        => o is not null && Id == o.Id && Source.Equals(o.Source) && Size == o.Size && ReferenceEquals(Options, o.Options)
           && ReferenceEquals(Extra, o.Extra) && Shape.AsSpan().SequenceEqual(o.Shape.AsSpan());

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Id, Source, Size);

    /// <summary>Unknown members of the item object, kept as raw JSON (name → value) so a newer build's fields
    /// survive a round trip.</summary>
    public ImmutableSortedDictionary<string, string> Extra { get; init; } = ImmutableSortedDictionary<string, string>.Empty;
}

public sealed record HomeGroup(string Id, string? Title, string? TitleKey, ImmutableArray<HomeItem> Items);

public sealed record HomeDoc(int Version, long Rev, long UpdatedAtMs,
    ImmutableArray<HomeGroup> Groups, ImmutableArray<SectionRef> Hidden, ImmutableArray<SeenRef> Seen,
    ImmutableArray<string> LegacyHidden,      // v2 zone names waiting for the first answer (1.2.4)
    bool SeededFromSidebar)
{
    public const int CurrentVersion = 3;
    public const int MaxItems = 200, MaxSeen = 200;
    public const long SeenTtlMs = 90L * 24 * 3600 * 1000;

    /// <summary>The group with this id, or -1.</summary>
    public int GroupIndex(string groupId)
    {
        for (int i = 0; i < Groups.Length; i++) if (string.Equals(Groups[i].Id, groupId, StringComparison.Ordinal)) return i;
        return -1;
    }

    /// <summary>Where an item sits: (group, index), or (-1, -1).</summary>
    public (int Group, int Index) Find(string itemId)
    {
        for (int g = 0; g < Groups.Length; g++)
        {
            var items = Groups[g].Items;
            for (int i = 0; i < items.Length; i++)
                if (string.Equals(items[i].Id, itemId, StringComparison.Ordinal)) return (g, i);
        }
        return (-1, -1);
    }

    public int ItemCount
    {
        get { int n = 0; for (int g = 0; g < Groups.Length; g++) n += Groups[g].Items.Length; return n; }
    }

    /// <summary>Unknown top-level members of the file, kept as raw JSON so a newer build's fields survive.</summary>
    public ImmutableSortedDictionary<string, string> Extra { get; init; } = ImmutableSortedDictionary<string, string>.Empty;
}

public static class HomeDocs
{
    public const string Today = "today", Pinned = "pinned", Spotify = "spotify";
    public const string ShortcutsFill = "spotify-shortcuts";
    public const int FillMax = 8;

    // Fixed ids for the default items, so a reset lands on the same entries (and the inspector's keys are stable).
    public const string DaylistId = "d-daylist", FillId = "d-fill", RecentsId = "d-recents", FeedId = "d-feed";

    static readonly ImmutableSortedDictionary<string, string> s_noOptions = ImmutableSortedDictionary<string, string>.Empty;

    /// <summary>today: daylist | pinned: Fill(8), recents (full) | spotify: SpotifyFeed("").</summary>
    public static HomeDoc Default() => new(
        HomeDoc.CurrentVersion, 0, 0,
        [
            new HomeGroup(Today, null, "home.board.group.today",
                [new HomeItem(DaylistId, new WidgetSource("daylist", null), HomeSize.Auto, s_noOptions)]),
            new HomeGroup(Pinned, null, "home.board.group.pinned",
                [
                    new HomeItem(FillId, new FillSource(ShortcutsFill, FillMax), HomeSize.Auto, s_noOptions),
                    new HomeItem(RecentsId, new WidgetSource("recents", null), HomeSize.Full, s_noOptions),
                ]),
            new HomeGroup(Spotify, null, "home.board.group.spotify",
                [new HomeItem(FeedId, new SpotifyFeedSource(""), HomeSize.Auto, s_noOptions)]),
        ],
        [], [], [], false);

    /// <summary>A new item id: random, never an index.</summary>
    public static string NewItemId()
    {
        Span<byte> b = stackalloc byte[5];
        System.Security.Cryptography.RandomNumberGenerator.Fill(b);
        return "i" + Convert.ToHexStringLower(b);
    }
}
