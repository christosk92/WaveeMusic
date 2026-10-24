// ── Themes/Classic/Structure/Views/Detail/ClassicDetailSeam.cs — Classic's notice strip and the vertical arm's trailing body (themes plan 6.1, T9b) ──
//
// Two leaves of the detail frame that only the album and the playlist fill, left in `Entities\Detail.UI.cs` by T9a:
//
//   · the NOTICE strip (`Detail.NoticeBar`): W15's "bad news is a strip, never an error page" — one Informational bar, zero-height (and hit-test free) while
//     there is nothing to say. The page's verdicts (`DetailNotice`, `Detail.NoticeRules`) are core; the bar is the picture.
//   · the vertical arm's TRAILING body (`Detail.FrameHost.VerticalTrailing`): the prerelease card and About-this-release move BELOW the rows because the hero
//     system has no rail (they are not dropped), then the album trailing band, each in the rail's keyed rows.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

/// <summary>Classic's notice strip and the vertical arm's trailing body.</summary>
internal static class ClassicDetailSeam
{
    /// <summary>The registered template of <see cref="ViewKeys.DetailNotice"/>: one Informational bar. The one action is a way OUT, never a retry; there
    /// is no bare "library" destination (the albums section), and the minified verdict carries no action.</summary>
    public static Element NoticeTemplate(DetailNoticeProps p, in ViewContext ctx)
    {
        var notice = p.Notice;
        if (notice == DetailNotice.None || (notice == DetailNotice.MinifiedAlbum && !p.ShowMinifiedAlbum))
            return new BoxEl { Key = "detail-notice", Height = 0f, HitTestVisible = false };

        string message = notice switch
        {
            DetailNotice.Deleted => Loc.Get(Strings.Detail.Notice.Deleted),
            DetailNotice.AccessRevoked => Loc.Get(Strings.Detail.Notice.AccessRevoked),
            DetailNotice.MinifiedAlbum => Loc.Get(Strings.Detail.Notice.MinifiedAlbum),
            _ => Loc.Get(Strings.Detail.Notice.CreateFailed),
        };
        Element? action = notice == DetailNotice.MinifiedAlbum || p.GoLibrary is null
            ? null
            : Button.Create(Loc.Get(Strings.Detail.Notice.GoToLibrary), p.GoLibrary, ButtonAppearance.Subtle, ControlSize.Small);
        return new BoxEl
        {
            Key = "detail-notice",
            Direction = 1,
            Padding = new Edges4(ClassicDetailKitLayout.NoticePadX, ClassicDetailKitLayout.NoticePadTop, ClassicDetailKitLayout.NoticePadX,
                ClassicDetailKitLayout.NoticePadBottom),
            Shrink = 0f,
            Children = [InfoBar.Create(InfoBarSeverity.Informational, message, "", isClosable: false, actionButton: action)],
        };
    }

    /// <summary>The registered template of <see cref="ViewKeys.DetailVerticalTrailing"/>: the prerelease card (it can arrive late), About this release, and
    /// the album trailing band.</summary>
    public static Element VerticalTrailingTemplate(DetailVerticalTrailingProps p, in ViewContext ctx)
    {
        var kids = new List<Element>(3);
        if (p.PreRelease is { } pre) kids.Add(ClassicDetailKit.LateRow("vtrail:prerelease", pre));
        if (p.Release is { } release) kids.Add(ClassicDetailKit.Row("vtrail:release", release));
        if (p.Trailing is { } trailing) kids.Add(ClassicDetailKit.Row("vtrail:trailing", trailing));
        return new BoxEl { Direction = 1, Children = kids.ToArray() };
    }
}
