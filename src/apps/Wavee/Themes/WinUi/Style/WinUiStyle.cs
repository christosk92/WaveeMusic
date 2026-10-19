// ── Themes/WinUi/Style/WinUiStyle.cs — the WinUI style: a STUB (themes plan 6.2; P6 W0) ──────────────────────────────────────────────
//
// W0 STUB, REPLACED WHOLE BY W1 (the `Style\` folder is W1's). The entry point is Zune-shaped so W1 drops in without touching the composition:
// `WinUiStyle.LayerId` ("WinUi.Style", the id Panorama's manifest names) and `WinUiStyle.Create()`.
//
// Until W1 lands it publishes Classic's values without naming Classic (WVTH001: a style file references its own theme only):
//   · the DEFINITION is Wavee's (`Design.Definition`) carrying NO roles, so every `R.*` read falls back to the process
//     baseline (Classic's table, `R.SetBaseline`); like Classic it follows the Windows accent (one instance per OS ramp);
//   · the METRICS are Classic's numbers at the Default row density;
//   · NO options.

using FluentGpu.Dsl;
using FluentGpu.Theming;
using Wavee.Theming;

namespace Wavee.Themes.WinUi;

/// <summary>The style of the WinUI theme (W0 stub: Classic's values until W1).</summary>
public sealed class WinUiStyle : IStyleLayer
{
    /// <summary>The style id.</summary>
    public const string LayerId = "WinUi.Style";

    /// <summary>The style layer the WinUI theme composes.</summary>
    public static WinUiStyle Create() => new();

    // Built once (the composition's identity rule wants a stable instance per input). Wavee's definition without a role table.
    private static readonly ThemeDefinition s_definition = Design.Definition with { AppRoles = null };

    private static readonly object s_gate = new();
    private static AccentRamp s_lastRamp = AccentRamp.FluentDefault;
    private static ThemeDefinition s_lastDefinition = s_definition;

    /// <inheritdoc />
    public string Id => LayerId;

    /// <inheritdoc />
    public ThemeOptionSchema Options => ThemeOptionSchema.Empty;

    /// <inheritdoc />
    public ThemeDefinition Definition(OptionValues style, AccentRamp osAccent)
    {
        if (osAccent == AccentRamp.FluentDefault) return s_definition;
        lock (s_gate)
        {
            if (s_lastRamp != osAccent)
            {
                s_lastDefinition = s_definition with { Accent = osAccent };
                s_lastRamp = osAccent;
            }
            return s_lastDefinition;
        }
    }

    /// <inheritdoc />
    public StyleMetrics Metrics(OptionValues style) => s_metrics;

    /// <summary>Classic's metrics at the Default density (W1 replaces them with WinUI's).</summary>
    private static readonly StyleMetrics s_metrics = new(
        RowSingle: Design.Size.NavItemH, RowDouble: Design.Size.TrackRowH, RowWithArt: Design.Size.TrackRowH,
        ControlHeight: Design.Size.ControlH, PivotRow: 0f, SubPivotRow: 0f, SeekRow: 0f, TransportRow: Design.Size.PlayerBarH,
        SearchWidth: 420f, Gutter: Design.Definition.Spacing.PageWide, Section: 32f,
        MinPageColumn: 480f, PanoramaCompactBelow: 0f,
        DockedPane: 380f, DockedPane4K: 420f, PaneItem: 36f, CardGap: 12f,
        TrackRows: new(Rung: 1, ModernRow: 48f, ModernArt: 32f, ClassicRow: 40f, ClassicArt: 32f, EpisodeRow: 96f, NarrowEpisode: true));
}
