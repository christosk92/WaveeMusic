using Windows.UI.ViewManagement;

namespace Wavee.UI.WinUI.Helpers;

/// <summary>
/// Single read-point for the OS "reduce motion" preference, so every reveal /
/// transition can honor it consistently (WCAG 2.3.3 — Animation from
/// Interactions). Backed by <see cref="UISettings.AnimationsEnabled"/>, which
/// reflects Windows Settings → Accessibility → Visual effects → Animation
/// effects.
/// </summary>
public static class ReducedMotion
{
    private static readonly UISettings _settings = new();

    /// <summary>
    /// True when the OS allows UI motion. False when the user has requested
    /// reduced motion — callers should snap to the final state instead of
    /// animating. Defaults to <c>true</c> if the setting can't be read.
    /// </summary>
    public static bool AnimationsEnabled
    {
        get
        {
            try { return _settings.AnimationsEnabled; }
            catch { return true; }
        }
    }
}
