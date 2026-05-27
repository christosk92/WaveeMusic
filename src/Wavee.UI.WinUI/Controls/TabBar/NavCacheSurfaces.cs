using System;
using System.Collections.Generic;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Wavee.UI.WinUI.Controls.TabBar;

/// <summary>
/// Visual-tree walk that drives <see cref="INavCacheSurfaceParticipant"/>
/// release / restore for a page (or any subtree), and silences
/// <see cref="Shimmer"/> animations on cached pages. One mechanism replacing the
/// scattered per-page hero-release calls and the reverted per-control
/// CompositionImage surface tree-walk.
///
/// <para>
/// Only the <em>realized</em> visual tree is walked — a collapsed cached page
/// is still realized, but virtualized off-screen list items are not, which is
/// correct: an unrealized item holds no surfaces.
/// </para>
///
/// <para>
/// Shimmer handling: every <see cref="Shimmer"/> instance encountered during a
/// release walk has <c>IsActive</c> flipped to <c>false</c> — that kills the
/// running <c>Vector2KeyFrameAnimation</c> on the toolkit's sprite visual
/// (composition cost). Restoring sets it back to <c>true</c>. Shimmers are also
/// recorded in a weak-reference registry so <see cref="UiHealthMonitor"/> can
/// surface the live count as a leak indicator.
/// </para>
/// </summary>
public static class NavCacheSurfaces
{
    // Weak-reference registry of every Shimmer ever observed by a walk. Used by
    // UiHealthMonitor.GetLiveShimmerCount() to flag regressions. List is pruned
    // on every read; growth bound is the realized-tree shimmer count across all
    // tabs, not the cumulative session count.
    private static readonly object _registryLock = new();
    private static readonly List<WeakReference<Shimmer>> _shimmerRegistry = new(64);

    /// <summary>
    /// Walks the realized visual tree under <paramref name="root"/> and invokes
    /// <paramref name="action"/> for every <see cref="INavCacheSurfaceParticipant"/>.
    /// Returns the count for which the action returned <c>true</c>.
    /// </summary>
    public static int VisitParticipants(
        DependencyObject? root,
        Func<INavCacheSurfaceParticipant, bool> action)
    {
        if (root is null)
            return 0;

        var count = 0;
        var stack = new Stack<DependencyObject>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current is INavCacheSurfaceParticipant participant && action(participant))
                count++;

            int childCount;
            try
            {
                childCount = VisualTreeHelper.GetChildrenCount(current);
            }
            catch
            {
                // Visual tree can mutate during a page trim — skip this branch.
                continue;
            }

            for (var i = childCount - 1; i >= 0; i--)
            {
                try
                {
                    stack.Push(VisualTreeHelper.GetChild(current, i));
                }
                catch
                {
                    // Branch mutated mid-walk — skip it.
                }
            }
        }

        return count;
    }

    /// <summary>Releases every participant under <paramref name="root"/> and
    /// silences every <see cref="Shimmer"/> animation.</summary>
    public static int ReleaseAll(DependencyObject? root) => Walk(root, release: true);

    /// <summary>Restores every participant under <paramref name="root"/> and
    /// re-activates every <see cref="Shimmer"/> animation.</summary>
    public static int RestoreAll(DependencyObject? root) => Walk(root, release: false);

    /// <summary>
    /// Sums <see cref="INavCacheSurfaceParticipant.EstimatedSurfaceBytes"/> over
    /// the subtree — for the memory-attribution diagnostic.
    /// </summary>
    public static long SumEstimatedBytes(DependencyObject? root)
    {
        long total = 0;
        VisitParticipants(root, p =>
        {
            total += p.EstimatedSurfaceBytes;
            return false;
        });
        return total;
    }

    /// <summary>
    /// Counts every <see cref="Shimmer"/> still alive in any visual tree the
    /// walk has touched. Prunes dead weak references each call so the value
    /// reflects current state, not cumulative history.
    /// </summary>
    public static int GetLiveShimmerCount()
    {
        lock (_registryLock)
        {
            var write = 0;
            for (var read = 0; read < _shimmerRegistry.Count; read++)
            {
                if (_shimmerRegistry[read].TryGetTarget(out _))
                {
                    if (write != read)
                        _shimmerRegistry[write] = _shimmerRegistry[read];
                    write++;
                }
            }
            if (write < _shimmerRegistry.Count)
                _shimmerRegistry.RemoveRange(write, _shimmerRegistry.Count - write);
            return _shimmerRegistry.Count;
        }
    }

    // ── Internals ───────────────────────────────────────────────────────

    private static int Walk(DependencyObject? root, bool release)
    {
        if (root is null)
            return 0;

        var participantCount = 0;
        var stack = new Stack<DependencyObject>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var current = stack.Pop();

            if (current is INavCacheSurfaceParticipant participant)
            {
                var handled = release ? participant.ReleaseForNavCache() : participant.RestoreForNavCache();
                if (handled)
                    participantCount++;
            }

            if (current is Shimmer shimmer)
            {
                // IsActive toggles the offset KFA on the toolkit's sprite visual
                // — Visibility.Collapsed alone leaves the animation running and
                // its peer alive. Killing IsActive on cached pages drops the
                // running composition animation cost without disturbing the
                // realized XAML tree.
                shimmer.IsActive = !release;
                RegisterShimmer(shimmer);
            }

            int childCount;
            try
            {
                childCount = VisualTreeHelper.GetChildrenCount(current);
            }
            catch
            {
                continue;
            }

            for (var i = childCount - 1; i >= 0; i--)
            {
                try
                {
                    stack.Push(VisualTreeHelper.GetChild(current, i));
                }
                catch
                {
                    // Branch mutated mid-walk — skip it.
                }
            }
        }

        return participantCount;
    }

    private static void RegisterShimmer(Shimmer shimmer)
    {
        lock (_registryLock)
        {
            // Linear scan is fine — registry size is bounded by realized-tree
            // shimmer count and dead refs are pruned on read.
            for (var i = 0; i < _shimmerRegistry.Count; i++)
            {
                if (_shimmerRegistry[i].TryGetTarget(out var existing) && ReferenceEquals(existing, shimmer))
                    return;
            }
            _shimmerRegistry.Add(new WeakReference<Shimmer>(shimmer));
        }
    }
}
