// ── Theming/Contracts/NavRowKit.cs — what a structure supplies for the navigation pane's list (themes plan 6.1, T5a) ─────────
//
// THE PANE IS A CORE HOST AND A THEME VIEW. The core pane (`Sidebar.PaneView`) owns when a plan is built, published and
// swept: the binder's revision, the drag session, the disclosure channel, the per-row epochs, the selection travel, the
// reorder bands. What the structure owns is the LIST: how (layout document x projection) is flattened into rows and which
// section holds a route (the planner, X26), which realized rows a republish must re-skin (the diff), how tall a row is
// before anything measures (the analytic extent), that a re-projection arriving mid-drag is parked until the session ends
// (the freeze), and what one row looks like (the slot). The structure hands all of it to the pane as ONE kit when the
// pane mounts (`Shell.NavPane` -> `NavPaneProps.Mount(kit)`), so core never names a structure and a second theme plans and
// draws another list over the same pane.
//
// `INavRowHost` (NavRowHost.cs) is the other direction: what a slot may ask of the pane.

using FluentGpu.Controls;
using FluentGpu.Dsl;

namespace Wavee.Theming.Contracts;

/// <summary>The mid-drag parking bay for a plan stage: the newest stage is HELD instead of published while a rootlist drag is
/// live, and applied exactly once on SESSION END. Last writer wins; a stage produced after the session ended is never held.</summary>
public interface INavStageHold<TStage> where TStage : class
{
    /// <summary>Is a stage parked right now? Diagnostics and tests only.</summary>
    bool HasHeld { get; }

    /// <summary>Hold <paramref name="stage"/> instead of publishing it, when <paramref name="sessionLive"/>. TRUE when the
    /// stage was parked (the caller must NOT publish); FALSE when no session is live.</summary>
    bool TryHold(bool sessionLive, TStage stage);

    /// <summary>Hand back the parked stage EXACTLY ONCE.</summary>
    bool TryFlush(out TStage? stage);

    /// <summary>Drop the parked stage without publishing it.</summary>
    void Discard();
}

/// <summary>What a structure supplies for the pane's list (see the file header). One instance per structure, stateless.</summary>
public abstract class NavRowKit
{
    /// <summary>(layout document x projection) -> ONE flat row list, for the expanded pane or the rail. The buffers are the
    /// CALLER's and the plan ALIASES them.</summary>
    public abstract SidebarRowPlan Plan(SidebarLayoutDoc doc, in SidebarProjectionInput input, in SidebarPlanOptions options,
        SidebarPlanBuffers buffers);

    /// <summary>One section's body, expanded, top level only for trees: a compact section tile's flyout.</summary>
    public abstract SidebarRowPlan PlanSection(SidebarLayoutDoc doc, string sectionId, in SidebarProjectionInput input,
        SidebarPlanBuffers buffers);

    /// <summary>The first visible top-level section of <paramref name="doc"/> that would hold <paramref name="route"/> if it were
    /// expanded (the pill's header or folder anchor), or null. <paramref name="isPinned"/> asks the pin store.</summary>
    public abstract string? OwningSection(SidebarLayoutDoc doc, in SidebarProjectionInput input, string route,
        Func<string?, bool> isPinned);

    /// <summary>Does row <paramref name="index"/> of the new plan render differently from the same slot of the old?</summary>
    public abstract bool RowChanged(
        IReadOnlyList<SidebarRow> oldRows, IReadOnlyList<SidebarLibraryEntry> oldEntries,
        IReadOnlyList<SidebarRow> newRows, IReadOnlyList<SidebarLibraryEntry> newEntries, int index);

    /// <summary>The analytic extent of plan row <paramref name="index"/> (NaN = measure): it SEEDS the virtualizing host's
    /// extent table so a disclosure lands at its final geometry before anything is realized.</summary>
    public abstract float ExtentOf(IReadOnlyList<SidebarRow> rows, int index, SidebarSection? section);

    /// <summary>How the pane presents a drag: the drag-dim policy of its drop targets and the reorder bands' lift (T5e). The cues
    /// themselves are drawn by the slot.</summary>
    public abstract NavDropLook DropLook { get; }

    /// <summary>A fresh mid-drag parking bay for a plan stage (one per pane).</summary>
    public abstract INavStageHold<TStage> NewStageHold<TStage>() where TStage : class;

    /// <summary>The bound slot of one plan row: a component the list builds once per slot and recycles by writing the scope's
    /// index. It reads the plan and the per-row epoch through <paramref name="host"/>.</summary>
    internal abstract Element Slot(INavRowHost host, RowScope scope);
}
