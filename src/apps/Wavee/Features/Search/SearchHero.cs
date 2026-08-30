using System;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using Wavee.Core;
using Wavee.SpotifyLive;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>All-tab top-result hero. Copy on the left; art is cropped off the right with a slight rotation. The card
/// is a token plate; a CoverColorPlane radial (when the cover is gradeable) washes the art edge — not a solid chrome
/// fill. Play / Open / Save reuse the existing search-row actions — no second play state machine.</summary>
sealed class SearchHero : Component
{
    readonly SearchTopHit _hit;

    public SearchHero(SearchTopHit hit) => _hit = hit;

    public override Element Render()
    {
        var model = UseContext(SearchAllList.Props);
        var lib = UseContext(LibraryBridge.Slot);
        var goOrigin = UseContext(HistoryStore.GoWithOrigin);
        string q = (UseContext(SearchQuery.Slot)?.Peek() ?? "").Trim();
        if (model is null) return new BoxEl();
        var origin = q.Length == 0 ? (NavOrigin?)null : new NavOrigin(q, "search", q);

        var h = _hit;
        string? url = h.Image?.Url;
        if (url is { Length: > 0 } && CoverColorPlane.CanGrade(url))
            _ = CoverColorPlane.Current.Watch(url).Value;
        var scheme = url is { Length: > 0 }
            ? CoverColorPlane.Current.TryGetScheme(url, Tok.Theme == ThemeKind.Light)
            : null;
        ColorF accent = scheme is { } s ? WaveePalette.ChromeAccent(s) : Tok.AccentDefault;

        bool isTrack = h.Kind == SearchHitKind.Track;
        bool canPlay = h.Kind is not (SearchHitKind.User or SearchHitKind.Genre or SearchHitKind.Author);
        bool canOpen = h.Kind is SearchHitKind.Artist or SearchHitKind.Album or SearchHitKind.Playlist
            or SearchHitKind.Podcast or SearchHitKind.Audiobook or SearchHitKind.Genre or SearchHitKind.Episode;
        Action play = isTrack ? () => model.PlayTrack(h.Uri) : () => model.PlayContext(h.Uri);
        Action open = isTrack ? play : SearchAllList.OpenFor(model, h.Kind, h.Uri, h.Name, origin, goOrigin);

        Element? trailing =
            h.Followable ? Embed.Comp(() => new FollowButton(h.Uri, h.Name)) with { Key = "follow:" + h.Uri }
            : isTrack ? SearchAllList.SaveTrailing(lib?.IsSaved(h.Uri) ?? false, () =>
            {
                if (h.Uri.Length > 0) lib?.ToggleSaved(h.Uri, h.Name);
            })
            : null;

        var actions = new System.Collections.Generic.List<Element>(3);
        if (canPlay) actions.Add(WaveeCta.Play(accent, play) with { Shrink = 0f });
        if (canOpen && !isTrack)
            actions.Add(WaveeCta.Pill(Loc.Get(Strings.Search.OpenPage), open, ButtonAppearance.Standard) with { Shrink = 0f });
        if (trailing is not null) actions.Add(trailing);

        var chips = new System.Collections.Generic.List<Element>(3);
        if (h.MatchedTitle) chips.Add(Chip(Loc.Get(Strings.Search.MatchedTitle)));
        if (h.MatchedLyrics) chips.Add(Chip(Loc.Get(Strings.Search.LyricsMatch)));
        if (h.AccessLabel is { Length: > 0 }) chips.Add(Chip(h.AccessLabel));

        const float art = 300f;
        var copy = new BoxEl
        {
            Direction = 1, Grow = 1f, MinWidth = 0f, Gap = Spacing.S, Justify = FlexJustify.End,
            Padding = new Edges4(Spacing.L, Spacing.L, Spacing.XL, Spacing.L),
            Children =
            [
                WaveeType.Eyebrow(Loc.Get(Strings.Search.TopResult) + " · " + h.TypeLabel) with
                {
                    Color = Tok.AccentTextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                },
                WaveeType.PageHero(h.Name) with
                {
                    MinWidth = 0f,
                    MaxLines = 2, Trim = TextTrim.CharacterEllipsis,
                },
                h.Subtitle.Length == 0
                    ? new BoxEl()
                    : RichText.OfRow(h.Subtitle, 12f, Tok.TextSecondary, Tok.AccentTextPrimary, key => model.Go(key, null)),
                chips.Count == 0
                    ? new BoxEl()
                    : new BoxEl { Direction = 0, Gap = Spacing.S, Wrap = true, MinWidth = 0f, Children = chips.ToArray() },
                actions.Count == 0
                    ? new BoxEl()
                    : new BoxEl { Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, Children = actions.ToArray() },
            ],
        };
        var cover = new BoxEl
        {
            Width = art, Height = 260f, Shrink = 0f, AlignSelf = FlexAlign.Center,
            OffsetX = 40f, OffsetY = -16f, Rotation = -2.5f,
            HitTestVisible = false, ClipToBounds = true,
            Corners = CornerRadius4.All(Radii.Card),
            Children = [Surfaces.Artwork(h.Image, h.Uri.GetHashCode() & 0x7fffffff, art, 260f, Radii.Card)],
        };

        return new BoxEl
        {
            Height = 228f, MinWidth = 0f, Grow = 1f, AlignSelf = FlexAlign.Stretch,
            ClipToBounds = true, Corners = Radii.CardAll,
            Fill = Tok.FillCardDefault, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            Role = AutomationRole.Button, OnClick = open, ZStack = true,
            Children =
            [
                scheme is { } graded
                    ? new BoxEl
                    {
                        HitTestVisible = false, AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch,
                        Height = 228f,
                        Gradient = new GradientSpec(GradientShape.Radial, 0f,
                        [
                            new GradientStop(0f, WaveePalette.ChromeAccent(graded) with { A = 0.55f }),
                            new GradientStop(0.58f, WaveePalette.ChromeAccent(graded) with { A = 0f }),
                        ])
                        {
                            RadialCenter = new Point2(0.88f, 0.40f),
                            RadialRadius = new Point2(1.2f, 0.8f),
                        },
                    }
                    : new BoxEl { HitTestVisible = false },
                new BoxEl
                {
                    Direction = 0, MinWidth = 0f, Height = 228f, AlignSelf = FlexAlign.Stretch,
                    Children = [copy, cover],
                },
            ],
        };
    }

    static Element Chip(string text) => new BoxEl
    {
        Shrink = 0f, Padding = new Edges4(Spacing.S, Spacing.XS, Spacing.S, Spacing.XS), Corners = Radii.FullAll,
        Fill = ColorF.Transparent, BorderWidth = 1f, BorderColor = Tok.StrokeControlDefault,
        Children = [WaveeType.Eyebrow(text) with { Color = Tok.TextSecondary }],
    };
}
