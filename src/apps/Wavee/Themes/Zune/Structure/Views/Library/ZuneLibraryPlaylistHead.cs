// ── Themes/Zune/Structure/Views/Library/ZuneLibraryPlaylistHead.cs — the playlists reader's head in Zune's voice (`StyleKeys.LibraryPlaylistHead`) ──
//
// Board Z-Library-Playlists (audit B5a, V20): the 152 cover, then the playlist's name in the 40 light voice as its owner wrote it, the owner and count
// line in the 13 secondary voice, and the verbs as rings with their lowercase words: play (the accent disc), shuffle, open playlist. The core pane
// keeps the demand, the verbs and the table under the head (`Entities\User.Page.Playlists.Master.cs`). Numbers: `Rules\ZuneLibraryRules.cs`.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using Wavee.Theming.Contracts;
using K = Wavee.Themes.Zune.ZuneDiscoveryKit;
using LR = Wavee.Themes.Zune.ZuneLibraryRules;

namespace Wavee.Themes.Zune;

/// <summary>Zune's playlists reader head.</summary>
internal static class ZuneLibraryPlaylistHead
{
    /// <summary>The style Zune registers under <see cref="StyleKeys.LibraryPlaylistHead"/>. Stateless.</summary>
    public static readonly LibraryPlaylistHeadStyle Style = new(Head);

    static Element Head(LibraryPlaylistHeadArgs a)
    {
        var words = new List<Element>(3)
        {
            // A name: never re-cased.
            R.Type.PageHero(a.Name) with
            {
                Color = Tok.TextPrimary, Case = TextCase.AsWritten, MaxLines = 2, Wrap = TextWrap.WrapWholeWords, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
            },
        };
        if (a.Meta.Length > 0)
            words.Add(R.Type.DenseMeta(a.Meta) with { Color = Tok.TextSecondary, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f });
        words.Add(new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = LR.PaneCommandsGap, Shrink = 0f, MinWidth = 0f, Margin = new Edges4(0f, LR.PlaylistVerbsTop - LR.PlaylistTitleGap, 0f, 0f),
            Children =
            [
                K.Ring("lib-playlist-play", Icons.Play, Loc.Get(Strings.Home.Play), showLabel: true, primary: true, a.Play),
                K.Ring("lib-playlist-shuffle", Icons.Shuffle, Loc.Get(Strings.Detail.Shuffle), showLabel: true, primary: false, a.Shuffle),
                K.Ring("lib-playlist-open", Icons.ChevronRight, Loc.Get(Strings.Library.OpenPlaylist), showLabel: true, primary: false, a.Open),
            ],
        });
        return new BoxEl
        {
            Key = "lib-playlist-head", Direction = 0, Gap = LR.PlaylistHeadGap, AlignItems = FlexAlign.Center, Shrink = 0f, MinWidth = 0f,
            Padding = new Edges4(0f, 0f, 0f, LR.PlaylistHeadBottom),
            Children =
            [
                a.CoverAt(LR.PlaylistHeadCover),
                new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = LR.PlaylistTitleGap, Children = words.ToArray() },
            ],
        };
    }
}
