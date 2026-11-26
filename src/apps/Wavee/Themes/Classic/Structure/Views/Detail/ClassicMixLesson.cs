// ── Themes/Classic/Structure/Views/Detail/ClassicMixLesson.cs — Classic's Mix lesson page, "Build a transition together" (#48 Mix) ───────
//
// Classic speaks the lesson in the Fluent family's voice (Themes/Common/CommonMixLessonFluent.cs) with its own plates and radii: the Edit
// transition card is Classic's own editor template in its teaching mode (its rail-header title, its control layer), on Classic's page pad.

using FluentGpu.Dsl;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Themes.Common;
using E = Wavee.Themes.Classic.ClassicMixEditorRules;

namespace Wavee.Themes.Classic;

internal static class ClassicMixLesson
{
    /// <summary>The registered template of <see cref="ViewKeys.MixLesson"/>.</summary>
    public static Element Template(MixLessonProps p, in ViewContext ctx)
    {
        var tier = ctx.Tier();
        return CommonMixLesson.Page(p, CommonMixLessonFluent.Voice(E.Lesson(tier.Window)), E.LessonStacked(tier.Content), in ctx);
    }
}
