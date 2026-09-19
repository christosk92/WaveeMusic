using System;

namespace Wavee.Core;

/// <summary>
/// Menu-row label hygiene — pure, engine-free, and therefore directly testable.
///
/// <para>A context-menu row label is a single-line <c>TextEl</c> with <c>Grow = 1</c> and no trimming of its own, so a
/// long DYNAMIC label ("Move out of “Late night listening, autumn 2019 edition”") does not clip: it widens the whole
/// flyout, and every other row with it. The fix belongs where the label is MINTED — the interpolated name is clipped to
/// a sane width before it reaches the loc format string, so the row stays inside the menu's natural column instead of
/// stretching it.</para>
/// </summary>
public static class MenuLabel
{
    /// <summary>The default clip width for an interpolated entity name inside a menu label, in characters. Sized
    /// against the 250-DIP context-menu minimum: ~28 characters of 14px UI text plus the surrounding verb fills that
    /// column without widening it.</summary>
    public const int NameChars = 28;

    /// <summary>Clip <paramref name="name"/> to <paramref name="max"/> characters, ending in a single ellipsis. Shorter
    /// names (and a null/empty one) come back untouched — the ellipsis appears only when something was actually
    /// dropped, so a name that fits is never decorated.</summary>
    public static string Clip(string? name, int max = NameChars)
    {
        if (name is not { Length: > 0 }) return "";
        if (max < 1) return "…";
        if (name.Length <= max) return name;
        // Trim the trailing space the cut usually lands on, so the result is "Late night…" and never "Late night …".
        return string.Concat(name.AsSpan(0, max - 1).TrimEnd(), "…");
    }
}
