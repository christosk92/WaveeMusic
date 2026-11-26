// ── Themes/WinUi/Structure/Views/Detail/WinUiMixLesson.cs — WinUI's Mix lesson page, "Build a transition together" (#48 Mix) ───────────
//
// WinUI's voice for the lesson (Themes/Common/CommonMixLesson.cs): the Edit transition card (WinUI's own editor template in its teaching
// mode) on the page's gutter, the guide beside it in stock Fluent controls (Button and AccentButton, SegmentedControl, InfoBar, the stepper's
// round nodes, the task list as a card of list rows with the selection pill); below Standard the guide scrolls under the editor.

using FluentGpu.Dsl;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Themes.Common;
using E = Wavee.Themes.WinUi.WinUiMixEditorRules;

namespace Wavee.Themes.WinUi;

internal static class WinUiMixLesson
{
    /// <summary>The registered template of <see cref="ViewKeys.MixLesson"/>.</summary>
    public static Element Template(MixLessonProps p, in ViewContext ctx)
    {
        var tier = ctx.Tier();
        return CommonMixLesson.Page(p, CommonMixLessonFluent.Voice(E.Lesson(tier.Window)), E.LessonStacked(tier.Content), in ctx);
    }
}
