// ── Themes/Classic/Style/ClassicStyle.cs — the Classic style (themes plan 6.1) ─────────────────────────────────────────
//
// T1: the style IS today's `Design.Definition` (the engine's Neutral palette with Wavee's fonts), returned unchanged for
// every option set and accent, so the definition the composition resolves is the instance the appearance seed already
// installed. T2a/T2b move colour, type, space and shape out of `Design` into this layer's roles and publish them as the
// definition's `AppRoles`; no option exists yet.

using FluentGpu.Dsl;
using FluentGpu.Theming;
using Wavee.Theming;

namespace Wavee.Themes.Classic;

/// <summary>The style of Wavee Classic.</summary>
public sealed class ClassicStyle : IStyleLayer
{
    /// <summary>The style id.</summary>
    public const string LayerId = "Classic.Style";

    /// <inheritdoc />
    public string Id => LayerId;

    /// <inheritdoc />
    public ThemeOptionSchema Options => ThemeOptionSchema.Empty;

    /// <inheritdoc />
    public ThemeDefinition Definition(OptionValues style, AccentRamp osAccent) => Design.Definition;

    /// <inheritdoc />
    public StyleMetrics Metrics(OptionValues style) => StyleMetrics.Unset;
}
