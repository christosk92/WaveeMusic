// ── Themes/Classic/Structure/Views/Nav/ClassicNavRows.cs — Classic's `NavRowKit` (themes plan 6.1, T5a) ───────────────────
//
// What Classic supplies for the pane's list, in one stateless object the template hands the core pane at mount: the planner and
// its owning-section answer (`SidebarRowPlanner`, X26), the publish diff (`SidebarPlanDiff`), the analytic extents
// (`SidebarRowExtents`), the mid-drag freeze (`SidebarStageHold<T>`; all in `Rules\SidebarPlanner.cs`), the row slot
// (`ClassicNavRow`, which also draws the drop cues), the drag presentation (`NavDropLook`, T5e) and, since T5f, the ladder answers core's pane
// still needs (the drop resolver's depth ladder, the list's inset, a row's pitch, the pill's height, the Library grid's columns): the
// core keeps no copy of `SidebarGeometry`. Since P3 it also hands the core the boxes the pane is built in (`ClassicNavFrame`: the layout
// host's box, the pane's frame and context-menu shield, the list's inset box).

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

/// <summary>Classic's rows for the navigation pane.</summary>
public sealed class ClassicNavRows : NavRowKit
{
    /// <summary>The one instance: the kit holds no state, so a pane, a drawer and a test share it.</summary>
    public static readonly ClassicNavRows Kit = new();

    ClassicNavRows() { }

    /// <inheritdoc />
    public override NavTreeLadder TreeLadder => new(SidebarGeometry.TreeContentX(0), SidebarGeometry.IndentStep);

    /// <inheritdoc />
    public override Edges4 ListInset => SidebarGeometry.ListInset;

    /// <inheritdoc />
    public override float PitchOf(SidebarRowShape shape) => SidebarGeometry.PitchOf(shape);

    /// <inheritdoc />
    public override float PillHeight => SidebarGeometry.PillH;

    /// <inheritdoc />
    public override int GridColumns(float paneWidth) => SidebarGeometry.GridColumns(paneWidth);

    /// <inheritdoc />
    public override SidebarRowPlan Plan(SidebarLayoutDoc doc, in SidebarProjectionInput input, in SidebarPlanOptions options,
        SidebarPlanBuffers buffers)
        => SidebarRowPlanner.Build(doc, in input, in options, buffers);

    /// <inheritdoc />
    public override SidebarRowPlan PlanSection(SidebarLayoutDoc doc, string sectionId, in SidebarProjectionInput input,
        SidebarPlanBuffers buffers)
        => SidebarRowPlanner.BuildSection(doc, sectionId, in input, buffers);

    /// <inheritdoc />
    public override string? OwningSection(SidebarLayoutDoc doc, in SidebarProjectionInput input, string route, Func<string?, bool> isPinned)
        => SidebarRowPlanner.OwningSection(doc, in input, route, isPinned);

    /// <inheritdoc />
    public override bool RowChanged(
        IReadOnlyList<SidebarRow> oldRows, IReadOnlyList<SidebarLibraryEntry> oldEntries,
        IReadOnlyList<SidebarRow> newRows, IReadOnlyList<SidebarLibraryEntry> newEntries, int index)
        => SidebarPlanDiff.RowChanged(oldRows, oldEntries, newRows, newEntries, index);

    /// <inheritdoc />
    public override float ExtentOf(IReadOnlyList<SidebarRow> rows, int index, SidebarSection? section)
        => SidebarRowExtents.HeightOf(rows, index, section);

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
    public override INavStageHold<TStage> NewStageHold<TStage>() => new SidebarStageHold<TStage>();

    /// <inheritdoc />
    internal override Element Slot(INavRowHost host, RowScope scope) => Embed.Comp(() => new ClassicNavRow(host, scope));

    /// <inheritdoc />
    public override Element ModeHost(Element mode) => ClassicNavFrame.ModeHost(mode);

    /// <inheritdoc />
    public override Element Frame(in NavPaneFrameArgs args) => ClassicNavFrame.Frame(in args);

    /// <inheritdoc />
    public override Element ListBox(Element list) => ClassicNavFrame.ListBox(list);
}
