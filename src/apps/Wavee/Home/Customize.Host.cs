// ── Home/Customize.Host.cs — the Home customizer's CORE HOST over home-layout.json v2 (T10a1) ─────────────────────────────
//
// Role: CORE host (the picture — the header with Reset, the reorderable zone rows with their gripper, label, Up/Down and visibility toggle —
//   is Classic's `Pages.HomeCustomize` view: `Themes\Classic\Structure\Views\Home\ClassicHomeCustomize.cs`)
// Owner: B5
// Wave: 2
// Spec: docs/plans/wavee/home-redesign-implementation.md — Workstream H, New files table row `Customize.UI.cs`
//   ("CustomizeScreen (ToggleSwitch visibility + Reorderable), entry: 'Customize Home' link at page end + palette
//   command"); "Customizer: fresh home-layout.json v2 … All facet only" (Removals section);
//   docs/plans/wavee/home-redesign-remediation.md §3.7 (the `HomeLayout` service replacing the two private copies).
//
// Owner rule: NO reference to `HomeCustomizerPage`/`Home.Customizer.cs`/`HomeLayoutStore`/`HomePreferences` — read
// only to learn the shape of the problem. Every type here rides the fresh v2 store: `Home/LayoutFile.cs`'s `LayoutZone`,
// `LayoutDoc`, `LayoutEntry`, `LayoutCommands` and the `HomeLayout` service (Wave 2, F29) — All facet only, per the plan. No local
// `LayoutStore`/document copy here: this screen and `HomeScreen` both read the ONE `HomeLayout` instance through
// `UseRequiredContext(HomeLayout.Slot)`, so a toggle/reorder here is visible on Home the same frame it commits (F29 — the old
// customizer's private `_layoutDoc` never synced with Home's).
//
// WHAT THE HOST KEEPS. The service, the three verbs (`LayoutCommands.Toggle/Move/Reset` through `HomeLayout.Dispatch`, whose .bak rotation is
// `LayoutStore`'s) and the drag-reorder behaviour object (`Reorderable`: an engine behaviour, owned by the host, handed to the view). The
// view wires the reorderable's scene and render request from its OWN render context, because a drag asks for a re-render of the component
// that builds the rows; it also gives the reorderable its row extent (the picture's number).

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Hooks;
using Wavee;
using Wavee.Theming.Contracts;
using Slots = FluentGpu.Theming.Slots;

namespace Wavee.HomeUi;

/// <summary>A list of <see cref="LayoutZone"/> rows for the All facet: a <see cref="ToggleSwitch"/> for visibility,
/// drag-reorder, and Up/Down fallback buttons for keyboard use. Every mutation goes through
/// <see cref="LayoutCommands"/> and lands via <see cref="HomeLayout.Dispatch"/> (the .bak rotation is
/// <see cref="LayoutStore"/>'s, not this file's).</summary>
[CoreHost]
public sealed class CustomizeScreen : Component
{
    const string DragKind = "wavee.home-zone-layout";

    /// <summary>Route-constructible factory — C1 (Wave 4) wires <c>Shell.SetPage(RouteKind.HomeCustomize, …)</c> to
    /// this; this file does not touch <c>Shell.cs</c>.</summary>
    public static Element For() => Embed.Comp(static () => new CustomizeScreen());

    readonly Reorderable _reorder;
    readonly Action<int, int> _onReorder;
    readonly Action<LayoutZone> _onToggle;
    readonly Action _onReset;

    /// <summary>The context-resolved service, refreshed every render (hooks run unconditionally at the top of
    /// <see cref="Render"/>) — <see cref="Toggle"/>/<see cref="Move"/>/<see cref="Reset"/> read this FIELD rather
    /// than re-resolving the context, since they run from a later frame's click/drop callback, not from render.</summary>
    HomeLayout? _layout;

    public CustomizeScreen()
    {
        _onReorder = Move;
        _onToggle = Toggle;
        _onReset = Reset;
        // The row dims in place (opacity, in the view) — no custom drag chip; the engine's own ghost lift is enough for a
        // 9-row settings list (unlike the old customizer's module chip, which existed for a much longer list). The row extent is the
        // view's (it sets `ItemExtent` from its own row height).
        _reorder = new Reorderable(DragKind) { Spacing = 0f, RequireDropOnList = true, OnReorder = _onReorder };
    }

    void Toggle(LayoutZone zone) => _layout!.Dispatch(doc => LayoutCommands.Toggle(doc, zone));
    void Move(int from, int to) => _layout!.Dispatch(doc => LayoutCommands.Move(doc, from, to));
    void Reset() => _layout!.Dispatch(_ => LayoutCommands.Reset());

    public override Element Render()
    {
        var layout = UseRequiredContext(HomeLayout.Slot);
        _layout = layout;
        return Slots.View(ViewKeys.HomeCustomize, new HomeCustomizeProps
        {
            Doc = layout.Doc.Value, Reorder = _reorder, Toggle = _onToggle, Move = _onReorder, Reset = _onReset,
        });
    }
}
