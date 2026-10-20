// ── Theming/Composition/ThemeLintGuide.cs — what a theme tells the `--inspect` lint about where its edges belong ────────────
//
// The agent server's `lint` verb (Screens/Diagnostics.Inspect.Lint*.cs) checks the mounted scene against rules every theme shares (clipping,
// trimming, type floor, hit targets, duplicates, rhythm, alignment, spacing) and against the two things only a theme knows:
//   · EDGES: the x its gutter, page column and edges sit on in the window tier it is drawn in (Zune: `ZuneFrameRules.ColumnLead(width,
//     gutter)`), as `LintEdge` data the lint resolves against the scene's on-screen keyed nodes;
//   · POLICIES (intents by key prefix): what the theme MEANS where a rule would otherwise guess, each with a reason a reviewer can check
//     against a board. "Shelf captions ellipsise" (`TrimOk`), "the panorama hangs 8 left of the column" (`Hang`), "this title never
//     trims" (`NeverTrim`). A policy turns a finding into an intended one (outcome `suppressed`, counted in the lint head), never silence.
//     A board draws one page: a guide scopes a page's policies by `LintContext.Route`.
// Core cannot name a theme's records (WVTH001), so the theme describes both as DATA and registers a guide for its id (`ThemeInspection`).
// A theme with no guide (Classic) gets no edge checks and no intents: its findings are reported, not fixed (the owner's Classic rule).
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
/// <param name="Column">The x is the page column's ink line: `align.inset` measures the page's leads against it (when at least three leads
/// sit on it) instead of guessing the line from whatever is on screen.</param>
public sealed record LintEdge(string Rule, string Id, string What, LintSide Side, float Expected, float Tolerance,
    IReadOnlyList<string>? Keys = null, IReadOnlyList<string>? Prefixes = null, string UnderKey = "", bool GlyphsOnly = false,
    bool Advisory = false, bool Column = false);

/// <summary>What a guide reads: the window's tiers and size, and the presented rect of a keyed node (null when none is mounted).</summary>
/// <param name="Window">The window width tier.</param>
/// <param name="Height">The window height tier.</param>
/// <param name="WindowW">The window's width in DIP.</param>
/// <param name="WindowH">The window's height in DIP.</param>
/// <param name="RectOf">The presented absolute rect (window DIP) of the first live node with this key, or null.</param>
public readonly record struct LintContext(WidthTier Window, HeightTier Height, float WindowW, float WindowH, Func<string, RectF?> RectOf)
{
    /// <summary>The route's name as `status` names it (<c>home</c>, <c>album</c>; null or "" when unknown): a policy that one page's board
    /// draws holds on that page only.</summary>
    public string? Route { get; init; }
}

/// <summary>What a guide policy declares about the nodes under its key prefix (each names the rule it speaks to).</summary>
public enum LintIntent : byte
{
    /// <summary>Text here may ellipsise: the content decides its length (`text.trim` reports it as designed, not as a finding).</summary>
    TrimOk,

    /// <summary>Text here must never trim: any trim is an error (`text.trim`).</summary>
    NeverTrim,

    /// <summary>The lead edge sits <see cref="LintPolicy.Value"/> DIP off its column on purpose, an optical hang (`align.inset`; negative = left).</summary>
    Hang,

    /// <summary>The lead edge is indented <see cref="LintPolicy.Value"/> DIP from its column on purpose, a nested list (`align.inset`).</summary>
    Indent,

    /// <summary>Content runs past a scroller's or the window's edge on purpose (`text.clip` at a scroll edge).</summary>
    Bleed,

    /// <summary>The declared spacing here is off the theme's scale on purpose (`space.scale`).</summary>
    OffScaleOk,

    /// <summary>The same verb is offered here more than once on purpose (`duplicate.action`).</summary>
    RepeatOk,

    /// <summary>Any finding of <see cref="LintPolicy.Rule"/> here is intended: the generic form for the rules without a named intent.</summary>
    Allow,
}

/// <summary>One intent: <see cref="Intent"/> holds for every node whose key, or an ancestor's key, starts with <see cref="Prefix"/>.</summary>
/// <param name="Intent">What is intended.</param>
/// <param name="Prefix">The key prefix it covers (a key constant of the theme's Rules, or the literal a view keys with).</param>
/// <param name="Reason">Why, in words a reviewer can check: name the board that draws it. Never empty.</param>
/// <param name="Value">The DIP a <see cref="LintIntent.Hang"/> or <see cref="LintIntent.Indent"/> declares.</param>
/// <param name="Rule">The rule id an <see cref="LintIntent.Allow"/> covers (<c>align.baseline</c>).</param>
public sealed record LintPolicy(LintIntent Intent, string Prefix, string Reason, float Value = 0f, string Rule = "");

/// <summary>A theme's edge expectations and intents for the window it is drawn in.</summary>
public interface IThemeLintGuide
{
    /// <summary>The expectations that hold in <paramref name="context"/>'s window (empty when none do). Called per lint, on the UI thread.</summary>
    IReadOnlyList<LintEdge> Edges(LintContext context);

    /// <summary>The intents that hold in <paramref name="context"/>'s window (none by default). Called per lint, on the UI thread.</summary>
    IReadOnlyList<LintPolicy> Policies(LintContext context) => [];

    /// <summary>Keys of the boxes whose display type the theme sets on an EXACT line flush with a clipping edge above it, by design (Zune's
    /// panorama: the title's line box sits on the band's top, its ascenders inside it). The clip rule's ink model (1.2 em, centred on the line)
    /// reads such a line as running past the top, so for text under these keys a cut at the TOP is reported as <c>info</c> ("designed bleed"),
    /// never an error; a cut at the bottom (descenders) still is. Empty by default.</summary>
    IReadOnlyList<string> DesignedTopBleeds => [];
}
