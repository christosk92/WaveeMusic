// ── Themes/Classic/Structure/Rules/ClassicLintGuide.cs — what Classic tells the `--inspect` lint it means (audit FU item 14) ──────────────
//
// Classic declares no edges (the owner's Classic rule: its findings are reported, not fixed; the lint checks no gutter or right edge for it). It
// declares the INTENTS the owner decided for it, each with the board or the decision behind it, so the lint reads them as designed (suppressed,
// counted in the head) instead of asking again. Diagnostics only: nothing here lays anything out.

using FluentGpu.Foundation;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

/// <summary>Classic's lint guide: no edges, the owner's intents.</summary>
public sealed class ClassicLintGuide : IThemeLintGuide
{
    /// <summary>The hero's fixed actions (`ClassicDetailHero`: <c>ClassicDetailKit.FixedActions("vhero-", ...)</c>), whose Shuffle is keyed <c>vhero-shuffle</c>.</summary>
    private const string HeroShufflePrefix = "vhero-shuffle";

    /// <summary>The routes whose pages draw both Shuffles (the album page and the playlist page, <c>LintContext.Route</c>'s names: <c>album</c>, <c>pl</c>).</summary>
    private static readonly string[] s_bothShuffles = ["album", "pl"];

    /// <summary>Album and playlist from Small up: the hero's Shuffle and the list bar's inline one are BOTH kept.</summary>
    private static readonly LintPolicy[] s_shuffles =
    [
        new(LintIntent.RepeatOk, HeroShufflePrefix,
            "owner decision 2026-10-25 (the B8 r3 reversal): Classic keeps BOTH Shuffles from Small up on album and playlist pages, the hero's under Play " +
            "(C-Album, C-Playlist) and the list bar's inline one; never fold them"),
    ];

    /// <inheritdoc />
    public IReadOnlyList<LintEdge> Edges(LintContext context) => [];

    /// <inheritdoc />
    public IReadOnlyList<LintPolicy> Policies(LintContext context)
        => context.Window >= WidthTier.Small && context.Route is { } route && Array.IndexOf(s_bothShuffles, route) >= 0 ? s_shuffles : [];
}
