using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;

namespace Wavee;

/// <summary>One artwork-derived radial wash: the colour at the wash's origin plus the ARTWORK KEY it was graded from.
/// The key is the wash's IDENTITY — two washes with the same resolved colour but different art are different layers, and
/// it is what the shell keys its layer nodes on so an artwork change remounts (and therefore cross-fades) the layer.</summary>
public readonly record struct WashLayer(ColorF Color, string? ArtworkKey);

/// <summary>Home's three-wash composition, in STACKING order (Hero paints first, Mix last). Any leg may be null — a
/// module whose artwork has not been graded yet simply contributes no layer.</summary>
public readonly record struct HomeWash(WashLayer? Hero, WashLayer? Weekly, WashLayer? Mix);

/// <summary>The published shell-material state: an OWNER token plus the two mutually-exclusive material forms — a flat
/// <see cref="Tint"/> (detail pages) or a three-layer radial <see cref="Wash"/> (Home). Both null ⇒ the neutral ground.
/// <para>The owner makes nav transitions race-free: only the page that last CLAIMED the slot may refresh it (see
/// <see cref="ShellMaterial.Publish"/>).</para></summary>
public readonly record struct ShellMaterialState(object? Owner, ColorF? Tint, HomeWash? Wash);

/// <summary>
/// The shell-owned, page-scoped MATERIAL channel. The shell publishes one <see cref="Signal{T}"/> at the root and paints
/// it as the layer directly above the deterministic ground (<c>WaveeColors.ShellGround</c>) that backs ALL chrome — title bar,
/// toolbar, sidebar, player dock. The active page claims it, so the window's chrome carries the album/playlist/Home colour;
/// a page without a colour of its own is claimed neutral by the content host.
/// <para>This IS a Mica scrim again (the hybrid model): the chrome is Mica-passthrough, so the material composites over
/// the live window material and carries the page's hue into it; the content pane above stays opaque, so the PAGE never
/// depends on the wallpaper. No native interop, no GPU pass — one flat rect, or up to three clipped radial-gradient
/// rects.</para>
/// </summary>
public static class ShellMaterial
{
    /// <summary>Context slot — the shell provides its material signal here; consumers read it with
    /// <c>UseContext(ShellMaterial.Slot)</c>. Null when no shell is mounted (e.g. headless tests), in which case a
    /// consumer simply no-ops.</summary>
    public static readonly Context<Signal<ShellMaterialState>?> Slot = new(null);

    /// <summary>THE one way anything writes the material — a HAND-OVER, never a clear (the rule is
    /// <see cref="SpotifyLive.ShellTintOwnership"/>). A page CLAIMS the slot on its first publish and on a KeepAlive
    /// reactivation; any other publish is a refresh that lands only while that page is still the owner, so the exit
    /// tail of a page that was navigated away from can never paint over its successor. A page that parks or unmounts
    /// writes nothing: its material stays until the next page claims, and a claim whose colour is not graded yet keeps
    /// whatever is showing — the chrome never dips to neutral and back between two coloured pages.
    /// <paramref name="definite"/>: the page has DECIDED it carries no colour (washes off, or no colour by design) —
    /// the neutral ground, eased to like any other colour.</summary>
    public static void Publish(Signal<ShellMaterialState>? slot, object owner, bool isClaim, bool definite, ColorF? tint, HomeWash? wash)
    {
        if (slot is null) return;
        var cur = slot.Peek();
        bool hasColor = tint.HasValue || wash is { } w && (w.Hero.HasValue || w.Weekly.HasValue || w.Mix.HasValue);
        switch (SpotifyLive.ShellTintOwnership.Resolve(cur.Owner, new(owner, isClaim, definite, hasColor)))
        {
            case SpotifyLive.ShellTintOwnership.Outcome.WriteKnownColor: slot.Value = new ShellMaterialState(owner, tint, wash); break;
            case SpotifyLive.ShellTintOwnership.Outcome.WriteNeutral: slot.Value = new ShellMaterialState(owner, null, null); break;
            case SpotifyLive.ShellTintOwnership.Outcome.WriteHeldColor: slot.Value = cur with { Owner = owner }; break;
        }
    }
}
