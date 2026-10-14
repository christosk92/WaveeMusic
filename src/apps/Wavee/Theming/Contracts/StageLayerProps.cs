// ── Theming/Contracts/StageLayerProps.cs — the props of the stage's layout layers (themes plan 6.0, T7a2) ─────────────────────
//
// THE LAYERS ARE CORE DECISIONS AND THEME PICTURES. The look (which layout, which spectrum, which face, whether lyrics sit over the
// photo) is decided by the core allocator and carried by `Stage.StageCtx`'s signals (`Layout`, `Look`, `Mode`, `Kind`, `Accent`, ...);
// a template reads them inside its own render, so it re-renders on exactly what it read, and draws. What it may not name (`Entities`,
// `Playback`, `Shell`, `Prefs`) arrives as a predicate or a fact the template calls inside its render: a signal read there subscribes
// the layer, exactly as the core component that used to draw it subscribed on its own render.
//
// Every record is built ONCE per stage context (`Shell\Stage.Layers.cs`): the props stay equal across the surface's re-renders, so a
// layer re-renders only on the signals it read.

using FluentGpu.Controls;

namespace Wavee.Theming.Contracts;

/// <summary>The props of a layer that needs nothing but the stage context (<see cref="ViewKeys.StageFace"/>, <see cref="ViewKeys.StageSpectrum"/>).</summary>
public sealed record StageLayerProps
{
    public required Stage.StageCtx Stage { get; init; }
}

/// <summary>The props of <see cref="ViewKeys.StageBackdrop"/>.</summary>
public sealed record StageBackdropProps
{
    public required Stage.StageCtx Stage { get; init; }

    /// <summary>The backdrop is drawn (false under an opaque Artist header photo, where its blobs would raster for nothing); read inside a bound channel.</summary>
    public required Func<bool> Shown { get; init; }
}

/// <summary>The props of <see cref="ViewKeys.StageFaceScrim"/>.</summary>
public sealed record StageFaceScrimProps
{
    public required Stage.StageCtx Stage { get; init; }

    /// <summary>The veil's alpha over the backdrop face (0 outside Visualizer mode); read inside the template's render.</summary>
    public required Func<float> Alpha { get; init; }
}

/// <summary>The props of <see cref="ViewKeys.StageNowPlayingCard"/>.</summary>
public sealed record StageNowPlayingCardProps
{
    public required Stage.StageCtx Stage { get; init; }

    /// <summary>The card is mounted (Visualizer mode, a layout that has one, room for the chips); read inside the Show's predicate.</summary>
    public required Func<bool> Shown { get; init; }
}

/// <summary>The props of <see cref="ViewKeys.StageHero"/>.</summary>
public sealed record StageHeroProps
{
    public required Stage.StageCtx Stage { get; init; }

    /// <summary>The playhead is in the last seconds of the track: the next track's header decodes hidden under the hero (a playback read, made inside the render).</summary>
    public required Func<bool> PrefetchDue { get; init; }
}

/// <summary>The props of <see cref="ViewKeys.StageArtistName"/>.</summary>
public sealed record StageArtistNameProps
{
    public required Stage.StageCtx Stage { get; init; }

    /// <summary>The name of the artist whose photo shows (the first billed artist while the overview is pending), "" when unknown. The call
    /// subscribes to the entity tables it reads.</summary>
    public required Func<string> Name { get; init; }
}

/// <summary>The props of <see cref="ViewKeys.StageIdentity"/>: the title, the "Artist · Album" line and the chips are facts read inside
/// the template's (and its leaves') renders; the right-click menu and the artist link are verbs.</summary>
public sealed record StageIdentityProps
{
    public required Stage.StageCtx Stage { get; init; }

    /// <summary>The title shown (the playing track's, or the "Nothing playing" sentence).</summary>
    public required Func<string> Title { get; init; }

    /// <summary>The "Artist · Album" line for the given hero-artist slot (0 = every billed artist; otherwise the OTHER billed artists, "with …").</summary>
    public required Func<int, StageMetaFacts> Meta { get; init; }

    /// <summary>The chips under the title: the stream format, "Synced lyrics" and (outside Visualizer mode) BPM.</summary>
    public required Func<IReadOnlyList<StageChipFact>> Chips { get; init; }

    /// <summary>The now-playing context menu (asked when the right-click opens; null = none).</summary>
    public required Func<ContextMenuModel?> NowPlayingMenu { get; init; }

    /// <summary>Open the playing track's artist: closes the stage first, then navigates. The argument is the shared-element <c>Begin</c> from the template's context.</summary>
    public required Action<Action<string>?> OpenArtist { get; init; }
}

/// <summary>The "Artist · Album" line and whether it links to an artist page.</summary>
/// <param name="Line">The text, "" when there is nothing to show.</param>
/// <param name="Enabled">The artist has a page to open.</param>
public readonly record struct StageMetaFacts(string Line, bool Enabled);

/// <summary>One chip under the title.</summary>
/// <param name="Glyph">The icon glyph, null for a text-only chip.</param>
/// <param name="Text">The chip's text.</param>
public readonly record struct StageChipFact(string? Glyph, string Text);

// ── the Artist pane ─────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>The props of <see cref="ViewKeys.StageArtistPane"/>. Each <c>Func</c> subscribes to the entity tables it reads, so a
/// section re-renders only when its own tables publish; a null answer is "no section" (the pane leaves no gap for it).</summary>
public sealed record StageArtistPaneProps
{
    public required Stage.StageCtx Stage { get; init; }

    /// <summary>The artist and track the pane shows (the rail's resolver); subscribes to the track key, the scope and the credits edge.</summary>
    public required Func<StageArtistTarget> Target { get; init; }

    /// <summary>Ask for what the pane paints once per (artist, track, scope): the overview, the popular and related edges, the credits.</summary>
    public required Action<int, int> Demand { get; init; }

    public required Func<int, StageArtistHeaderFacts?> Header { get; init; }

    /// <summary>The About block for an artist slot; a bio link opens through <see cref="OpenLink"/>.</summary>
    public required Func<int, StageArtistAboutFacts?> About { get; init; }

    /// <summary>The track's credits grouped by person, for a track slot.</summary>
    public required Func<int, IReadOnlyList<Stage.ArtistRules.CreditPerson>?> Song { get; init; }

    public required Func<int, StageArtistPopularFacts> Popular { get; init; }

    public required Func<int, IReadOnlyList<StageArtistCityRow>?> Cities { get; init; }

    public required Func<int, StageArtistFansFacts> Fans { get; init; }

    /// <summary>Ask for the top tracks' rows / the related artists' identities (run once per edge version).</summary>
    public required Action<int> EnsurePopular { get; init; }

    public required Action<int> EnsureRelated { get; init; }

    /// <summary>Open an artist page (closes the stage first); the first argument is the shared-element <c>Begin</c>.</summary>
    public required Action<Action<string>?, int> GoToArtist { get; init; }

    /// <summary>Open a bio link (closes the stage first); the second argument is the link's key.</summary>
    public required Action<Action<string>?, string> OpenLink { get; init; }
}

/// <summary>The artist the pane shows. <paramref name="Valid"/> false = nothing playing / no artist (the pane says so).</summary>
public readonly record struct StageArtistTarget(bool Valid, int Artist, int Track, uint Epoch);

/// <summary>The header band's facts.</summary>
/// <param name="Name">The artist's name.</param>
/// <param name="Verified">Show the verified badge.</param>
/// <param name="Meta">"listeners · rank", "" when neither is known.</param>
/// <param name="TourEyebrow">The tour line's eyebrow, "" when it has none.</param>
/// <param name="TourHeadline">The tour headline, null when there is no tour line.</param>
/// <param name="Uri">The artist's uri, "" when it has none (no Follow button).</param>
/// <param name="PhotoUrl">The header photo's url (null: the artwork's placeholder).</param>
public sealed record StageArtistHeaderFacts(string Name, bool Verified, string Meta, string TourEyebrow, string? TourHeadline, string Uri, string? PhotoUrl);

/// <summary>The About block's facts.</summary>
/// <param name="Bio">The whole biography (rich text), "" when there is none.</param>
/// <param name="BioChars">The bio's plain-text length (asked once per bio).</param>
/// <param name="GalleryUrls">The gallery strip's photo urls (at most the pane's tile count).</param>
public sealed record StageArtistAboutFacts(string Bio, int BioChars, IReadOnlyList<string?> GalleryUrls);

/// <summary>The Popular list's facts. <paramref name="Epoch"/> and <paramref name="Version"/> key the pane's one demand effect.</summary>
public sealed record StageArtistPopularFacts(uint Epoch, uint Version, bool ShowArt, IReadOnlyList<StageArtistPopularRow> Rows);

/// <summary>One Popular row.</summary>
/// <param name="Index">Its place in the chart (0-based).</param>
/// <param name="Now">It is the playing track.</param>
/// <param name="Playing">It is the playing track and playback runs.</param>
/// <param name="ImageUrl">The cover's url, null with the artwork hidden or none.</param>
/// <param name="Title">The title.</param>
/// <param name="Explicit">Show the explicit badge.</param>
/// <param name="Plays">The play-count label, "" when unknown.</param>
/// <param name="Duration">The duration clock, "" when unknown.</param>
/// <param name="Click">Play the chart from this row (the deck row toggles).</param>
public sealed record StageArtistPopularRow(int Index, bool Now, bool Playing, string? ImageUrl, string Title, bool Explicit, string Plays, string Duration, Action Click);

/// <summary>One "Where people listen" row.</summary>
/// <param name="City">The city's name.</param>
/// <param name="Listeners">The compact listener count.</param>
/// <param name="Fraction">Its share of the loudest city, 0..1.</param>
public readonly record struct StageArtistCityRow(string City, string Listeners, float Fraction);

/// <summary>The "Fans also like" facts. <paramref name="Total"/> is every related artist (the fit counts against it); <paramref name="Fans"/>
/// are the ones with a name, in order.</summary>
public sealed record StageArtistFansFacts(uint Epoch, uint Version, int Total, IReadOnlyList<StageArtistFan> Fans);

/// <summary>One related artist.</summary>
public readonly record struct StageArtistFan(int Slot, string Name, string? ImageUrl);
