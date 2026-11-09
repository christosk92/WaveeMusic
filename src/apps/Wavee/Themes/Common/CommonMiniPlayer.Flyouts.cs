// ── Themes/Common/CommonMiniPlayer.Flyouts.cs — the mini player's two menus (#197) ───────────────────────────────────────
//
// Options (⋯) is the app's own menu, the one every ⋯ in the theme opens (`MenuFlyout`, restyled by the theme's control table: WinUI's
// Fluent menu, Classic's, Zune's lowercase rows): a "Shape" sub-menu (Strip, Wide, Square, Tall as radio rows, each with its outline),
// a "Face" sub-menu (the eight faces as radio rows), keep on top and colour from album art as check rows, and "Back to Wavee" with its
// shortcut where a menu shows accelerators. Play on (the device button) is the player bar's own device picker (`Player.DevicePicker`,
// toggled by the shell), not a copy.
//
// Both escape the small window into a popup over the screen, anchored to the window's top right (where ⋯ sits in every shape) and
// opening above it, or below it when the screen has no room above: a menu never lands on the mini player's own controls.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using Wavee.Theming.Contracts;
using H = Wavee.Themes.Common.CommonMiniPlayerRules;

namespace Wavee.Themes.Common;

internal static partial class CommonMiniPlayer
{
    static readonly MiniShape[] s_shapes = [MiniShape.Strip, MiniShape.Wide, MiniShape.Square, MiniShape.Tall];

    static string ShapeName(MiniShape s) => s switch
    {
        MiniShape.Strip => Loc.Get(Strings.Mini.Strip),
        MiniShape.Square => Loc.Get(Strings.Mini.Square),
        MiniShape.Tall => Loc.Get(Strings.Mini.Tall),
        _ => Loc.Get(Strings.Mini.Wide),
    };

    /// <summary>A shape's outline in a menu row (a vector icon drawn at the shape's own ratio, inked as the row's glyphs).</summary>
    static IconRef ShapeIcon(MiniShape s) => IconRef.Themed(s switch
    {
        MiniShape.Strip => H.ShapeIconStrip,
        MiniShape.Square => H.ShapeIconSquare,
        MiniShape.Tall => H.ShapeIconTall,
        _ => H.ShapeIconWide,
    });

    /// <summary>The ⋯ menu: built at open from live reads (a pick closes it, as every menu row does).</summary>
    static void OpenOptions(MiniPlayerProps p, MiniPlayerStyle st, IOverlayService overlay, Anchors a)
    {
        if (a.OptionsMenu.Value is { IsOpen: true } open) { open.Close(); return; }
        MiniShapeIcons.Ensure();
        var shape = p.Shape.Peek();
        int face = Math.Max(0, p.Face.Peek());

        var shapes = new MenuFlyoutItem[s_shapes.Length];
        for (int i = 0; i < shapes.Length; i++)
        {
            var s = s_shapes[i];
            shapes[i] = MenuFlyoutItem.RadioItem(ShapeName(s), s == shape, () => p.SetShape(s), ShapeIcon(s));
        }
        var faces = new MenuFlyoutItem[p.FaceCount];
        for (int i = 0; i < faces.Length; i++)
        {
            int fi = i;
            faces[i] = MenuFlyoutItem.RadioItem(p.FaceName(fi), fi == face, () => p.SetFace(fi));
        }
        bool onTop = p.OnTop.Peek();
        bool art = p.ArtColour(st.ArtColourSlot, st.ArtColourDefault);
        int slot = st.ArtColourSlot;
        MenuFlyoutItem[] items =
        [
            MenuFlyoutItem.SubMenu(Loc.Get(Strings.Mini.Shape), shapes, ShapeIcon(shape)),
            MenuFlyoutItem.SubMenu(Loc.Get(Strings.Mini.Face), faces, Icons.Picture),
            MenuFlyoutItem.Separator,
            MenuFlyoutItem.Toggle(Loc.Get(Strings.Mini.OnTop), onTop, p.ToggleOnTop, Icons.Pin),
            MenuFlyoutItem.Toggle(Loc.Get(Strings.Mini.ArtColour), art, () => p.SetArtColour(slot, !art), Icons.Brush),
            MenuFlyoutItem.Separator,
            new MenuFlyoutItem(Loc.Get(Strings.Mini.Back), Icons.BackToWindow, true, p.BackToWavee) { AcceleratorText = H.BackAccelerator },
        ];
        var handle = a.OptionsMenu;
        handle.Value = overlay.Open(() => a.Window.Value, () => MenuFlyout.Create(items, () => handle.Value?.Close(), size: OverlayFit.MenuSize),
            FlyoutPlacement.TopEdgeAlignedRight, new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = false });
        handle.Value.ClosedAction = () => handle.Value = null;
    }

    /// <summary>Play on: the player bar's device picker, escaping this window (the shell owns the overlay).</summary>
    static void OpenDevices(MiniPlayerProps p, IOverlayService overlay, Anchors a) => p.ToggleDevicePicker(overlay, a.Window, a.DeviceMenu);
}
