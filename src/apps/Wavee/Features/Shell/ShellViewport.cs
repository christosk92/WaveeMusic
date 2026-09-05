using System;
using FluentGpu.Controls;
using Wavee;

namespace Wavee.Features.Shell;

/// <summary>The one "window viewport height → page content viewport height" conversion, shared by Home's hero and the
/// artist detail hero so neither invents its own chrome allowance. Mirrors the shape
/// <see cref="Wavee.Features.Detail.DetailLayoutBreakpoints.EstimatePageWidthFromViewport"/> already uses for width:
/// subtract the shell's own fixed chrome from the raw <c>Viewport.Size</c> reading, floored at a sane minimum rather
/// than going negative on a very short window. Same idiom as <c>ImmersiveLyricsSurface.BodyH</c>
/// (<c>vpH - TitleBar.ExpandedHeight - WaveeSize.PlayerBarH</c>) — restated here as a named, test-included helper so
/// Home and Detail cannot drift into two different allowances.</summary>
public static class ShellViewport
{
    /// <summary>The merged title/nav row (<see cref="TitleBar.ExpandedHeight"/>) plus the bottom player dock
    /// (<see cref="PlayerDock.Reserve"/>) — the vertical chrome every page's content column sits between.</summary>
    public const float ChromeVerticalAllowanceDip = TitleBar.ExpandedHeight + PlayerDock.Reserve;

    /// <summary>A page's own visible content height from the raw window viewport height. Never negative.</summary>
    public static float PageHeightFor(float viewportHeight) => MathF.Max(0f, viewportHeight - ChromeVerticalAllowanceDip);
}
