// ── Themes/Classic/Structure/Views/Nav/ClassicNavRows.cs — Classic's `NavRowKit` (themes plan 6.1, T5a) ───────────────────
//
// What Classic supplies for the pane's list, in one stateless object the template hands the core pane at mount: the planner and
// its owning-section answer (`SidebarRowPlanner`, X26), the publish diff (`SidebarPlanDiff`), the analytic extents
// (`SidebarRowExtents`), the mid-drag freeze (`SidebarStageHold<T>`; all in `Rules\SidebarPlanner.cs`) and the row slot
// (`ClassicNavRow`).

using FluentGpu.Controls;
using FluentGpu.Dsl;
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
    public override INavStageHold<TStage> NewStageHold<TStage>() => new SidebarStageHold<TStage>();

    /// <inheritdoc />
    internal override Element Slot(INavRowHost host, RowScope scope) => Embed.Comp(() => new ClassicNavRow(host, scope));
}
