// ── Themes/WinUi/Structure/Views/Screens/WinUiReleaseNotes.cs — WinUI's release-notes overlays ──────────────────────────────────────────────
//
// The highlight viewer and the after-update plate are WinUI's (the sibling files). The What's-new PAGE is not registered here: WinUI shows
// Classic's page (the stand-in fills `Pages.WhatsNew`), the same page in both themes since 0.3.6. The shared helpers below serve the overlays.

using FluentGpu.Hooks;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;
using static Wavee.ReleaseNotes;
using K = Wavee.Themes.WinUi.WinUiReleaseNotesRules;

namespace Wavee.Themes.WinUi;

/// <summary>Wavee Classic's release-notes surfaces: the What's-new page, the highlight viewer and the after-update plate.</summary>
internal static partial class WinUiReleaseNotes
{
    /// <summary>Registers the viewer and the plate. The What's-new PAGE is Classic's (the stand-in fills the key WinUI leaves out).</summary>
    public static void Register(ViewRegistryBuilder views)
    {
        views.Add(ViewKeys.ReleaseNotesViewer, ViewerTemplate, "winui.release-notes.viewer");
        views.Add(ViewKeys.ReleaseNotesAfterUpdatePlate, PlateTemplate, "winui.release-notes.after-update-plate");
    }

    static readonly Action<string> IgnoreLink = static _ => { };
    static readonly Action Inert = static () => { };

    /// <summary>A button at the tier's size: the stock size, or Large (40) where the record asks for a hit target.</summary>
    static BoxEl Btn(string label, Action click, ButtonAppearance appearance, ControlSize size, float target, bool enabled = true)
        => Button.Create(label, click, appearance, target > 0f ? ControlSize.Large : size, isEnabled: enabled);
}
