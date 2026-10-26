// ── Themes/WinUi/Structure/Views/Nav/WinUiNavRows.cs — WinUI's `NavRowKit` (P6 W2b) ─────────────────────────────────────────────────
//
// What WinUI supplies for the pane's list, in one stateless object the template hands the core pane at mount: the planner and its owning-section
// answer (`WinUiNavPlanner`), the publish diff, the analytic extents, the mid-drag freeze (all in `Rules\WinUiNavPlanner.cs`), the row slot
// (`WinUiNavRow`, which also draws the drop cues), the drag presentation, the ladder answers the core pane needs (the drop resolver's depth ladder,
// the list's inset, a row's pitch, the pill's height), the boxes the pane is built in (`WinUiNavFrame`) and the FOOTER (Settings, `WinUiNavFooter`).
// Derived from Classic's `ClassicNavRows`.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.WinUi;

/// <summary>Classic's rows for the navigation pane.</summary>
public sealed class WinUiNavRows : NavRowKit
{
    /// <summary>The one instance: the kit holds no state, so a pane, a drawer and a test share it.</summary>
    public static readonly WinUiNavRows Kit = new(WinUiNavMetrics.Standard);

    /// <summary>The touch kit: the same list at the 40 target the Compact window tier asks for (the pane in the drawer).</summary>
    public static readonly WinUiNavRows TouchKit = new(WinUiNavMetrics.Touch);

    readonly WinUiNavMetrics _metrics;

    WinUiNavRows(WinUiNavMetrics metrics) => _metrics = metrics;

    /// <inheritdoc />
    public override NavTreeLadder TreeLadder => new(WinUiNavGeometry.TreeContentX(0), WinUiNavGeometry.IndentStep);

    /// <inheritdoc />
    public override Edges4 ListInset => WinUiNavGeometry.ListInset;

    /// <inheritdoc />
    public override float PitchOf(SidebarRowShape shape) => WinUiNavGeometry.PitchOf(shape);

    /// <inheritdoc />
    public override float PillHeight => WinUiNavGeometry.PillH;

    /// <inheritdoc />
    public override int GridColumns(float paneWidth) => 2;   // WinUI has no Library grid; the core asks anyway

    /// <inheritdoc />
    public override SidebarRowPlan Plan(SidebarLayoutDoc doc, in SidebarProjectionInput input, in SidebarPlanOptions options,
        SidebarPlanBuffers buffers)
        => WinUiNavPlanner.Build(doc, in input, in options, buffers);

    /// <inheritdoc />
    public override SidebarRowPlan PlanSection(SidebarLayoutDoc doc, string sectionId, in SidebarProjectionInput input,
        SidebarPlanBuffers buffers)
        => WinUiNavPlanner.BuildSection(doc, sectionId, in input, buffers);

    /// <inheritdoc />
    public override string? OwningSection(SidebarLayoutDoc doc, in SidebarProjectionInput input, string route, Func<string?, bool> isPinned)
        => WinUiNavPlanner.OwningSection(doc, in input, route, isPinned);

    /// <inheritdoc />
    public override string? AdoptiveRoute(string route) => WinUiNavPlanner.AdoptiveRoute(route);

    /// <inheritdoc />
    public override bool RowChanged(
        IReadOnlyList<SidebarRow> oldRows, IReadOnlyList<SidebarLibraryEntry> oldEntries,
        IReadOnlyList<SidebarRow> newRows, IReadOnlyList<SidebarLibraryEntry> newEntries, int index)
        => WinUiNavPlanDiff.RowChanged(oldRows, oldEntries, newRows, newEntries, index);

    /// <inheritdoc />
    public override float ExtentOf(IReadOnlyList<SidebarRow> rows, int index, SidebarSection? section)
        => WinUiNavExtents.HeightOf(rows, index, section);

    /// <inheritdoc />
    public override NavDropLook DropLook => s_dropLook;

    // The pane's targets stay lit while the rest of the app dims; a reordered row's source is fully vacated (the vacated slot IS the
    // gap, the chip is the ONE moving visual) and the engine's own insertion line is off (mixed pitches: the row's caret draws it).
    static readonly NavDropLook s_dropLook = new()
    {
        TargetPolicy = DropTargetVisualPolicy.Spotlight,
        ReorderDragStyle = new DragVisualStyle { Lift = DragLift.Stationary, Opacity = 0f },
        ReorderInsertionLine = false,
    };

    /// <inheritdoc />
    public override INavStageHold<TStage> NewStageHold<TStage>() => new WinUiNavStageHold<TStage>();

    /// <inheritdoc />
    internal override Element Slot(INavRowHost host, RowScope scope) => Embed.Comp(() => new WinUiNavRow(host, scope, _metrics));

    /// <inheritdoc />
    public override Element ModeHost(Element mode) => WinUiNavFrame.ModeHost(mode);

    /// <inheritdoc />
    public override Element Frame(in NavPaneFrameArgs args) => WinUiNavFrame.Frame(in args);

    /// <inheritdoc />
    public override Element ListBox(Element list) => WinUiNavFrame.ListBox(list);

    /// <inheritdoc />
    internal override Element? Footer(INavRowHost host, bool compact) => WinUiNavFooter.Build(host, compact, _metrics);
}
