// ── Theming/Composition/ThemeLintGuide.cs — what a theme tells the `--inspect` lint about where its edges belong ────────────
//
// The agent server's `lint` verb (Screens/Diagnostics.Inspect.Lint*.cs) checks the mounted scene against rules every theme shares (clipping,
// trimming, type floor, hit targets, duplicates, rhythm) and against the ONE thing only a theme knows: the x its gutter, page column and
// edges sit on in the window tier it is drawn in (Zune: `ZuneFrameRules.ColumnLead(width, gutter)`). Core cannot name a theme's records
// (WVTH001), so the theme describes those expectations as DATA, `LintEdge`s, and registers a guide for its id (`ThemeInspection`); the
// lint resolves each edge against the scene's keyed nodes and compares. A theme with no guide (Classic) simply gets no edge checks.
//
// Diagnostics only: a guide never decides how anything lays out.

using FluentGpu.Foundation;
using Wavee.Theming.Contracts;

namespace Wavee.Theming;

/// <summary>Which side of a node an edge expectation measures.</summary>
public enum LintSide : byte
{
    /// <summary>The node's left edge (its x); the leftmost matching node is the one measured.</summary>
    Left,

    /// <summary>The node's right edge (x + width); the rightmost matching node is the one measured.</summary>
    Right,
}

/// <summary>One expectation: "the <see cref="Side"/> edge of the matching node sits at <see cref="Expected"/> (window DIP, +/- <see cref="Tolerance"/>)".</summary>
/// <param name="Rule">The lint rule it reports under (<c>gutter</c> or <c>right-edge</c>).</param>
/// <param name="Id">A stable id for the expectation (<c>band.first-pivot</c>).</param>
/// <param name="What">A few words for the message ("the band's first pivot").</param>
/// <param name="Side">The edge measured.</param>
/// <param name="Expected">Where it must sit, in window DIP.</param>
/// <param name="Tolerance">How far it may be off.</param>
/// <param name="Keys">Node keys that match exactly.</param>
/// <param name="Prefixes">Node key prefixes that match.</param>
/// <param name="UnderKey">When set, only nodes inside the subtree of a node with this key match.</param>
/// <param name="GlyphsOnly">Only icon-glyph text nodes match (the "last glyph" of a bar).</param>
/// <param name="Advisory">A miss is a warning, not an error (an expectation the theme cannot be sure of).</param>
public sealed record LintEdge(string Rule, string Id, string What, LintSide Side, float Expected, float Tolerance,
    IReadOnlyList<string>? Keys = null, IReadOnlyList<string>? Prefixes = null, string UnderKey = "", bool GlyphsOnly = false,
    bool Advisory = false);

/// <summary>What a guide reads: the window's tiers and size, and the presented rect of a keyed node (null when none is mounted).</summary>
/// <param name="Window">The window width tier.</param>
/// <param name="Height">The window height tier.</param>
/// <param name="WindowW">The window's width in DIP.</param>
/// <param name="WindowH">The window's height in DIP.</param>
/// <param name="RectOf">The presented absolute rect (window DIP) of the first live node with this key, or null.</param>
public readonly record struct LintContext(WidthTier Window, HeightTier Height, float WindowW, float WindowH, Func<string, RectF?> RectOf);

/// <summary>A theme's edge expectations for the window it is drawn in.</summary>
public interface IThemeLintGuide
{
    /// <summary>The expectations that hold in <paramref name="context"/>'s window (empty when none do). Called per lint, on the UI thread.</summary>
    IReadOnlyList<LintEdge> Edges(LintContext context);

    /// <summary>Keys of the boxes whose display type the theme sets on an EXACT line flush with a clipping edge above it, by design (Zune's
    /// panorama: the title's line box sits on the band's top, its ascenders inside it). The clip rule's ink model (1.2 em, centred on the line)
    /// reads such a line as running past the top, so for text under these keys a cut at the TOP is reported as <c>info</c> ("designed bleed"),
    /// never an error; a cut at the bottom (descenders) still is. Empty by default.</summary>
    IReadOnlyList<string> DesignedTopBleeds => [];
}
