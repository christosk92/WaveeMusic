// ── Shell/Stage.Options.UI.cs ──────────────────────────────────────────────────────────────────────────────────────────
// The "Full screen" options popover's core opener (design A, two tabs), opened from the top bar's sliders button
//
// Role: UI (the core half; the picture is the structure's `Stage.Options` view, T7a1)
// Owner: K
// Wave: 7
// Spec: PopoverTabsVisualizer.dc.html / PopoverTabsLayout.dc.html (440 wide · 20 padding · tabs with a 3 px accent underline)
//
// ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// An overlay mounts OUTSIDE the stage subtree, so the opener re-provides the stage context around the structure's view
// (`ViewKeys.StageOptions`, Classic: `Views\Stage\ClassicStageOptions.cs`: the tabs, the visualizer grid, the layout rows, the hero
// options). The open / close toggle, the light dismiss, the focus trap and the chrome's counted hold are SurfaceCore's
// (`_openOptions`); the preference writes the picture makes travel as `IStageSettingsVerbs` (`StageSettingsVerbs`).

using FluentGpu.Dsl;
using FluentGpu.Hooks;
using Wavee.Theming.Contracts;
using ViewSlots = FluentGpu.Theming.Slots;

namespace Wavee;

public static partial class Stage
{
    /// <summary>The options popover, re-providing the stage context (it mounts in the overlay layer, outside the stage subtree).</summary>
    static Element OptionsContent(StageCtx ctx, Action close)
        => Ctx.Provide(StageContext, ctx, ViewSlots.View(ViewKeys.StageOptions,
            new StageOptionsProps { Stage = ctx, Verbs = StageSettingsVerbs.Shared, Close = close }));
}
