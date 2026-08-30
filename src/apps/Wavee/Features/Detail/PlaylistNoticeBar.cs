using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;

namespace Wavee;

/// <summary>
/// The playlist page's NOTICE strip: one Informational bar between the header and the list saying what happened to the
/// thing the user is looking at (it was deleted, access was revoked, the create failed).
/// <para>Informational, not Error, on purpose: nothing the user did failed — the world changed under them. And the page
/// keeps its content: the edit affordances are gone (<c>PlaylistInlineEdit.Editable</c> reads the same
/// <see cref="DetailModel.Notice"/>), but the rows they were reading stay on screen instead of collapsing into an
/// error page that says less.</para>
/// <para>A Component (not a static builder) so it re-renders off the LIVE loadable: the strip must appear the frame the
/// tombstone push lands and disappear the frame the playlist comes back, without the shell re-rendering.</para>
/// </summary>
sealed class PlaylistNoticeBar : Component
{
    readonly Loadable<DetailModel> _full;

    public PlaylistNoticeBar(Loadable<DetailModel> full) => _full = full;

    /// <summary>Mount the strip for a page. Zero-height (and mounts no bar) while there is nothing to say.</summary>
    internal static Element For(Loadable<DetailModel> full)
        => Embed.Comp(() => new PlaylistNoticeBar(full)) with { Key = "pl-notice" };

    public override Element Render()
    {
        var go = UseContext(HistoryStore.NavCtx);
        var notice = _full.Value.Value.Notice;      // subscribe → appears/clears in place
        if (notice == DetailNotice.None) return new BoxEl { Height = 0f, HitTestVisible = false };

        string message = notice switch
        {
            DetailNotice.Deleted => Loc.Get(Strings.Detail.Notice.Deleted),
            DetailNotice.AccessRevoked => Loc.Get(Strings.Detail.Notice.AccessRevoked),
            _ => Loc.Get(Strings.Detail.Notice.CreateFailed),
        };

        // The one action is a way OUT, not a retry: every one of these states is terminal for this page, and the only
        // useful next move is back to something that still exists. "albums" is Your Library's landing section (there is
        // no bare "library" destination — the library is a set of kind sections, and this is the first of them).
        Element? action = go is null ? null : Button.Create(
            Loc.Get(Strings.Detail.Notice.GoToLibrary), () => go("albums", null), ButtonAppearance.Subtle, ControlSize.Small);

        return new BoxEl
        {
            Direction = 1, Padding = new Edges4(16f, 8f, 16f, 4f), Shrink = 0f,
            Children =
            [
                InfoBar.Create(InfoBarSeverity.Informational, message, "", isClosable: false, actionButton: action),
            ],
        };
    }
}
