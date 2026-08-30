namespace Wavee.Core;

// The source-agnostic Home model has two synchronized views of a response: a lossless, ordered section ledger for
// accounting/drill-in, and typed presentation groups for Home's designed rhythm. Shape is keyed on content — entity type
// and Spotify's stable playlist `format`, never localized copy — while section title/URI ownership remains intact.

/// <summary>How a home group is laid out. The shape follows the CONTENT, not the section's localized copy: a Hero band
/// (daylist / spotlight), the QuickGrid opener, a finite horizontally paged Shelf, a Featured editorial break, and the
/// content-specific modules — a continuous MixBand of daily mixes, the WeeklyPair (Discover Weekly + Release Radar),
/// ChipCards for seed-listing mixes, a RadioDial of station rows, a RatedShelf of audiobooks, a QueueList of episodes,
/// and the DiscoverFeed that presents single-item baseline recommendations in one paged browse module at the tail.
/// <para>A section whose cards name no shape is never forced into one. It splits per card, and the shapeless remainder
/// lands in QuickGrid — which is why the landing lets that ONE module wear the section's server title when a single
/// section feeds it, instead of filing the title under the app's own "jump back in" copy. A section with no dominant
/// module at all keeps its title on a SectionEntry in the deck. Shelf is the source-neutral plain shelf, available to
/// sources that publish one directly; the Spotify composer emits the two fallbacks above instead.</para></summary>
public enum HomeGroupKind
{
    Hero, QuickGrid, Shelf, Featured,
    MixBand, WeeklyPair, ChipCards, RadioDial, RatedShelf, QueueList, DiscoverFeed,
    // Recently played has its own presentation contract: a paged MediaCard shelf with circular artist covers.
    Recents,
    // Source-section presentations. Topic is an editorial-majority section; SectionEntry is a mixed/undominated
    // section whose title cannot honestly belong to one of its preview modules. Both render in the section deck.
    Topic, SectionEntry,
    // Podcasts are shows, not loose quick-pick tiles. They have their own paged shelf and drill page.
    PodcastShelf,
}

/// <summary>What a home card points at — drives the nav route (pl: / album: / artist: / show: / liked) and the card
/// shape. Audiobook and Podcast are distinct kinds even though both carry a <c>spotify:show:</c> URI: they route the
/// same way but render differently (a rating cluster vs a publisher line).</summary>
public enum HomeCardKind { Playlist, Album, Artist, Track, Liked, Episode, Audiobook, Podcast }

/// <summary>The optional, content-specific facets of a home card — everything beyond "cover + two lines of text" that a
/// module needs to render its own shape. Bundled rather than widening <see cref="HomeCard"/>'s positional list (the
/// <c>ArtistExtras</c> precedent), so a card that has none simply carries null.
/// <para><paramref name="Format"/> is Spotify's stable playlist format token (<c>daylist</c>, <c>daily-mix</c>,
/// <c>inspiredby-mix</c>, <c>topic-mix</c>, <c>editorial</c>, …) — it is what the composer routes on, never the
/// localized title. <paramref name="Accent"/> is the server's <c>extractedColors.colorDark</c> as opaque ARGB (0 =
/// unknown): an IMMEDIATE, pre-decode accent, whereas <c>CoverColorPlane</c> stays the authority for the full graded
/// role set. <paramref name="Seeds"/> are the artist/tag chips a mix lists in its description (or a daylist's
/// <c>localized_terms</c>).</para></summary>
public sealed record HomeCardMeta(
    string? Format = null,
    uint Accent = 0,
    int TrackCount = 0,
    IReadOnlyList<string>? Seeds = null,
    // The playlist's OWNER, separately from its description. HomeCard.Subtitle is `description ?? ownerName` — one slot
    // for two different facts, which no consumer can tell apart: a card with a description rendered "50 songs · by
    // <a href=spotify:playlist:…>Tophyun</a> and more" because the caller believed the subtitle was an owner. The
    // description stays in Subtitle (it is what the card DISPLAYS); this is for the meta lines that need the owner.
    string? OwnerName = null,
    // Episode: total length, resume position, and whether the show ships a video track for this episode (the row's
    // artwork is 16:9 rather than square when it does — the shape previews the medium).
    long DurationMs = 0, long ResumeMs = 0, bool HasVideo = false,
    // Audiobook: the average rating (0 when the server withholds it), the author line, and the access signifier
    // ("Included in Premium").
    double Rating = 0, string? Author = null, string? Signifier = null,
    // Provider-declared generic label for a shallow identity (Spotify's `daylist_pretitle`). Kept separately so an
    // authoritative header that still equals the placeholder is not mistaken for successful hydration.
    string? GenericTitle = null,
    // True only when the provider explicitly identified this card as a shallow identity that needs one exact header
    // read before display. Spotify sets it for a daylist whose `name` is empty or byte-for-byte equal to its
    // `daylist_pretitle`; consumers must never synthesize a personalized title from the card's tags.
    bool NeedsHydration = false,
    // Unix ms when a generated playlist's content rolls over (Spotify daylist `expires` attribute). 0 = unknown /
    // not applicable.
    long ExpiresAtMs = 0,
    // Unix ms when the current content window began (daylist `created` attribute). 0 = unknown.
    long CreatedAtMs = 0,
    // Provider's authored full-bleed desktop header image (`header_image_url_desktop`), distinct from the square cover.
    string? HeaderImageUrl = null);

/// <summary>One home tile: a context URI + display metadata + its kind. Source-neutral (cover may be a remote CDN url).
/// <paramref name="MosaicTiles"/> (when <paramref name="Image"/> is null) carries up to 4 album-cover URLs for a 2×2
/// cover-less-playlist mosaic.</summary>
public sealed record HomeCard(string Uri, string Title, string? Subtitle, Image? Image, HomeCardKind Kind,
    System.Collections.Generic.IReadOnlyList<string>? MosaicTiles = null,
    // Optional eyebrow — the section context shown ABOVE the title on a Featured card (e.g. "For fans of IU",
    // "More like GFRIEND"). Carried from the baseline section's title, which the old composer discarded.
    string? Eyebrow = null,
    // Content-specific facets (format, accent, track count, episode/audiobook metadata). Null for a card whose source
    // publishes none — every consumer must treat it as optional.
    HomeCardMeta? Meta = null);

/// <summary>A titled group of home cards laid out per <see cref="HomeGroupKind"/>. The section accent is resolved by the
/// VIEW — from the first card's graded cover (CoverColorPlane) when that has landed, else from that card's
/// <see cref="HomeCardMeta.Accent"/>. A group's Title is always the SERVER's label, verbatim, or null when the section
/// carried none (the QuickGrid) or when it is the continuation of a split section.</summary>
public sealed record HomeGroup(
    HomeGroupKind Kind,
    string? Title,
    IReadOnlyList<HomeCard> Cards,
    string? Subtitle = null,
    string? Uri = null,
    int TotalCount = 0);

/// <summary>The lossless source-section ledger behind the typed Home modules. Cards retain response order and are
/// deduplicated only inside this section; the same URI in another section is another valid occurrence. Raw accounting is
/// explicit: <c>RawItemCount == Cards.Count + UnsupportedCount + DuplicateCount</c> for ordinary card sections.
/// <paramref name="TotalCount"/> is the server-side total and may be larger than the returned raw page.</summary>
public sealed record HomeSection(
    string? Uri,
    string? Title,
    string? Subtitle,
    IReadOnlyList<HomeCard> Cards,
    int TotalCount,
    int RawItemCount,
    int UnsupportedCount = 0,
    int DuplicateCount = 0);

/// <summary>One page of ONE home section (the "Show all" axis), as returned by Spotify's <c>homeSection</c> operation.
/// <paramref name="Section"/> is the same lossless ledger the inline Home sections produce, so a fetched page folds into
/// what Home already rendered without a second card model.
/// <para><paramref name="NextOffset"/> is the SERVER's own cursor (<c>sectionItems.pagingInfo.nextOffset</c>) and is the
/// only trustworthy terminator: null means "complete", and a value at or behind the offset just requested means the same
/// thing (a complete 6-of-6 section really does answer <c>nextOffset: 0</c>). <see cref="HomeSection.TotalCount"/> must
/// NOT be the terminator — it disagreed with the returned item count in 7 of 31 captured sections.</para></summary>
public sealed record HomeSectionPageResult(HomeSection Section, int? NextOffset);

/// <summary>One preview track of a home recommendation (the hover peek on a Featured editorial card): display name,
/// cover art, and an optional 30s MP3 preview URL. Source-neutral — Spotify fills it from feedBaselineLookup.</summary>
public sealed record HomePreviewTrack(string Uri, string Name, Image? Cover, string? PreviewUrl = null);

/// <summary>A home facet chip (Spotify <c>home.homeChips[]</c>). <paramref name="Id"/> is what goes back into the
/// <c>facet</c> request variable — it is an opaque server token ("music-chip"), never localised or synthesised.
/// <paramref name="Label"/> arrives already localised by the server. <paramref name="SubChips"/> is the second level
/// ("Following"), which is what lets a selected chip fuse into a two-segment pill.</summary>
public sealed record HomeChip(string Id, string Label, IReadOnlyList<HomeChip> SubChips);

/// <summary>What a live (server) home fetch returns to a catalog source: the editorial groups plus the facet chip row
/// plus the server's own greeting. Chips are null when the server sent none — the source then contributes groups only.
/// <paramref name="Greeting"/> is empty for a source that has no greeting to offer.</summary>
public sealed record LiveHomeResult(IReadOnlyList<HomeGroup> Groups, IReadOnlyList<HomeChip>? Chips,
    string Greeting = "", IReadOnlyList<HomeSection>? Sections = null)
{
    public static readonly LiveHomeResult Empty = new(System.Array.Empty<HomeGroup>(), null);
}

/// <summary>One source's contribution to the home feed (its groups), with a priority for ordering when merged across
/// sources by the aggregate (lower sorts first). <paramref name="Chips"/> is the source's facet row, if it has one.
/// <paramref name="Greeting"/> is the SERVER's greeting (Spotify's <c>home.greeting.transformedLabel</c>) — already
/// localized for the account, so it is never re-translated and never synthesised from the local clock. Empty means
/// "this source has none", which is what lets the aggregate fall through to the next source.</summary>
public sealed record HomeContribution(IReadOnlyList<HomeGroup> Groups, int Priority = 0,
    IReadOnlyList<HomeChip>? Chips = null, string Greeting = "", IReadOnlyList<HomeSection>? Sections = null);

/// <summary>The finished, merged Home model: greeting, typed presentation groups, facet chips, and the ordered section
/// ledger used by drill-in pages. <paramref name="Chips"/> is empty for sources without facets.</summary>
public sealed record HomeFeed(string Greeting, IReadOnlyList<HomeGroup> Groups,
    IReadOnlyList<HomeChip>? Chips = null, IReadOnlyList<HomeSection>? Sections = null)
{
    public static readonly HomeFeed Empty = new("", System.Array.Empty<HomeGroup>());
}
