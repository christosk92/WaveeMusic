// ── Themes/Classic/Structure/Views/Video/ClassicVideoPanelBody.cs — Classic's video body of the right panel (themes plan 6.1, T7c) ──
//
// From `Shell/Rail.UI.cs` (`VideoBody` and `NoVideoPlaceholder`): the panel's Video mode, everything BELOW the pinned docked card: the
// track's title and its artists + album line, the hairline, the "Up next" eyebrow over the shared "Next up" rows (the track-row family's,
// which the now-playing body places too) or the empty queue's vacancy, scrolling under an edge fade.
//
// B10: the card upstream collapses; the rail does NOT close. Never flash "no video" while Video is unknown: the placeholder shows only
// once the track KNOWS it has none. The placeholder is the card's poster composition minus the spinner, since the answer is already known,
// in ON-MEDIA ink, not `TextOnAccentPrimary` (black in dark theme). The 16:9 sits on a PLAIN column box and the layers on a ZStack inside it
// (G-256): a ZStack's measure ignores `AspectRatio` and reports its tallest layer (here the square cover), so the placeholder rendered square.
//
// A component, as the core's was: it subscribes the playing track, the scope epoch, the queue edge and the album table (the core reads them
// through the props), so an edge re-renders this body and not the panel around it.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's video mode body of the right panel.</summary>
internal sealed class ClassicVideoPanelBody : Component
{
    public override Element Render()
    {
        var props = UseProps<PanelProps>();
        var now = props.NowPlaying;
        var track = now.NowTrack();
        props.SubscribeVideoBody();
        var content = new List<Element>(8);
        if (track.IsValid && track.Knows(TrackFields.Video) && !track.HasVideo) content.Add(NoVideoPlaceholder(track));
        if (track.IsValid)
        {
            content.Add(R.Type.NowPlayingTitle(track.Title) with { MaxLines = 2, Wrap = TextWrap.Wrap, Trim = TextTrim.CharacterEllipsis });
            string artists = now.ResolveString(track.ArtistLineId), album = track.Album.IsValid ? track.Album.Title : "";
            string meta = artists.Length == 0 ? album : album.Length == 0 ? artists : artists + " · " + album;
            if (meta.Length > 0) content.Add(R.Type.TrackMeta(meta) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis });
        }
        content.Add(new BoxEl { Height = 1f, Shrink = 0f, Fill = Tok.StrokeCardDefault });
        content.Add(R.Type.Eyebrow(Loc.Get(Strings.Player.VideoUpNext)) with { Color = Tok.TextTertiary });
        content.Add(now.HasNextUp()
            ? now.NextUp()
            : Controls.Vacancy(Controls.VacancyVoice.Empty, Controls.VacancyScale.Compact, title: Loc.Get(Strings.Player.QueueEmpty), subtitle: ""));
        return ScrollView(new BoxEl
        {
            Direction = 1, Gap = Spacing.S, Padding = new Edges4(Spacing.M, Spacing.S, Spacing.M, Spacing.M),
            Children = content.ToArray(),
        }) with { Grow = 1f, MinHeight = 0f, AutoEdgeFade = true };
    }

    static Element NoVideoPlaceholder(Track track) => new BoxEl
    {
        Shrink = 0f, AspectRatio = ClassicVideoRules.NoVideoAspect, Direction = 1, ClipToBounds = true, Corners = Radii.CardAll, Fill = Tok.MediaLetterbox,
        Children =
        [
            new BoxEl
            {
                // Basis 0: the layers' own measure never becomes the height; the ratio's height is handed out by Grow.
                ZStack = true, Grow = 1f, Basis = 0f, MinHeight = 0f, ClipToBounds = true,
                Children =
                [
                    new BoxEl { Grow = 1f, Opacity = ClassicVideoRules.PosterOpacity, ClipToBounds = true, Children = [Controls.ArtworkFill(Controls.ArtUrl(track.ImageId), 0f)] },
                    new BoxEl
                    {
                        Grow = 1f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Padding = Edges4.All(Spacing.S),
                        Children = [new TextEl(Loc.Get(Strings.Player.NoVideoForThisSong)) { Size = ClassicVideoRules.NoVideoText, Weight = 600, Color = Tok.OnMediaPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f }],
                    },
                ],
            },
        ],
    };
}
