// ── Theming/Contracts/ProfileListProps.cs — the props of the Following / Followers list page (themes plan 6.0, T10c2) ──────────────────────────
//
// `ProfileLists.PageHost` (a `[CoreHost]`) keeps everything that decides: the demand, the pooled model, the load rule, the grid plan, the letter projection, the sticky
// watch and the list's mount cache. It hands the view the VALUES and VERBS it draws, once per render it makes. Every member is read-only (WVTH006); a writable cell
// travels as a `Func<Signal<T>>`. The record compares by reference: the host rebuilds one on every render it makes.
//
// The shape (`ProfileListShape`) is a signal the view reads INSIDE the skeleton region's content, so a data landing re-runs the region and never the page.

using System.Runtime.CompilerServices;
using FluentGpu.Controls;
using FluentGpu.Foundation;
using FluentGpu.Signals;

namespace Wavee.Theming.Contracts;

/// <summary>What the list shows, as a VALUE: the load face, the facet, the counts, the grid plan, the flat row count and the mount identity. Equality is the compiler's, so
/// a re-derivation that changed nothing writes nothing. <paramref name="Epoch"/> moves whenever the rows were rebuilt (data, filter or width), which re-fires the realized slots.</summary>
public sealed record ProfileListShape(ProfileListLoad Load, ProfileFacet Facet, int Total, int Visible, ProfileChip Chip,
                                      ProfileChipCounts Counts, ProfileGridPlan Plan, int Rows, string MountKey, uint Epoch)
{
    /// <summary>The seed before the first derivation: pending, no columns.</summary>
    public static readonly ProfileListShape Pending = new(ProfileListLoad.Pending, ProfileFacet.Following, 0, 0, ProfileChip.All,
        default, default, 0, "", 0u);
}

/// <summary>The head's words and navigation: the person's name (the fallback when it has none), the page title, the two selector words and the verbs.</summary>
public sealed record ProfileListHead(string Name, string Title, ProfileFacet Facet, string[] Labels, Action? OpenProfile, Action<int> PickFacet);

/// <summary>The bound list's per-mount layout and options (the layout's extents are STATEFUL, so the host builds them once per mount key).</summary>
public readonly record struct ProfileListMount(RepeatLayout Layout, ListOptions<ProfileRowItem> Options);

/// <summary>The props of <see cref="ViewKeys.ProfileListPage"/>.</summary>
public sealed record ProfileListProps
{
    /// <summary>The route parsed: false draws the error vacancy alone.</summary>
    public required bool Parsed { get; init; }

    /// <summary>The head (always real, outside the region).</summary>
    public required ProfileListHead Head { get; init; }

    /// <summary>Pending (the derivation has no measured grid yet, or the list has not answered): the derived shimmer shows.</summary>
    public required Func<bool> Pending { get; init; }

    /// <summary>The list failed with nothing to show: the error vacancy with <see cref="Retry"/>.</summary>
    public required Func<bool> Failed { get; init; }

    /// <summary>Retry the list (and the profile row when it is what failed).</summary>
    public required Action Retry { get; init; }

    /// <summary>The page's shape, read inside the region's content.</summary>
    public required IReadSignal<ProfileListShape> Shape { get; init; }

    /// <summary>The current facet (an untracked read, for the shimmer source).</summary>
    public required Func<ProfileFacet> Facet { get; init; }

    /// <summary>The body's measured width, floored to 8 DIP (an untracked read, for the shimmer source).</summary>
    public required Func<float> BodyWidth { get; init; }

    /// <summary>The measured box's size observer: writes the body width.</summary>
    public required Action<RectF> OnBodyBounds { get; init; }

    /// <summary>The person's display name now (the hidden vacancy's subtitle).</summary>
    public required Func<string> DisplayName { get; init; }

    /// <summary>The find box's text (not persisted).</summary>
    public required Func<Signal<string>> Query { get; init; }

    /// <summary>A chip was picked.</summary>
    public required Action<ProfileChip> SelectChip { get; init; }

    /// <summary>The layout and options for one mount key (cached by the host while the key does not change).</summary>
    public required Func<string, ProfileListMount> Mount { get; init; }

    /// <summary>The bound flat projection (letter headers and rows of cards).</summary>
    public required BoundItemsSource<ProfileRowItem> Rows { get; init; }

    /// <summary>The sticky letter's glyph index (-1: none).</summary>
    public required IReadSignal<int> StickyLetter { get; init; }

    /// <summary>The sticky letter's upward push by the header arriving under it.</summary>
    public required IReadSignal<float> StickyPush { get; init; }

    /// <summary>The a-z strip's mask of present letters.</summary>
    public required IReadSignal<uint> Present { get; init; }

    /// <summary>A strip tap (unanimated; an absent letter resolves to nothing).</summary>
    public required Action<int> Jump { get; init; }

    /// <summary>The pinned extent of one flat item.</summary>
    public required Func<int, float> ExtentOf { get; init; }

    /// <summary>How many entries the filter lets through (a stale slot never reads past it).</summary>
    public required Func<int> VisibleCount { get; init; }

    /// <summary>The k-th visible entry.</summary>
    public required Func<int, ProfileEntry> VisibleAt { get; init; }

    /// <inheritdoc />
    public bool Equals(ProfileListProps? other) => ReferenceEquals(this, other);

    /// <inheritdoc />
    public override int GetHashCode() => RuntimeHelpers.GetHashCode(this);
}
