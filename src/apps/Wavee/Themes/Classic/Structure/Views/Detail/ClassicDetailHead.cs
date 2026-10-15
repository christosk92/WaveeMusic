// ── Themes/Classic/Structure/Views/Detail/ClassicDetailHead.cs — the head of a detail page with no rail (tuned Classic D16) ──────────────
//
// Liked Songs has no rail (board C-Liked): its wide arm is a 156-DIP head over the list. The head is the page title (the 40/52 display
// face, `R.Type.SurfaceDisplay`) and the meta line ("1,284 songs · 71 hr 3 min", or "3 of 48 songs · 11 min (filtered)" while the
// list's find or filter narrows it: the rail's count line, D4); the 44-DIP bar under it is the TABLE's list bar in its page form
// (Play split, Shuffle, the genre chips, the view commands), so it shares the list's state with no second copy. The bar's 8-DIP top air
// is the chrome's own, so the head stops 4 short of the board's 12.
//
// The numbers are `Rules\DetailLayout.cs` (`ClassicDetailLayout.Head*`).

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

internal static partial class ClassicDetailFrame
{
    /// <summary>The page head: the title and, 4 below it, the meta line, in the page gutter.</summary>
    static Element PageHead(DetailHeadProps head)
    {
        var id = head.Spec.Identity;
        Element meta = head.Shown is { } shown
            ? Embed.Comp(new RailMetaProps(id, float.NaN), () => new RailMetaLine(shown)) with { Key = "head:meta" }
            : MetaLine(id, float.NaN, maxWidth: float.NaN, maxLines: 1) with { Key = "head:meta" };
        return new BoxEl
        {
            Key = "detail:head-title", Direction = 1, Shrink = 0f, MinWidth = 0f,
            Padding = new Edges4(ClassicDetailLayout.HeadGutter, ClassicDetailLayout.HeadPadTop, ClassicDetailLayout.HeadGutter,
                                 ClassicDetailLayout.HeadPadBottom),
            Children =
            [
                // Fixed line boxes (the board's 52 and 16): the face's natural box is taller than its line, and the head is 156 whatever
                // the text measures.
                new BoxEl
                {
                    Direction = 1, MinWidth = 0f, Height = ClassicDetailLayout.HeadTitleLine,
                    Children = [R.Type.SurfaceDisplay(id.Title) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f }],
                },
                new BoxEl
                {
                    Direction = 1, MinWidth = 0f, Height = ClassicDetailLayout.HeadMetaLine,
                    Margin = new Edges4(0f, ClassicDetailLayout.HeadMetaGap, 0f, 0f),
                    Children = [meta],
                },
            ],
        };
    }
}
