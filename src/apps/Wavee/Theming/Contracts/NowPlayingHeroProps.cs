// ── Theming/Contracts/NowPlayingHeroProps.cs — the props of `Player.NowPlayingHero` and `Player.DeckStyleFlyout` (T6b) ───────
//
// THE NOW-PLAYING PANE. Its pinned slot is the hero tile (`Player.NowPlayingHero`): a header strip (eyebrow, the Cover | Player
// switch, the style gear) over EITHER the cover art OR the deck, with an art menu on the wrapper. The scrolling body under it
// (credits, the About card, cities, merch, Next up) is drawn by the panel's now-playing arm from the same record.
//
// WHAT STAYED CORE. The deck itself (`Deck.View()`: the twelve faces, the clock, the spectrum) arrives as a builder: the hero
// HOSTS it and never builds it, so a mode or theme switch re-mounts nothing the deck owns. Which face the hero paints is the
// core's decision (`Rail.PlayerPrefs.ResolvedPresentation()`, developer-gated as today); the preset table and its reactive reads are
// `Rail.PlayerCatalog` / `Rail.PlayerPrefs`. Every WRITE a view makes (a style, an option, the presentation, the flyout's open
// flag) goes through <see cref="IDeckStyleVerbs"/>, attributed to the surface that made it. The "Next up" rows are the `Panel.NextUp` key
// (`NextUp`); the lyrics peek is a core host that mounts `Player.LyricsPeek`, handed over as a builder; the entity subscriptions, the
// demand and the string table are verbs.
//
// WVTH006: sealed records with init-only members; the verbs object is one cached instance.

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Signals;

namespace Wavee.Theming.Contracts;

/// <summary>The surface that wrote a deck preference (the core logs it: without it "the style changed" is unattributable).</summary>
public enum DeckStyleSource : byte { Header, Flyout, ArtMenu }

/// <summary>Every write the now-playing pane's views make to the deck's content preferences.</summary>
public interface IDeckStyleVerbs
{
    /// <summary>Pick a player style from the flyout. Picking a player ALSO shows it.</summary>
    void SetStyle(int id);

    /// <summary>Pick one option of a style from the flyout (the index is clamped against the option's choices).</summary>
    void SetChoice(in Rail.PlayerCatalog.Preset preset, in Rail.PlayerCatalog.OptionDef option, int index);

    /// <summary>A controlled mirror of one option's choice for a segmented row: a fresh one per render ON PURPOSE (the option SET
    /// changes with the style, so a hook per option would be a conditional hook). The store, not the mirror, is the truth.</summary>
    Signal<int> ChoiceMirror(in Rail.PlayerCatalog.Preset preset, in Rail.PlayerCatalog.OptionDef option);

    /// <summary>Show the cover (<see cref="Rail.PlayerPrefs.Cover"/>) or the player (<see cref="Rail.PlayerPrefs.Player"/>).</summary>
    void SetPresentation(int presentation, DeckStyleSource source);

    /// <summary>Open the style flyout (the SAME popup the gear owns, from wherever it was asked).</summary>
    void OpenStyleFlyout();

    /// <summary>The gear: open the flyout if it is closed, close it if open.</summary>
    void ToggleStyleFlyout();
}

/// <summary>The props of <see cref="ViewKeys.DeckStyleFlyout"/>.</summary>
public sealed record DeckStyleFlyoutProps
{
    /// <summary>The writes the flyout makes.</summary>
    public required IDeckStyleVerbs Verbs { get; init; }

    /// <summary>The playing track's cover url, read live (the "Album colour" swatch paints the cover's grading).</summary>
    public required Func<string?> NowArtUrl { get; init; }
}

/// <summary>The props of <see cref="ViewKeys.NowPlayingHero"/> (and of the now-playing body the panel draws under it).</summary>
public sealed record NowPlayingHeroProps
{
    // ── the playing track ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The playing TRACK (subscribes the playable and the track table), or an invalid handle (an episode, a module
    /// playable or nothing).</summary>
    public required Func<Track> NowTrack { get; init; }

    /// <summary>The playing track's cover url, read live (a bound thunk that must follow a track change).</summary>
    public required Func<string?> NowArtUrl { get; init; }

    /// <summary>The panel's committed width (the hero's side is derived from it, quantised).</summary>
    public required IReadSignal<float> RailWidth { get; init; }

    // ── the hero ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The deck (core). The hero places it under the keyed wrapper and never builds it.</summary>
    public required Func<Element> Deck { get; init; }

    /// <summary>Every write the hero and its flyout make.</summary>
    public required IDeckStyleVerbs Style { get; init; }

    /// <summary>The style flyout's props (the gear's popup mounts <see cref="ViewKeys.DeckStyleFlyout"/> with them).</summary>
    public required DeckStyleFlyoutProps Flyout { get; init; }

    // ── the body under the hero ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Subscribe what the body reads of the entity tables (the scope epoch, artists, albums, cities, the queue edge).</summary>
    public required Action SubscribeEntities { get; init; }

    /// <summary>The rail is not a page and never mounts on a route: ask for the PLAYING track's identity and its first artist's
    /// overview.</summary>
    public required Action<int, int> DemandNowPlaying { get; init; }

    /// <summary>Is there any "Next up" row? The body decides whether the section mounts at all.</summary>
    public required Func<bool> HasNextUp { get; init; }

    /// <summary>The "Next up" rows (shared with the video body): the props the body mounts <see cref="ViewKeys.PanelNextUp"/> with.</summary>
    public required NextUpProps NextUp { get; init; }

    /// <summary>The lyrics peek under the track meta: the core host (the document, the clock, the pair) that mounts <see cref="ViewKeys.LyricsPeek"/>.</summary>
    public required Func<Element> LyricsPeek { get; init; }

    /// <summary>Resolve an interned string (a city, a country, a merch name, a price).</summary>
    public required Func<StringId, string> ResolveString { get; init; }

    /// <summary>Navigate to an entity (the artist, the album) under a display name.</summary>
    public required Action<EntityUri, string> GoTo { get; init; }
}
