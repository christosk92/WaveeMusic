// ── Theming/Policies/TierPolicy.cs — what a structure declares about window size tiers (themes plan 3.2, T3b) ───────────
//
// A structure's `ThemePolicies.Tiers`: three engine `TierLadder`s (the window's width, the content region's width and the
// window's height; E22), the smallest layout width it supports, the page height its Short layout guarantees, the board
// marks that are not tiers (`NamedRung`) and the finer ladders a core model may consume by id (`NamedLadder`; Classic
// declares none: its player bar's levels are `PlayerBarPolicy`'s). A ladder is DATA: an ascending list of edges, each with a direction-aware hold. It decides nothing by itself;
// `Shell.WindowTiers` feeds it the window and the content width, and the engine's `UseTier` commits the crossing on the
// first frame that is not resized (plan 3.3).
//
// THE EDGE MODEL IS THE ENGINE'S (`FluentGpu.Foundation.TierEdge(At, NarrowHold, WidenHold)`): an edge is crossed upward
// once the value reaches `At + WidenHold`, downward once it drops below `At - NarrowHold`, and a fresh resolve (no previous
// tier: a first mount, a navigation, a launch) uses `At` itself. An absent edge (`TierEdge.None`) can never be crossed, so
// the band above it is unreachable and the band below covers every width up to infinity.
//
// A structure's `Rules\Tiers*.cs` builds its policy; core never names a theme, it reads `ThemeHost.Facts` (`Policies.Tiers`).

using System.Collections.Immutable;
using FluentGpu.Foundation;
using Wavee.Theming.Contracts;

namespace Wavee.Theming;

/// <summary>The axis a <see cref="NamedRung"/> is measured on.</summary>
public enum RungAxis : byte
{
    /// <summary>The window's layout width.</summary>
    Window,

    /// <summary>The content region's width.</summary>
    Content,

    /// <summary>The window's layout height.</summary>
    Height,
}

/// <summary>A board mark that is not a tier (Zune's second docked pane at 1920, a content-line mark): one edge on one axis,
/// resolved with the same hold and the same deferral as a tier and read by a layout record through
/// <see cref="TierFacts.Rung"/>.</summary>
/// <param name="Id">The rung's id (<c>pane.second</c>), unique in the policy.</param>
/// <param name="Edge">Where the rung is crossed and how it holds.</param>
/// <param name="Axis">The axis the edge is measured on.</param>
public readonly record struct NamedRung(string Id, TierEdge Edge, RungAxis Axis = RungAxis.Window);

/// <summary>One of a structure's finer ladders that a core model consumes by id: a ladder whose tier index is the core model's
/// own enum value.</summary>
/// <param name="Id">The ladder's id, unique in the policy.</param>
/// <param name="Ladder">The ladder.</param>
public sealed record NamedLadder(string Id, TierLadder Ladder);

/// <summary>The window tiers a structure declares.</summary>
/// <param name="Window">The window's layout width (after app zoom) to a <see cref="WidthTier"/> index.</param>
/// <param name="Content">The content region's width (the window less the nav pane and an inline panel) to a
/// <see cref="WidthTier"/> index.</param>
/// <param name="Height">The window's layout height to a <see cref="HeightTier"/> index.</param>
/// <param name="CompactFloor">The smallest layout width the structure supports (the window minimum, 300 DIP).</param>
/// <param name="ShortFloor">The page height the structure's Short layout guarantees at the window's minimum height.</param>
/// <param name="Rungs">The board marks that are not tiers.</param>
/// <param name="Ladders">The finer ladders core models consume.</param>
public sealed record TierPolicy(
    TierLadder Window, TierLadder Content, TierLadder Height,
    float CompactFloor, float ShortFloor,
    ImmutableArray<NamedRung> Rungs, ImmutableArray<NamedLadder> Ladders)
{
    /// <summary>The smallest layout width of every shipped structure: the window minimum.</summary>
    public const float WindowFloor = 300f;

    /// <summary>The most rungs a policy may declare (they travel in one 64-bit mask of <see cref="TierFacts"/>).</summary>
    public const int MaxRungs = 64;

    /// <summary>The policy of a structure that declares no tiers yet: every window is Standard and Regular. Core falls back
    /// to it where no theme is installed.</summary>
    public static TierPolicy Standard { get; } = Fixed(WidthTier.Standard, HeightTier.Regular);

    /// <summary>A policy whose every window lands in <paramref name="width"/> and <paramref name="height"/>: the ladders'
    /// edges all lie below zero, so every usable (positive) size is at or above them.</summary>
    public static TierPolicy Fixed(WidthTier width, HeightTier height = HeightTier.Regular)
        => new(FixedLadder((int)width), FixedLadder((int)width), FixedLadder((int)height),
            WindowFloor, 0f, ImmutableArray<NamedRung>.Empty, ImmutableArray<NamedLadder>.Empty);

    private static TierLadder FixedLadder(int tier)
    {
        var edges = new TierEdge[tier];
        for (int i = 0; i < tier; i++) edges[i] = new TierEdge(-(tier - i));
        return new TierLadder(edges);
    }

    /// <summary>The named ladder <paramref name="id"/>; throws when the structure declares none (a core model asked for a
    /// ladder its structure does not provide: a programming error).</summary>
    public TierLadder Ladder(string id)
        => TryGetLadder(id, out TierLadder? ladder)
            ? ladder
            : throw new InvalidOperationException($"The active structure declares no tier ladder '{id}'.");

    /// <summary>The named ladder <paramref name="id"/>, when the structure declares it.</summary>
    public bool TryGetLadder(string id, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out TierLadder? ladder)
    {
        if (!Ladders.IsDefaultOrEmpty)
        {
            foreach (NamedLadder named in Ladders)
            {
                if (string.Equals(named.Id, id, StringComparison.Ordinal))
                {
                    ladder = named.Ladder;
                    return true;
                }
            }
        }
        ladder = null;
        return false;
    }

    /// <summary>The index of the rung <paramref name="id"/> in <see cref="Rungs"/> (its bit in <see cref="TierFacts.Rungs"/>),
    /// or -1.</summary>
    public int IndexOfRung(string id)
    {
        if (Rungs.IsDefaultOrEmpty) return -1;
        for (int i = 0; i < Rungs.Length; i++)
            if (string.Equals(Rungs[i].Id, id, StringComparison.Ordinal)) return i;
        return -1;
    }

    /// <summary>Everything wrong with the policy, empty when it can be bound: the ladders fit their enums, the floors are
    /// sane and the rung and ladder ids are unique.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (Window is null || Content is null || Height is null)
        {
            problems.Add("the window, content and height ladders must all be set");
            return problems;
        }
        int widthMax = (int)WidthTier.Huge4K;
        if (Window.EdgeCount > widthMax) problems.Add($"the window ladder has {Window.EdgeCount} edges, at most {widthMax} fit WidthTier");
        if (Content.EdgeCount > widthMax) problems.Add($"the content ladder has {Content.EdgeCount} edges, at most {widthMax} fit WidthTier");
        if (Height.EdgeCount > (int)HeightTier.Regular) problems.Add($"the height ladder has {Height.EdgeCount} edges, at most {(int)HeightTier.Regular} fit HeightTier");
        if (!(CompactFloor > 0f) || !float.IsFinite(CompactFloor)) problems.Add("CompactFloor must be a positive number");
        if (!(ShortFloor >= 0f) || !float.IsFinite(ShortFloor)) problems.Add("ShortFloor must be zero or positive");

        if (!Rungs.IsDefaultOrEmpty)
        {
            if (Rungs.Length > MaxRungs) problems.Add($"{Rungs.Length} rungs declared, at most {MaxRungs} are supported");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (NamedRung rung in Rungs)
            {
                if (string.IsNullOrWhiteSpace(rung.Id)) problems.Add("a rung has an empty id");
                else if (!seen.Add(rung.Id)) problems.Add($"rung '{rung.Id}' is declared more than once");
                if (!rung.Edge.IsPresent) problems.Add($"rung '{rung.Id}' has no edge");
            }
        }
        if (!Ladders.IsDefaultOrEmpty)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (NamedLadder named in Ladders)
            {
                if (string.IsNullOrWhiteSpace(named.Id) || named.Ladder is null) problems.Add("a named ladder has no id or no ladder");
                else if (!seen.Add(named.Id)) problems.Add($"ladder '{named.Id}' is declared more than once");
            }
        }
        return problems;
    }
}
