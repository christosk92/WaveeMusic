using FluentGpu.Dsl;
using FluentGpu.Hooks;
using Wavee.Core;

namespace Wavee;

/// <summary>
/// The ONE shell-facing call for a Now Playing "player" deck: a square element that fills the Details rail's hero
/// slot, exactly where <c>NowPlayingPanel.HeroArt</c> would otherwise be.
///
/// <para>Everything else about a deck — its physics, its ticker, its options, its gestures — is behind this call.
/// The shell decides only Cover-vs-Player and WHICH preset; it never learns that a tonearm exists.</para>
/// </summary>
static class NpvDeck
{
    /// <summary>The rail's content width at the default rail size (rail 340 minus its <c>Spacing.S</c> inset per
    /// side) = 324. Used by the no-width overload and as <c>DeckHost</c>'s own floor.</summary>
    public const float DefaultSide = ShellResponsiveLayout.RailDefaultW - 2f * Spacing.S;

    /// <summary>
    /// Build the deck at an explicit square edge — the rail's content width (<c>railWidth − 2·Spacing.S</c>), which
    /// the pinned hero already knows. Passing it beats measuring: the deck can declare its size on the first frame,
    /// so it never relayouts itself into place.
    /// </summary>
    /// <remarks>
    /// <paramref name="side"/> freezes at mount along with the preset (a plain component factory field). The
    /// returned element is keyed on the preset slug, which is what a preset change remounts on; a caller that wants
    /// a live rail RESIZE to re-square the deck must widen that key itself.
    /// </remarks>
    public static Element Create(Track track, NpvPlayerCatalog.Preset preset, float side)
    {
        // `track` is the shell's assurance that something IS playing (the pinned hero mounts nothing without one).
        // The deck itself reads the live track off PlaybackBridge, so a track CHANGE must never remount it — that
        // is the whole point of the boundary machine: the medium reacts, the deck persists.
        _ = track;
        return Embed.Comp(() => new DeckHost { Preset = preset, Side = side }) with { Key = KeyFor(preset) };
    }

    /// <summary>Build the deck at the default rail content width (<see cref="DefaultSide"/>).</summary>
    public static Element Create(Track track, NpvPlayerCatalog.Preset preset) => Create(track, preset, DefaultSide);

    /// <summary>The hero slot's remount key for a preset — "deck:" + the PERSISTED slug. Exposed so the shell's own
    /// <c>Key</c> and this one cannot drift into two different strings for the same deck.</summary>
    public static string KeyFor(in NpvPlayerCatalog.Preset preset) => "deck:" + preset.Slug;
}
