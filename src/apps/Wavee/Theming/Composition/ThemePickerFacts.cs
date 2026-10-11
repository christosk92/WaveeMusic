// ── Theming/Composition/ThemePickerFacts.cs — "installed + current" for core UI ────────────────────────────────────────
//
// The one API core UI (the Settings theme picker, the feedback line) uses to learn which themes exist and which is active;
// it carries ids and manifests as DATA and never a composition (WVTH004 keeps every other identity read inside
// `Theming\Composition`, App.cs and ThemeBootstrap).

using FluentGpu.Signals;

namespace Wavee.Theming;

/// <summary>The themes a picker offers and the active one.</summary>
/// <param name="Installed">Every installed manifest, in install order, Developer-only themes included.</param>
/// <param name="CurrentId">The id of the active theme ("" before <c>ThemeHost.Install</c>).</param>
public sealed record PickerState(IReadOnlyList<ThemeManifest> Installed, string CurrentId)
{
    /// <summary>No theme installed.</summary>
    public static PickerState Empty { get; } = new(Array.Empty<ThemeManifest>(), "");

    /// <summary>The manifests a picker shows: all of them with <paramref name="developerMode"/> on, else the public ones.</summary>
    public IReadOnlyList<ThemeManifest> Visible(bool developerMode)
    {
        if (developerMode) return Installed;
        var visible = new List<ThemeManifest>(Installed.Count);
        foreach (var m in Installed)
            if (m.Availability == ThemeAvailability.Public) visible.Add(m);
        return visible;
    }
}

/// <summary>The installed themes and the current one, as a signal.</summary>
public static class ThemePickerFacts
{
    private static readonly Signal<PickerState> s_state = new(PickerState.Empty);

    /// <summary>The picker state; republished by <c>ThemeHost.Install</c> and <c>ThemeHost.Switch</c>.</summary>
    public static IReadSignal<PickerState> State => s_state;

    /// <summary>Published by <c>Themes</c> only.</summary>
    internal static void Publish(PickerState state) => s_state.Value = state;
}
