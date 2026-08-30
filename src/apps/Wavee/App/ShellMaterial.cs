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
/// <see cref="Tint"/> (detail pages) or a three-layer radial <see cref="Wash"/> (Home). Both null ⇒ the bare ground.
/// <para>The owner makes nav transitions race-free — a page clears the material only if it is still the owner, so
/// "park old page + activate new page" lands on the new page's material regardless of which effect fires first.</para></summary>
public readonly record struct ShellMaterialState(object? Owner, ColorF? Tint, HomeWash? Wash);

/// <summary>
/// The shell-owned, page-scoped MATERIAL channel. The shell publishes one <see cref="Signal{T}"/> at the root and paints
/// it as the layer directly above the deterministic ground (<c>WaveeColors.ShellGround</c>) that backs ALL chrome — title bar,
/// toolbar, sidebar, player dock. A page sets it while it is the active, visible page and clears it on park / unmount, so
/// the window's chrome carries the album/playlist/Home colour and reverts when you navigate away.
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
}
