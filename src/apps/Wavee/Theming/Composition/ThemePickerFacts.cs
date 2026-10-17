// ── Theming/Composition/ThemePickerFacts.cs — "installed + current" for core UI ────────────────────────────────────────
//
// The one API core UI (the Settings theme picker, the feedback line) uses to learn which themes exist and which is active;
// it carries ids and manifests as DATA and never a composition (WVTH004 keeps every other identity read inside
// `Theming\Composition`, App.cs and ThemeBootstrap).

using FluentGpu.Signals;

namespace Wavee.Theming;

/// <summary>One card of a theme picker: a theme as DATA (ids and loc keys, never a composition).</summary>
/// <param name="Id">The theme id (what <c>ThemeHost.Switch</c> takes).</param>
/// <param name="NameKey">The loc key of its name.</param>
/// <param name="DescriptionKey">The loc key of its one-line description, or null.</param>
/// <param name="Preview">Its preview images; a picker draws a miniature when the asset is missing.</param>
/// <param name="Availability">Who can pick it.</param>
/// <param name="Selected">It is the active theme.</param>
public sealed record ThemePickerEntry(string Id, string NameKey, string? DescriptionKey, ThemePreview Preview,
    ThemeAvailability Availability, bool Selected);

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

    /// <summary>The cards a picker shows: <see cref="Visible"/> plus the ACTIVE theme even when it is Developer-only and
    /// Developer mode is off (the picker must always show what is selected), in install order.</summary>
    public IReadOnlyList<ThemePickerEntry> Entries(bool developerMode)
    {
        var entries = new List<ThemePickerEntry>(Installed.Count);
        foreach (var m in Installed)
        {
            bool selected = string.Equals(m.Id, CurrentId, StringComparison.OrdinalIgnoreCase);
            if (!developerMode && m.Availability != ThemeAvailability.Public && !selected) continue;
            entries.Add(new ThemePickerEntry(m.Id, m.DisplayNameKey, m.DescriptionKey, m.Preview, m.Availability, selected));
        }
        return entries;
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
