// ── Home/Board/HomeModel.cs: the pinned Home's shared model types ──────────────────────────────────────────────────
//
// Read by the document (HomeDoc), the reducer (HomeCommands), the projection (HomeProjection), the data lane's identity
// and chooser (SectionIdentity, SectionChooser) and the theme contracts (HomeBoardProps): the way Zone and ScreenModel
// live in Home/Model.cs and are read by HomeZoneProps. Model types, not contracts: nothing here draws.

using System.Collections.Immutable;

namespace Wavee;

/// <summary>A Spotify section's identity (design 2.2): <see cref="Key"/> the section uri, <see cref="Fp"/> a structural
/// fingerprint (FNV-1a 64 of typename, title template, header entity, dominant card kind and format), <see cref="Family"/>
/// typename and title template ("hide all like this").</summary>
public readonly record struct SectionRef(string Key, ulong Fp, string Family);

/// <summary>Which body draws a Spotify section (design 2.3, spec 3.1). Chosen from structure only.</summary>
public enum SectionTemplate : byte
{
    None, ShortcutFill, RecentsData, DaylistHero, CoverShelf, EntityShelf, MoodPlates, WideTiles, ReleaseList,
    ShowGrid, VideoTiles, EpisodeRows, BookShelf, SpineShelf, PlainShelf,
    /// <summary>Home rhythm (solution A): a personal set as one lead card beside a small grid of the rest.</summary>
    LeadGrid,
    /// <summary>Home rhythm: small tiles, the cover beside the text, in two to five columns.</summary>
    CompactGrid,
    /// <summary>Home rhythm: a row of round covers with the name centred under each (artist sets, artist stations).</summary>
    ArtistCircles,
    /// <summary>Home rhythm: one capped row of cards with two title lines and "See all N" instead of a sideways scroll.</summary>
    CappedShelf,
    /// <summary>A followed artist's new release (an untitled one-album baseline section): one spotlight card, the cover, the release's words,
    /// its canvas and its songs' previews; a run of them folds into one spotlight with pages (Home/ReleaseSpotlight).</summary>
    ReleaseSpotlight,
}

/// <summary>What one board entry is. <see cref="GroupHead"/> heads a group; <see cref="Item"/> is a document item
/// (a widget, a pin, a placed section); <see cref="FeedSection"/> a section the feed item expanded to (id
/// <c>feed:&lt;key&gt;</c>); <see cref="FillCell"/> a shortcut the fill expanded to (id <c>fill:&lt;uri&gt;</c>);
/// <see cref="Fold"/> a run of one-card sections folded into one preview grid; <see cref="Placeholder"/> a feed
/// section before the first answer.</summary>
public enum BoardEntryKind : byte { GroupHead, Item, FeedSection, FillCell, Fold, Placeholder }

/// <summary>Where Spotify's answer for the feed stands: on hand, still coming, failed, or out of reach offline. Anything but
/// <see cref="Ready"/> draws the feed slot as <see cref="BoardEntryKind.Placeholder"/> entries carrying it (the board's own
/// vacancy) while pins and local widgets keep working.</summary>
public enum HomeFeedState : byte { Ready, Loading, Error, Offline }

/// <summary>One entry of the board, in reading order. <see cref="Id"/> is the entry's identity across projections and
/// the tail of its node key (<c>home:tile:&lt;Id&gt;</c>, <c>home:group:&lt;GroupId&gt;</c> for a head).
/// <see cref="Source"/> is null for a group head and for an unknown source; <see cref="Raw"/> is the document's source
/// (a synthetic <see cref="SpotifySectionSource"/> for a feed entry). <see cref="Size"/> is the CHOSEN size (Auto when
/// the user never chose). <see cref="UserPin"/> marks the user's own pins (a fill cell is not one); <see cref="OnlyHide"/>
/// says Remove hides this entry (a feed section, a fill cell). <see cref="SectionSlot"/> is the Section row of a feed
/// or placed section in the current answer, -1 otherwise. <see cref="Hidden"/>: a placed section the answer lacks; it
/// keeps its place and takes no room.</summary>
public sealed record BoardEntry(string Id, BoardEntryKind Kind, string GroupId, HomeSourceDef? Source,
    HomeSource? Raw, HomeSize Size, SectionTemplate Template, string Title, bool HasOptions, bool Live,
    bool Paused, bool UserPin, bool OnlyHide, int SectionSlot)
{
    /// <summary>A placed section the current answer does not carry: a Hidden pack entry that keeps its place.</summary>
    public bool Hidden { get; init; }

    /// <summary>The item's options as saved (empty for entries the document does not hold yet).</summary>
    public ImmutableSortedDictionary<string, string> Options { get; init; } = ImmutableSortedDictionary<string, string>.Empty;

    /// <summary>A <see cref="BoardEntryKind.Fold"/>'s sections, in order (empty for every other kind).</summary>
    public ImmutableArray<SectionRef> Members { get; init; } = ImmutableArray<SectionRef>.Empty;

    /// <summary>The bridge's zone for a feed or placed section (until the chooser lands; then the section tile builds
    /// its own from <see cref="SectionSlot"/>). Null for every other kind.</summary>
    public HomeUi.Zone? Zone { get; init; }

    /// <summary>The templates a feed section may take instead of <see cref="Template"/> when its neighbour already draws that one
    /// (<see cref="SectionRhythm"/>), best first; empty where the structure allows no other.</summary>
    public ImmutableArray<SectionTemplate> Alternates { get; init; } = ImmutableArray<SectionTemplate>.Empty;

    /// <summary>A <see cref="BoardEntryKind.FillCell"/>'s Fill item (the document item that drew it); null for every other kind.</summary>
    public string? Parent { get; init; }

    /// <summary>A <see cref="BoardEntryKind.Placeholder"/>'s reason: the feed's state (Loading before the first answer, Error, Offline). On a
    /// <see cref="BoardEntryKind.FillCell"/>, Loading marks a cell that holds a shortcut's place until the answer names it.</summary>
    public HomeFeedState Feed { get; init; }
}

/// <summary>The board as projected right now: every group head and every entry in reading order. <see cref="Rev"/> is
/// the document's revision it was projected from.</summary>
public sealed record BoardModel(ImmutableArray<BoardEntry> Entries, long Rev)
{
    public static readonly BoardModel Empty = new(ImmutableArray<BoardEntry>.Empty, 0);

    /// <summary>The entry with this id, or -1.</summary>
    public int IndexOf(string id)
    {
        var e = Entries;
        for (int i = 0; i < e.Length; i++) if (string.Equals(e[i].Id, id, StringComparison.Ordinal)) return i;
        return -1;
    }
}
