// ── Themes/Classic/Structure/Views/Nav/ClassicEditBarParts.cs — the edit bar's parts, shared by the sidebar's Edit mode and Home's Edit Home ──────
//
// Classic's one edit vocabulary (spec 8.3, pinned-home-tech.md 1.7): a pencil and a caption, Undo and Redo as icon buttons with tooltips, a subtle
// "Reset" that opens a menu, and the accent "Done". The sidebar's bar (`ClassicEditPane.EditBar`) and Home's bar (`ClassicHomeEditBar`) build their
// rows from these parts, so the two bars are one design and the sidebar's renders exactly as before. Each part is a pure element factory.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Classic;

/// <summary>The edit bar's parts.</summary>
internal static class ClassicEditBarParts
{
    /// <summary>The pencil before the caption.</summary>
    public static Element Pencil() => Icon(Icons.Edit, ClassicNavEditLayout.PencilSize, Tok.TextSecondary);

    /// <summary>The bar's caption ("Edit sidebar", "Edit Home"): secondary semibold, one line, cut short when the bar is narrow.</summary>
    public static TextEl Caption(string text) => Ui.Caption(text) with
    {
        Weight = 600, Color = Tok.TextSecondary, MaxLines = 1, Shrink = 1f, MinWidth = 0f,
        Trim = TextTrim.CharacterEllipsis,
    };

    /// <summary>Undo, with its tooltip.</summary>
    public static Element Undo(Action undo, bool enabled) => ToolTip.Wrap(IconButton.Create(Icons.Undo, undo, isEnabled: enabled), Loc.Get("sidebar.undo.undoAction"));

    /// <summary>Redo, with its tooltip.</summary>
    public static Element Redo(Action redo, bool enabled) => ToolTip.Wrap(IconButton.Create(Icons.Redo, redo, isEnabled: enabled), Loc.Get("sidebar.undo.redo"));

    /// <summary>The subtle "Reset" button; the caller attaches its menu (a click requests the context menu).</summary>
    public static BoxEl Reset(string label) => Button.Create(label, static () => { }, ButtonAppearance.Subtle, ControlSize.Small);

    /// <summary>The accent "Done".</summary>
    public static BoxEl Done(Action done) => Button.Accent(Loc.Get("sidebar.edit.done"), done);

    /// <summary>The accent "Done" at a size (Home's bar at Compact: Large, the 40 target floor).</summary>
    public static BoxEl Done(Action done, ControlSize size) => Button.Create(Loc.Get("sidebar.edit.done"), done, ButtonAppearance.Accent, size);

    /// <summary>Undo as a <paramref name="box"/>-DIP glyph target (Home's bar at Compact: 40), with its tooltip.</summary>
    public static Element Undo(Action undo, bool enabled, float box)
        => ToolTip.Wrap(Controls.IconAction(Icons.Undo, undo, size: box, isEnabled: enabled), Loc.Get("sidebar.undo.undoAction"));

    /// <summary>Redo as a <paramref name="box"/>-DIP glyph target, with its tooltip.</summary>
    public static Element Redo(Action redo, bool enabled, float box)
        => ToolTip.Wrap(Controls.IconAction(Icons.Redo, redo, size: box, isEnabled: enabled), Loc.Get("sidebar.undo.redo"));
}
