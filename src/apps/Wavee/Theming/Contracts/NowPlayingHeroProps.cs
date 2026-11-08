// ── Theming/Contracts/NowPlayingHeroProps.cs — the props of `Player.NowPlayingHero` (T6b; the player style redesign 2026-11-08) ───────
//
// THE NOW-PLAYING PANE. Its pinned slot is the hero tile (`Player.NowPlayingHero`): a header ("Now playing" and the style CHIP naming the
// player style) over the STAGE, the art area that draws the style. The chip opens the in-place picker: the stage becomes a carousel of the
// eleven styles, the header reads "Player style" with Cancel and Done, and the style's name, its one-line description, its finish swatches
// (Vinyl, Pocket) and the dots sit under the stage. There is no pivot, no gear, no flyout and no option dial. The scrolling body under the
// hero (credits, the About card, cities, merch, Next up) is drawn by the panel's now-playing arm from the same record.
//
// WHAT STAYED CORE. The stage itself (`Deck.Stage`: the cover, the canvas, the deck faces, the carousel and its motion) arrives as a
// builder: the hero HOSTS it and never builds it, so a mode or theme switch re-mounts nothing a deck owns. The style table and its reactive
// reads are `Rail.PlayerCatalog` / `Rail.PlayerPrefs`; the picker's state is `Shell.StylePicker` (open, the focused style). Every WRITE a
// view makes (open, step, focus, a finish, Done, Cancel) goes through <see cref="IPlayerStyleVerbs"/>. The "Next up" rows are the
// `Panel.NextUp` key (`NextUp`); the lyrics peek is a core host that mounts `Player.LyricsPeek`, handed over as a builder; the entity
// subscriptions, the demand and the string table are verbs.
//
// WVTH006: sealed records with init-only members; the verbs object is one cached instance.

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Signals;

namespace Wavee.Theming.Contracts;

/// <summary>Every write the now-playing hero's style chip and picker make.</summary>
public interface IPlayerStyleVerbs
{
    /// <summary>The chip: open the picker on the stored style.</summary>
    void OpenPicker();

    /// <summary>The arrows: one style back (−1) or on (+1), round the ring.</summary>
    void Step(int delta);

    /// <summary>A dot: focus that style.</summary>
    void FocusOn(int styleId);

    /// <summary>A finish swatch of the focused style (Vinyl, Pocket).</summary>
    void SetFinish(int index);

    /// <summary>Done: store the focused style and close.</summary>
    void Done();

    /// <summary>Cancel (Escape too): close with the stored style and finishes as they were.</summary>
    void Cancel();
}

/// <summary>Builds the now-playing stage for a hero whose stage column sits inside <paramref name="chrome"/> DIP of horizontal padding (both
/// sides together: the carousel's neighbours peek into it). <paramref name="cover"/> builds the theme's cover image (a cached delegate);
/// <paramref name="corners"/> rounds the canvas as the cover is rounded.</summary>
public delegate Element StageView(float chrome, Func<Element> cover, CornerRadius4 corners);

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

    /// <summary>The stage (core): the art area that draws the player style and becomes the picker's carousel. The hero places it and never
    /// builds it. It measures its own column (a Compact sheet's is the window's, not the rail's); the CHROME (the hero's horizontal padding,
    /// both sides together) answers before the first layout and is where the neighbours peek.</summary>
    public required StageView Stage { get; init; }

    /// <summary>Every write the hero's chip and picker make.</summary>
    public required IPlayerStyleVerbs Style { get; init; }

    /// <summary>Is the style picker open? Reactive (a render that reads it re-renders when the picker opens or closes).</summary>
    public required Func<bool> Picking { get; init; }

    /// <summary>The style the chip names and the strip shows: the focused one while picking, the stored one otherwise (`Rail.PlayerCatalog`
    /// id). Reactive.</summary>
    public required Func<int> ShownStyle { get; init; }

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
