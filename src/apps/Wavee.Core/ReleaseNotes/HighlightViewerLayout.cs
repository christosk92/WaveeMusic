using System;

namespace Wavee.Core.ReleaseNotes;

/// <summary>The viewer's navigation verbs, app-neutral: the view maps FluentGpu's <c>Keys.*</c> ints onto these so
/// this file stays engine-free and unit-testable.</summary>
public enum HighlightNavKey : byte { Previous, Next, First, Last }

/// <summary>Which way a slide travels. None = the index did not change (clamped at an end, or a single item).</summary>
public enum HighlightSlideDirection : byte { None, Forward, Back }

public readonly record struct HighlightStep(int Index, HighlightSlideDirection Direction);

/// <summary>The viewer plate's geometry (design §B.1) and its stepping rule (§B.2), pure.</summary>
public static class HighlightViewerLayout
{
    /// <summary>A 1200 px poster shown at 960 is a 0.8× downsample — sharp, never upsampled; wider plates make the
    /// text measure absurd.</summary>
    public const float PlateMaxWidth = 960f;
    /// <summary>Below this the image is unreadable (180 tall); the text column scrolls instead.</summary>
    public const float PlateMinWidth = 320f;
    /// <summary>48 DIP of veil each side, so the plate reads as a plate and not as a page.</summary>
    public const float ScrimInsetX = 96f;
    /// <summary>Pager (36) + text block (≤ 260) + 64 vertical margin the image must leave below itself.</summary>
    public const float ReservedBelowImage = 360f;
    public const float PlateMarginY = 64f;
    public const float PosterAspect = 16f / 9f;
    public const float ChromeCircle = 36f;
    public const float ChromeInset = 12f;

    /// <summary>W = min(max(320, min(960, vpW − 96, (vpH − 360)·16⁄9)), vpW − 96). The floor never pushes the plate
    /// past the window edge. 1440×900 → 960; 1100×700 → 604; 900×600 → 427; 500×420 → 320; a 320-wide window → 224.</summary>
    public static float PlateWidth(float vpW, float vpH)
    {
        float byWidth = vpW - ScrimInsetX;
        float byHeight = (vpH - ReservedBelowImage) * PosterAspect;
        float w = MathF.Min(PlateMaxWidth, MathF.Min(byWidth, byHeight));
        w = MathF.Max(PlateMinWidth, w);
        w = MathF.Min(w, byWidth);
        return MathF.Round(w);
    }

    /// <summary>The 16:9 band height, from the plate's OWN width — always, poster or not. L4 (issue #89): a
    /// no-poster slide used to get a flat 120 DIP band regardless of plate width; the prototype keeps the same
    /// <c>w·9/16</c> rule either way (a tinted band, not a smaller one), which is also just this formula with
    /// <paramref name="hasPoster"/> dropped — kept as a parameter for the call-site's own poster-presence branch on
    /// the band's FILL, not its height.</summary>
    public static float ImageHeight(float plateWidth, bool hasPoster) => MathF.Round(plateWidth / PosterAspect);

    public static float PlateMaxHeight(float vpH) => vpH - PlateMarginY;

    /// <summary>Clamped, no wrap (the WinUI FlipView rule): a Right press on the last slide does nothing. A silent
    /// jump back to the first feels like a bug, and the dots already say where you are.</summary>
    public static HighlightStep Step(int current, int count, HighlightNavKey key)
    {
        if (count <= 1) return new(0, HighlightSlideDirection.None);
        int last = count - 1;
        current = Math.Clamp(current, 0, last);
        int target = key switch
        {
            HighlightNavKey.Previous => current - 1,
            HighlightNavKey.Next => current + 1,
            HighlightNavKey.First => 0,
            _ => last,
        };
        return StepTo(current, Math.Clamp(target, 0, last), count);
    }

    /// <summary>A direct jump (a pip click): the direction is the sign of the move.</summary>
    public static HighlightStep StepTo(int current, int target, int count)
    {
        if (count <= 1) return new(0, HighlightSlideDirection.None);
        int last = count - 1;
        current = Math.Clamp(current, 0, last);
        target = Math.Clamp(target, 0, last);
        var dir = target > current ? HighlightSlideDirection.Forward
                : target < current ? HighlightSlideDirection.Back
                : HighlightSlideDirection.None;
        return new(dir == HighlightSlideDirection.None ? current : target, dir);
    }
}
