// ── Themes/Classic/Style/ClassicStyle.cs — the Classic style (themes plan 6.1) ─────────────────────────────────────────
//
// The style IS today's `Design.Definition` (the engine's Neutral palette with Wavee's fonts) carrying Classic's roles
// (`ClassicRoles`, T2a), returned as ONE instance for every option set and accent, so the definition the composition resolves
// is the instance the appearance seed installs and a switch back to Classic adopts by reference. Colour joins the roles in T2b;
// no option exists yet.

using FluentGpu.Dsl;
using FluentGpu.Theming;
using Wavee.Theming;

namespace Wavee.Themes.Classic;

/// <summary>The style of Wavee Classic.</summary>
public sealed class ClassicStyle : IStyleLayer
{
    /// <summary>The style id.</summary>
    public const string LayerId = "Classic.Style";

    // Built once: Tok.Use compares definitions by value, but the composition's identity rule (an unchanged pairing installs exactly
    // the style's own instance) and ThemeHost.ApplyDefinition's reference check both want a stable instance.
    private static readonly ThemeDefinition s_definition = Design.Definition with { AppRoles = ClassicRoles.Table };

    /// <inheritdoc />
    public string Id => LayerId;

    /// <inheritdoc />
    public ThemeOptionSchema Options => ThemeOptionSchema.Empty;

    /// <inheritdoc />
    public ThemeDefinition Definition(OptionValues style, AccentRamp osAccent) => s_definition;

    /// <inheritdoc />
    public StyleMetrics Metrics(OptionValues style) => StyleMetrics.Unset;
}
