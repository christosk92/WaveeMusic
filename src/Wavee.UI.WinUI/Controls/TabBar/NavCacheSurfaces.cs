using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Wavee.UI.WinUI.Controls.TabBar;

/// <summary>
/// Visual-tree walk that drives <see cref="INavCacheSurfaceParticipant"/>
/// release / restore for a page (or any subtree). One mechanism replacing the
/// scattered per-page hero-release calls and the reverted per-control
/// CompositionImage surface tree-walk.
///
/// <para>
/// Only the <em>realized</em> visual tree is walked — a collapsed cached page
/// is still realized, but virtualized off-screen list items are not, which is
/// correct: an unrealized item holds no surfaces.
/// </para>
/// </summary>
public static class NavCacheSurfaces
{
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

    /// <summary>Releases every participant under <paramref name="root"/>.</summary>
    public static int ReleaseAll(DependencyObject? root)
        => VisitParticipants(root, static p => p.ReleaseForNavCache());

    /// <summary>Restores every participant under <paramref name="root"/>.</summary>
    public static int RestoreAll(DependencyObject? root)
        => VisitParticipants(root, static p => p.RestoreForNavCache());

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
}
