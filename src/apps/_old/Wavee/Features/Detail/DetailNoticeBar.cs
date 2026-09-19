using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;

namespace Wavee;

/// <summary>
/// The detail page's NOTICE strip — any kind, not just playlists: one Informational bar between the header and the
/// list saying what the reader needs to know about the thing they are looking at (a playlist was deleted, access was
/// revoked, the create failed — or an album's rows are still the minified gid-only view).
/// <para>Informational, not Error, on purpose: nothing the user did failed — the world changed under them (or has not
/// caught up yet). And the page keeps its content: a noticed playlist loses its edit affordances
/// (<c>PlaylistInlineEdit.Editable</c> reads the same <see cref="DetailModel.Notice"/>) but the rows they were reading
/// stay on screen instead of collapsing into an error page that says less.</para>
/// <para>A Component (not a static builder) so it re-renders off the LIVE loadable: the strip must appear the frame the
/// tombstone push (or a thin album projection) lands and disappear the frame the model comes back whole, without the
/// shell re-rendering.</para>
/// </summary>
sealed class DetailNoticeBar : Component
{
    readonly Loadable<DetailModel> _full;
    // Frozen at mount — which surface is asking, not a fact about the data (that stays on DetailModel.Notice). The
    // MinifiedAlbum verdict is real for BOTH mount sites (the full model's own Notice, per PlaylistPageNoticeRules.
    // ForAlbum), but only the embedded library pane can USE it: it never self-heals (LibraryDetailPane mounts no
    // trailing band, so nothing ever asks for Full), so the notice is the only way it tells the reader where the real
    // rows live. The full album page (DetailShell) DOES self-heal — its trailing band asks for Full the moment it
    // mounts — so showing the strip there is not "the truth a moment early", it is a bar that appears for ~0.4s while
    // the meta line's duration is still summing zero-duration thin rows (see DetailPage.MapAlbum) and then vanishes
    // the instant the repair lands, taking ~94px of hero with it. Suppressing it here (view policy) rather than never
    // computing the fact (DetailPage.MapAlbum keeps stamping it) keeps the library pane's own mount site — which
    // passes no second argument and keeps today's behaviour — untouched.
    readonly bool _showMinifiedAlbum;

    public DetailNoticeBar(Loadable<DetailModel> full, bool showMinifiedAlbum) { _full = full; _showMinifiedAlbum = showMinifiedAlbum; }

    /// <summary>Mount the strip for a page. Zero-height (and mounts no bar) while there is nothing to say.
    /// <paramref name="showMinifiedAlbum"/>: true (default) for the embedded library pane, which never self-heals a
    /// thin album and needs the strip to say where the real rows are; false for the full detail page, which does.</summary>
    internal static Element For(Loadable<DetailModel> full, bool showMinifiedAlbum = true)
        => Embed.Comp(() => new DetailNoticeBar(full, showMinifiedAlbum)) with { Key = "detail-notice" };

    public override Element Render()
    {
        var go = UseContext(HistoryStore.NavCtx);
        var notice = _full.Value.Value.Notice;      // subscribe → appears/clears in place
        if (notice == DetailNotice.None) return new BoxEl { Height = 0f, HitTestVisible = false };

        // MinifiedAlbum is its own shape: not terminal, and not this strip's job to fix — it says WHERE the details
        // load (the full album page) and nothing more. The way there is the surface's own chrome (the library pane's
        // "View full album" button, the full page's hero), never an action on the notice: the bar is a statement about
        // the data, and the full page heals itself the moment its trailing band asks for Full.
        if (notice == DetailNotice.MinifiedAlbum)
            return _showMinifiedAlbum
                ? new BoxEl
                {
                    Direction = 1, Padding = new Edges4(16f, 8f, 16f, 4f), Shrink = 0f,
                    Children =
                    [
                        InfoBar.Create(InfoBarSeverity.Informational, Loc.Get(Strings.Detail.Notice.MinifiedAlbum), "", isClosable: false),
                    ],
                }
                : new BoxEl { Height = 0f, HitTestVisible = false };

        string message = notice switch
        {
            DetailNotice.Deleted => Loc.Get(Strings.Detail.Notice.Deleted),
            DetailNotice.AccessRevoked => Loc.Get(Strings.Detail.Notice.AccessRevoked),
            _ => Loc.Get(Strings.Detail.Notice.CreateFailed),
        };

        // The one action is a way OUT, not a retry: every one of these playlist states is terminal for this page, and
        // the only useful next move is back to something that still exists. "albums" is Your Library's landing section
        // (there is no bare "library" destination — the library is a set of kind sections, and this is the first of them).
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
