// ── Themes/Zune/Structure/Views/Detail/ZuneDetailSeam.cs — Zune's notice strip and the vertical arm's trailing body (P5 Z6a1) ───────────────────
//
// The notice is about the PAGE (a deleted or revoked playlist, a playlist that failed to create, a minified album), read before the content it
// qualifies: one informational bar (the engine's InfoBar in Zune's control style) on the frame's gutter, its one action a way OUT ("go to
// library"), never a retry; no notice is a zero-height box. The vertical arm's trailing body is the prerelease card, the release facts and the
// album's trailing band, in that order, under the rows (the vertical arm has no identity column to hold them).

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using K = Wavee.Themes.Zune.ZuneDetailKit;
using Z = Wavee.Themes.Zune.ZuneDetailRules;

namespace Wavee.Themes.Zune;

/// <summary>Zune's notice strip (<see cref="ViewKeys.DetailNotice"/>) and vertical trailing body (<see cref="ViewKeys.DetailVerticalTrailing"/>).</summary>
internal static class ZuneDetailSeam
{
    /// <summary>The registered template of <see cref="ViewKeys.DetailNotice"/>.</summary>
    public static Element NoticeTemplate(DetailNoticeProps p, in ViewContext ctx)
    {
        var notice = p.Notice;
        if (notice == DetailNotice.None || (notice == DetailNotice.MinifiedAlbum && !p.ShowMinifiedAlbum))
            return new BoxEl { Key = Z.NoticeKey, Height = 0f, HitTestVisible = false };
        float gutter = ZuneTiers.FrameOf(ctx.Tier()).Gutter;
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
            Key = Z.NoticeKey, Direction = 1, Shrink = 0f,
            Padding = new Edges4(gutter, Z.NoticePadTop, gutter, Z.NoticePadBottom),
            Children = [InfoBar.Create(InfoBarSeverity.Informational, message, "", isClosable: false, actionButton: action)],
        };
    }

    /// <summary>The registered template of <see cref="ViewKeys.DetailVerticalTrailing"/>: the prerelease card (it can arrive late), the release facts
    /// on the gutter, then the trailing band.</summary>
    public static Element VerticalTrailingTemplate(DetailVerticalTrailingProps p, in ViewContext ctx)
    {
        var kids = new List<Element>(3);
        if (p.PreRelease is { } pre) kids.Add(K.LateRow("vtrail:prerelease", pre));
        if (p.Release is { } release) kids.Add(K.Row("vtrail:release", release));
        if (p.Trailing is { } trailing) kids.Add(K.Row("vtrail:trailing", trailing));
        return new BoxEl { Direction = 1, MinWidth = 0f, Children = kids.ToArray() };
    }
}
