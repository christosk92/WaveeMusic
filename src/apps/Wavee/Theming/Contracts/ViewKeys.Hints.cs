// ── Theming/Contracts/ViewKeys.Hints.cs — the hint, the explanation badge and the in-context dials (contract 1.115) ─────────────
//
// Settings rework phase 3 (handoff/settings-2026-11-18/DESIGN.md 5.2, 6). Three keys, each ONE key for every place it appears: the
// surface travels in the props. Every theme gets the Common stand-ins (`Themes\Common\CommonHint.cs`) until it draws its own (phases 4-6).

using FluentGpu.Theming;

namespace Wavee.Theming.Contracts;

public static partial class ViewKeys
{
    /// <summary>A hint on its surface (design 6.1, 6.3): the line, its one action, "×" and "Don't show tips"; a state line; a pointer;
    /// a dot. Mounted by the core hint slot of a surface (<c>Hints.Slot</c>) only while a hint qualifies (or <c>hint show</c> forces one).
    /// Props: <see cref="HintProps"/>.</summary>
    public static readonly ViewKey<HintProps> Hint = new("Hint.Line");

    /// <summary>A dial's explanation badge and the tip it opens (design 6.5): the clip and its caption. Mounted beside a dial's label only
    /// where the clip exists for the active theme and app mode. Props: <see cref="ExplainerProps"/>.</summary>
    public static readonly ViewKey<ExplainerProps> Explainer = new("Hint.Explainer");

    /// <summary>A short list of dials drawn in a feature's own panel (design 5.2), bound to the same value and writer as Settings.
    /// Props: <see cref="SettingsDialsProps"/>.</summary>
    public static readonly ViewKey<SettingsDialsProps> SettingsDials = new("Settings.Dials");
}
