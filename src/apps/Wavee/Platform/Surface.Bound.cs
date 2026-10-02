// ── Platform/Surface.Bound.cs ──────────────────────────────────────────────────────────────────────────────────────
// THE way a media surface enters a BOUND ItemsView slot: the slot root (invoke + the roving tab stop) around the ONE host.
//
// Role: UI
// Owner: L
// Wave: 0 (shared media surface)
// Budget: 120 lines
// Spec: docs/plans/wavee/shared-media-surface-implementation.md Appendix A.1
//
// The engine's own `PagedShelf.BindCard` is the same shape for a shelf (it provides `ItemsView.SlotRow` from its card
// slot), so a `Controls.Surface` built by a shelf's `CardAt(item, index, width)` template goes slot-mode with no change to
// the template. This is the grid/list counterpart for an app-built `ItemsView.CreateBound` source.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;

namespace Wavee;

public static partial class Controls
{
    /// <summary>THE way a media surface enters a BOUND <see cref="ItemsView"/> slot. Returns the slot ROOT: it owns invoke
    /// and focus — <c>Focusable = false</c> for the roving tab stop (the ItemsView toggles the current slot's focusability
    /// imperatively), press/Enter/Space → <see cref="RowScope.OnInteraction"/>, focus → <see cref="RowScope.OnFocusChanged"/>
    /// (which also drives <see cref="RowScope.IsFocused"/>) — and provides <see cref="ItemsView.SlotRow"/> to the ONE
    /// <see cref="SurfaceHost"/> inside it, so the host renders click-less and focus-less (Surface.Host.cs).
    /// <para><paramref name="adapt"/> runs inside the slot component's render: its subscribing reads (a table's
    /// <c>Changed</c>) re-describe a hydrating card. Null from <paramref name="adapt"/> = a blank item → the host's seed
    /// face (the caller ALSO disables the row through <c>IsItemEnabledTyped</c>, which dims this root and refuses invoke).
    /// The template runs once per persistent slot; a rebind costs the slot's re-render and one props re-push.</para>
    /// <para>No cursor here: the shell inside covers the slot and carries the hand through
    /// <see cref="SurfaceRules.Cursor"/> (in a slot ⇒ Hand). The corners match the shell's so the focus ring the engine
    /// draws around this root is concentric with the surface.</para></summary>
    public static BoxEl BoundSurface<T>(in BoundItemScope<T> item, SurfaceShape shape, Func<T, CardData?> adapt)
    {
        var scope = item;                                   // a copy the slot factory can hold (an `in` cannot be captured)
        return SlotRoot(scope.Row, shape, Embed.Comp(() => new SurfaceSlot<T>(scope, shape, adapt)));
    }

    /// <summary>The same slot root around ONE <see cref="Surface"/> for a caller that is NOT an <see cref="ItemsView"/> but
    /// owns its own row cursor (the search flyout: focus stays in the text field, an arrow key moves a highlight).
    /// <paramref name="scope"/> is the caller's synthesized <see cref="RowScope"/>: <see cref="RowScope.IsFocused"/> is the
    /// "this row is highlighted" fact (the host renders hot while it is true), <see cref="RowScope.OnInteraction"/> is the
    /// choose action. The data is pushed by the caller's own render, so a re-render re-describes the row.</summary>
    public static BoxEl SlotSurface(RowScope scope, CardData data, SurfaceShape shape)
        => SlotRoot(scope, shape, Surface(data, shape));

    /// <summary>The slot root shared by <see cref="BoundSurface{T}"/> and <see cref="SlotSurface"/>.</summary>
    static BoxEl SlotRoot(RowScope row, SurfaceShape shape, Element content)
    {
        Func<bool> isEn = row.IsEnabled;
        var interact = row.OnInteraction;
        return new BoxEl
        {
            Direction = 1, Focusable = false, Role = AutomationRole.Button,
            Corners = CornerRadius4.All(shape.IsRow ? Radii.Control : Radii.Card),
            FocusVisualMargin = shape.IsRow ? Design.FocusInsetRow : Design.FocusInsetBordered,
            Opacity = Prop.Of(() => isEn() ? 1f : ItemContainer.DisabledOpacity),
            OnPointerReleased = args => interact(RowClickPolicy.TriggerOf(args.ClickCount, args.Mods), args.Mods),
            OnKeyDown = args =>
            {
                if (args.KeyCode == Keys.Enter) { interact(ItemContainerTrigger.EnterKey, args.Mods); args.Handled = true; }
                else if (args.KeyCode == Keys.Space && !args.IsRepeat) { interact(ItemContainerTrigger.SpaceKey, args.Mods); args.Handled = true; }
            },
            OnFocusChanged = row.OnFocusChanged,
            Children = [Ctx.Provide<RowScope?>(ItemsView.SlotRow, row, content)],
        };
    }

    /// <summary>The per-slot component: reads the slot's equality-gated item memo, adapts it, re-pushes the ONE host.
    /// Always the same element type (a <see cref="Surface"/> component), so blank → live is a props re-push, never a
    /// remount.</summary>
    sealed class SurfaceSlot<T>(BoundItemScope<T> item, SurfaceShape shape, Func<T, CardData?> adapt) : Component
    {
        public override Element Render()
        {
            var t = item.Item.Value;                            // subscribes the per-slot memo: a recycle re-renders this slot
            return Surface(adapt(t) ?? CardData.Seed, shape);   // the adapter's own subscribing reads ride this render
        }
    }
}
