// ── Themes/Classic/Structure/Rules/ClassicUnavailableBarLayout.cs — the unavailable-songs bar's numbers per window tier (contract 1.22) ──
//
// Boards C-Unavailable (Standard) and C-Unavailable-Compact: a status bar over the rows of a list that holds songs the user cannot play, with
// "Hide them" (the filter card's "Hide songs I can’t play" switch, `Track.FilterFlags.PlayableOnly`). A 4-cornered card plate (the card fill and
// stroke), the accent info glyph at 16, the sentence at 14/20 that grows, the button after it, a 10 gap between the three.
//   · Compact: padding 12 / 8 / 8 / 8, the 40 button (the brief's target rule), the short sentence ("2 songs here can’t be played."), 12 to the
//     rows (the page's own 12 gap). 58 tall.
//   · Small: no board: Standard's bar with the short sentence (Q18: the list column is too narrow for the reasons on one line).
//   · Standard and Huge (Huge draws none, Q18): padding 12 / 8, the tuned 32 button (the board draws 28 at 13: Classic's buttons are 32 and the
//     plate keeps the board's 50 by taking 8 over and under it instead of 10), the sentence with its reasons, 8 to the column header.
// The bar's slot is always mounted while the list could show it: its height eases between the band and 0 through real layout (the rows under it
// reflow, never snap), its content clipped, so a "Hide them" or a list whose songs become playable releases the band animated.

using FluentGpu.Controls;
using FluentGpu.Foundation;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

/// <summary>The unavailable-songs bar of one window tier.</summary>
public sealed record ClassicUnavailableBar
{
    /// <summary>The bar's own height, its 1-DIP border included.</summary>
    public required float Height { get; init; }

    /// <summary>The plate's padding (left 12; the button side and the top and bottom per tier).</summary>
    public required Edges4 Padding { get; init; }

    /// <summary>The "Hide them" button's size: Large (40) at Compact, the tuned Medium (32) elsewhere.</summary>
    public required ControlSize ButtonSize { get; init; }

    /// <summary>The air under the bar, before the column header or the rows.</summary>
    public required float GapBelow { get; init; }

    /// <summary>The sentence names its reasons ("…: one isn’t available in your country, one isn’t out yet.").</summary>
    public required bool Explains { get; init; }

    /// <summary>The gap between the glyph, the sentence and the button.</summary>
    public float Gap { get; init; } = 10f;

    /// <summary>The info glyph's size.</summary>
    public float Glyph { get; init; } = 16f;

    /// <summary>The sentence's size.</summary>
    public float TextSize { get; init; } = 14f;

    /// <summary>The sentence's line height.</summary>
    public float TextLine { get; init; } = 20f;

    /// <summary>The band the bar takes while shown: its height and the air under it (the hero arm's clip inset adds it).</summary>
    public float Extent => Height + GapBelow;

    /// <summary>Compact (C-Unavailable-Compact).</summary>
    public static ClassicUnavailableBar Compact { get; } = new()
    {
        Height = 58f, Padding = new Edges4(12f, 8f, 8f, 8f), ButtonSize = ControlSize.Large, GapBelow = 12f, Explains = false,
    };

    /// <summary>Standard and Huge (C-Unavailable).</summary>
    public static ClassicUnavailableBar Standard { get; } = new()
    {
        Height = 50f, Padding = new Edges4(12f, 8f, 12f, 8f), ButtonSize = ControlSize.Medium, GapBelow = 8f, Explains = true,
    };

    /// <summary>Small: Standard's bar with the short sentence.</summary>
    public static ClassicUnavailableBar Small { get; } = Standard with { Explains = false };

    /// <summary>The bar of window tier <paramref name="tier"/>.</summary>
    public static ClassicUnavailableBar For(WidthTier tier) => tier switch
    {
        WidthTier.Compact => Compact,
        WidthTier.Small => Small,
        _ => Standard,
    };

    /// <summary>The slot's height motion: the app's disclosure reveal (`Design.Reveal.Resize`: layout lands once, the presented height springs and the rows
    /// under it ride it), so they ease (never snap) when the bar comes and goes. The call site passes null under reduced motion.</summary>
    public static LayoutTransition Motion => Design.Reveal.Resize;
}
