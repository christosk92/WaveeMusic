// ── Themes/Classic/Structure/Views/Video/ClassicVideoPanelBody.cs — Classic's video body of the right panel (themes plan 6.1, T7c) ──
//
// From `Shell/Rail.UI.cs` (`VideoBody` and `NoVideoPlaceholder`): the panel's Video mode, everything BELOW the pinned docked card: the
// track's title and its artists + album line, the hairline, the "Up next" eyebrow over the shared "Next up" rows (the `Panel.NextUp` view,
// which the now-playing body places too) or the empty queue's vacancy, scrolling under an edge fade. At Huge (C-Video-Huge,
// `ClassicPanelContentLayout.VideoTitleActions`) the title row carries the heart and the playing track's "..." beside the title and meta
// column; the row is always that column, so the buttons fade in beside it on a tier crossing without moving the rows under it.
//
// B10: the card upstream collapses; the rail does NOT close. Never flash "no video" while Video is unknown: the placeholder shows only
// once the track KNOWS it has none. The placeholder is the card's poster composition minus the spinner, since the answer is already known,
// in ON-MEDIA ink, not `TextOnAccentPrimary` (black in dark theme). The 16:9 sits on a PLAIN column box and the layers on a ZStack inside it
// (G-256): a ZStack's measure ignores `AspectRatio` and reports its tallest layer (here the square cover), so the placeholder rendered square.
//
// A component, as the core's was: it subscribes the playing track, the scope epoch, the queue edge and the album table (the core reads them
// through the props), so an edge re-renders this body and not the panel around it.

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's video mode body of the right panel.</summary>
internal sealed class ClassicVideoPanelBody : Component
{
    public override Element Render()
    {
        var props = UseProps<PanelProps>();
        var overlay = UseContext(Overlay.Service);
        var facts = UseContext(ThemeFactsContext.Signal);
        var tier = ClassicStageTiers.PanelOf(facts is null ? TierFacts.Unknown : facts.Value.Tiers);
        var now = props.NowPlaying;
        var track = now.NowTrack();
        props.SubscribeVideoBody();
        var content = new List<Element>(8);
        if (track.IsValid && track.Knows(TrackFields.Video) && !track.HasVideo) content.Add(NoVideoPlaceholder(track));
        if (track.IsValid)
        {
            // the title and meta column (the body's own gap between them), with the Huge row's heart and "..." beside it
            var titleColumn = new List<Element>(2) { R.Type.NowPlayingTitle(track.Title) with { MaxLines = 2, Wrap = TextWrap.Wrap, Trim = TextTrim.CharacterEllipsis } };
            string artists = now.ResolveString(track.ArtistLineId), album = track.Album.IsValid ? track.Album.Title : "";
            string meta = artists.Length == 0 ? album : album.Length == 0 ? artists : artists + " · " + album;
            if (meta.Length > 0) titleColumn.Add(R.Type.TrackMeta(meta) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis });
            var row = new List<Element>(3) { new BoxEl { Direction = 1, Gap = Spacing.S, Grow = 1f, Basis = 0f, MinWidth = 0f, Children = titleColumn.ToArray() } };
            if (tier.VideoTitleActions && props.VideoTitle is { } actions)
                row.Add(new BoxEl
                {
                    Key = "video:title-actions", Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0f,
                    Enter = s_fade, Exit = s_fade, Transition = MotionTok.ControlNormal,
                    Children = [Embed.Comp(new HeartProps(actions), static () => new TitleHeart()), More(actions, overlay)],
                });
            content.Add(new BoxEl { Key = "video:title", Direction = 0, AlignItems = FlexAlign.Center, Gap = ClassicStageTiers.VideoTitleGap, MinWidth = 0f, Children = row.ToArray() });
        }
        content.Add(new BoxEl { Height = 1f, Shrink = 0f, Fill = Tok.StrokeCardDefault });
        content.Add(R.Type.Eyebrow(Loc.Get(Strings.Player.VideoUpNext)) with { Color = Tok.TextTertiary });
        content.Add(now.HasNextUp()
            ? Slots.View(ViewKeys.PanelNextUp, now.NextUp)
            : Controls.Vacancy(Controls.VacancyVoice.Empty, Controls.VacancyScale.Compact, title: Loc.Get(Strings.Player.QueueEmpty), subtitle: ""));
        return ScrollView(new BoxEl
        {
            Direction = 1, Gap = Spacing.S, Padding = new Edges4(Spacing.M, Spacing.S, Spacing.M, Spacing.M),
            Children = content.ToArray(),
        }) with { Grow = 1f, MinHeight = 0f, AutoEdgeFade = true };
    }

    static readonly EnterExit s_fade = new(Opacity: 0f, Active: true);

    /// <summary>The playing track's "..." (C-Video-Huge's "Video options"): the menu the core hands over, asked at open time.</summary>
    static Element More(PanelVideoTitleProps actions, IOverlayService overlay)
        => ToolTip.Wrap((TitleGlyph(Icons.More, Tok.TextSecondary, null) with { ClickRequestsContext = true }).WithContextMenu(overlay, actions.Menu), Loc.Get(Strings.Common.More));

    /// <summary>A 32 x 32 subtle glyph button of the title row.</summary>
    static BoxEl TitleGlyph(string glyph, ColorF ink, Action? onClick) => new()
    {
        Width = ClassicStageTiers.VideoTitleButton, Height = ClassicStageTiers.VideoTitleButton, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Corners = Radii.ControlAll, Fill = Tok.FillSubtleTransparent, HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
        BrushTransitionMs = Design.Motion.Faster, Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, Cursor = CursorId.Hand,
        OnClick = onClick,
        Children = [new TextEl(glyph) { Size = ClassicVideoRules.TitleGlyph, FontFamily = Tok.Definition.Fonts.Icon, Color = ink }],
    };

    sealed record HeartProps(PanelVideoTitleProps Actions);

    /// <summary>The heart: its own component, so a save re-renders the heart and not the body.</summary>
    sealed class TitleHeart : Component
    {
        public override Element Render()
        {
            var f = UseProps<HeartProps>().Actions.Facts();
            return ToolTip.Wrap(TitleGlyph(f.Saved ? Icons.HeartFill : Icons.Heart, f.Saved ? Tok.AccentTextPrimary : Tok.TextSecondary, f.Like) with
            {
                IsEnabled = f.Like is not null, Key = f.Saved ? "video:heart:on" : "video:heart:off",
            }, Loc.Get(Strings.Menu.SaveToLiked));
        }
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
