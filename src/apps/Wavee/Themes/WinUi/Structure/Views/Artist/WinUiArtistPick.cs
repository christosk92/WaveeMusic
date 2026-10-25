// ── Themes/WinUi/Structure/Views/Pages/WinUiArtistPick.cs — WinUI's artist pick card (themes plan 6.1, T10b1) ──────────────
//
// From `Entities\Artist.UI.cs` §4.2 (0.2.9 MediaCard.ArtistPick, MediaCard.cs:299-471): the artist-authored pinned item as ONE tone panel. What it opens, plays and
// puts in its menu is `ArtistVerbs`; its clickable shell is the structure's surface shell (T8c `SurfaceStyle.Shell`).

using FluentGpu;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.WinUi;

/// <summary>Wavee WinUI's artist pick card.</summary>
internal static class WinUiArtistPick
{
    /// <summary>The photo ImageEl's decode HEIGHT, derived EXACTLY as the reconciler derives it for a width-less aspect
    /// image (<c>Reconciler.ImageDecodeTarget</c>: <c>round(decodePx / aspect)</c>) — so <see cref="PickPhoto"/>'s
    /// <c>UseImage</c> observes the very cache handle the mounted image pins (key = (src, 640, 400)), never a second
    /// decode.</summary>
    static readonly int s_photoDecodeH = (int)MathF.Round(WinUiArtistRules.PickPhotoDecodeW / WinUiArtistRules.PickPhotoAspect);

    /// <summary>The artist-authored pinned item as ONE tone panel: accent wash, the artist speaking, an optional wide
    /// photograph (the pick's own campaign art, else the artist's header — never a blurred cover stand-in), then the
    /// record as a footer row with Play, or Pre-save while it is not out. ONE record per panel (ch 08 §9 #9). The caller
    /// keys it on the rail/band arm (props freeze at mount).
    /// <para>The photograph is EARNED, never reserved: <see cref="PickPhoto"/> requests the art while the band is
    /// collapsed to nothing, and the band only mounts once the image is Ready — growing open (height on the rail arm,
    /// width on the band arm) and fading in. While pending the card is just header + comment + footer, and on a real
    /// failure it stays that way: no flat placeholder block ever stands in for a photo that may never come (the grey
    /// slab a stuck decode used to leave behind).</para>
    /// <para><paramref name="photo"/> false: the compact card (head, quote, footer) with no photograph band at all (the overview column).</para></summary>
    public static Element Card(Artist a, Func<ColorF> accent, bool horizontal, IOverlayService? overlay, SurfaceStyle? surface, float buttonTarget = WinUiDetailRules.Target,
                               bool photo = true)
    {
        if (surface is null) return new BoxEl();   // no registry: the page logged it (T8c: the panel's clickable shell is the structure's)
        ColorF tint = accent();
        ref readonly ArtistPick pick = ref a.Pick;
        string title = ArtistVerbs.Text(pick.Title);
        string kind = ArtistVerbs.Text(pick.Subtitle);
        string comment = ArtistVerbs.Text(pick.Comment.IsEmpty ? pick.Eyebrow : pick.Comment);
        string item = ArtistVerbs.Text(pick.ItemUri);
        string target = item.Length > 0 ? item : ArtistVerbs.Text(pick.Uri);
        string? cover = Controls.ArtUrl(pick.Cover);
        string? background = Controls.ArtUrl(pick.Background.IsEmpty ? a.HeaderId : pick.Background);
        int releaseAt = pick.ReleaseAt;
        string artistName = a.Name;
        string? avatar = Controls.ArtUrl(a.ImageId);
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var targetUri = EntityUri.Parse(target);
        Action open = () => ArtistVerbs.OpenEntity(targetUri, title);
        Action play = () => ArtistVerbs.PlayOrToggle(target);

        Element wash = new BoxEl
        {
            Grow = 1f, HitTestVisible = false,
            Gradient = GradientDown(
                new GradientStop(0f, tint with { A = 0.16f }),
                new GradientStop(0.55f, tint with { A = 0.05f }),
                new GradientStop(0.85f, tint with { A = 0f })),
        };
        Element head = new BoxEl
        {
            Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Center,
            Padding = new Edges4(Spacing.L, Spacing.L, Spacing.L, 0f),
            Children =
            [
                // ch 08 BUG F: PersonPicture.Create's `imageSourcePath` arm paints its photo over a FROZEN `fill`
                // ColorF (no watch subscription — the flat grey the pick avatar never repainted from once the
                // palette grading landed). Controls.Artwork stacks Shimmer's Design.WatchedPlaceholder tile under
                // the real photo instead — 32 DIP is below Design.ShimmerMinEdge (80f), so it takes the cheap
                // static-but-LIVE-bound arm, not the breathing pulse. The no-photo arm keeps PersonPicture.Create
                // (its initials/glyph fallback has no art to watch).
                avatar is { Length: > 0 }
                    ? new BoxEl
                      {
                          Width = WinUiArtistRules.PickAvatar, Height = WinUiArtistRules.PickAvatar, Shrink = 0f, ZStack = true, ClipToBounds = true,
                          Corners = Radii.Circle(WinUiArtistRules.PickAvatar), BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
                          Children = [Controls.Artwork(avatar, WinUiArtistRules.PickAvatar, WinUiArtistRules.PickAvatar, WinUiArtistRules.PickAvatar / 2f, decodePx: 64)],
                      }
                    : PersonPicture.Create("", WinUiArtistRules.PickAvatar, displayName: artistName) with { BorderColor = Tok.StrokeCardDefault, Shrink = 0f },
                new BoxEl
                {
                    Direction = 1, MinWidth = 0f, Grow = 1f, Basis = 0f,
                    Children =
                    [
                        BodyStrong(artistName) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                        Caption(Loc.Get(Strings.Artist.ArtistPick)) with { Color = Tok.TextSecondary, MaxLines = 1 },
                    ],
                },
            ],
        };
        Element quote = new BoxEl
        {
            Padding = new Edges4(Spacing.L, Spacing.S, Spacing.L, Spacing.L),
            Grow = horizontal ? 1f : 0f,
            Children =
            [
                R.Type.PickQuote(comment) with
                {
                    Wrap = TextWrap.Wrap, MaxLines = 4, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                },
            ],
        };
        // Props freeze at mount: the photo embed is keyed on the url it waits for (the arm is already the card's key).
        Element? band = photo && background is { Length: > 0 } bgUrl
            ? Embed.Comp(() => new PickPhoto { Url = bgUrl, Band = horizontal }) with
              {
                  Key = "pick-photo:" + bgUrl,
                  // The skeleton deriver can't see into a component: hand it the band's real shape (an image block), or a
                  // loading page shows one default bar where the photo will be.
                  SkeletonProxy = () => PickPhoto.Shape(bgUrl, horizontal),
              }
            : null;

        // Props freeze at mount: the pre-save embed is keyed on the uri it would pre-save.
        Element trailing = releaseAt > now
            ? Embed.Comp(() => new Controls.PreSaveButton { Uri = target, Name = title, Accent = accent })
                with { Key = "pick-presave:" + target }
            : Controls.PlayButton(tint, play, buttonTarget > WinUiDetailRules.Target ? ControlSize.Large : ControlSize.Medium);
        Element metaKind = R.Type.TrackMeta(kind) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f };
        TextEl titleText = R.Type.TrackTitle(title) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f };
        Element foot = new BoxEl
        {
            Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Center,
            Padding = new Edges4(Spacing.L, Spacing.M, Spacing.L, Spacing.M),
            Children =
            [
                new BoxEl { Shrink = 0f, Children = [Controls.Artwork(cover, WinUiArtistRules.PickCover, WinUiArtistRules.PickCover, Radii.Control, decodePx: 96)] },
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = Spacing.XXS,
                    Children =
                    [
                        Controls.TrimmedTitle(titleText, title),   // under the footer column's width, never a shrink-wrapped run
                        new BoxEl
                        {
                            Direction = 0, Gap = Spacing.XS, AlignItems = FlexAlign.Center, MinWidth = 0f,
                            Children = releaseAt > 0
                                ? [metaKind, Caption(Strings.Detail.ReleasesOn(Track.Format.ShortDate(releaseAt, now)))
                                    with { Color = tint, Weight = 600, MaxLines = 1 }]
                                : [metaKind],
                        },
                    ],
                },
                new BoxEl { Shrink = 0f, Children = [trailing] }.Skeletonized(false),
            ],
        };

        Element copy = new BoxEl
        {
            Direction = 1,
            Grow = horizontal ? 1f : 0f, Basis = horizontal ? 0f : float.NaN, MinWidth = horizontal ? 0f : float.NaN,
            Children = !horizontal && band is not null ? [head, quote, band.Value, foot] : [head, quote, foot],
        };
        Element content = horizontal && band is not null ? new BoxEl { Direction = 0, Children = [copy, band.Value] } : copy;

        // The panel keeps its own tree (a quote over a footer), but its clickable shell is the shared one: the plate's
        // hover/press reveal, the Button role, the tab stop and the hand cursor all come from the structure's surface shell and
        // the ownership rule (a clickable, free surface), not from a hand-set copy. The panel's own fill, hairline and
        // card shadow ride on top, and its extent stays the content's (the shell's stack Grow is zeroed).
        var mode = SurfaceRules.Ownership(inSlot: false, hasClick: true);
        BoxEl panel = surface.Shell(new BoxEl { ZStack = true, Grow = 1f, MinWidth = 0f, Children = [wash, content] },
                                    Shape.Grid, in mode, target) with
        {
            Grow = 0f, ClipToBounds = true,
            Fill = Tok.FillCardDefault, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault, Shadow = Elevation.Card,
            OnClick = open,
            Draggable = Drag.Source(() => new DragPayload(Drag.KindOfUri(target), target, target, title, ArtUrl: cover)),
        };
        // The record is a container (or a track) like any other card: right-click opens the same menu.
        return overlay is not null && !Controls.IsNullOverlay(overlay)
            ? ContextSheets.Attach(panel, overlay, () => ArtistVerbs.PickMenu(targetUri, title, cover, kind))
            : panel;
    }

    /// <summary>The pick's photograph band, mounted only once its image is Ready (see <see cref="Card"/>).
    ///
    /// <para>REQUEST WHILE HIDDEN: <c>UseImage</c> is <c>ImageCache.Request</c> + a read of that ONE handle's status
    /// signal (RenderContext.UseImage) — the decode starts with no node mounted, and this component re-renders only on
    /// that handle's own edges (never the global image epoch, never another card). Its (src, decode) key is the
    /// ImageEl's own (<see cref="s_photoDecodeH"/>), so when the band mounts the image pins the SAME Ready entry and
    /// paints on its first frame.</para>
    ///
    /// <para>LATCHED like <see cref="Controls.CoverShimmer"/>: Ready (shown) or a real failure (collapsed for good) stops
    /// the hook call, so a settled card unsubscribes and never re-renders again. A Canceled leftover is NOT a verdict —
    /// the next edge re-requests it. Nothing here polls or loops: the frame clock only runs for the one finite reveal.
    /// The image hook consumes no hook cell, so the conditional call is safe.</para>
    ///
    /// <para>The root is an always-present, zero-extent wrapper (a component root's Key is inert — the keyed band is its
    /// CHILD); the band's clip box grows open through real layout (Reflow + mount opt-in, the drawer idiom) while the
    /// photo fades in inside it. Ready already at mount (a warm cache) snaps in with no motion: a page visit never
    /// replays the entrance.</para></summary>
    sealed class PickPhoto : Component
    {
        public required string Url { get; init; }
        /// <summary>The band arm (photo as a right column) — else the rail arm (photo as a band under the comment).</summary>
        public bool Band { get; init; }

        static readonly LayoutTransition s_growRail = new(TransitionChannels.Size, MotionTok.ControlNormal.ToDynamics(),
            Enter: new EnterExit(Active: true), Size: SizeMode.Reflow, Anchor: SizeAnchor.Leading, Axes: SizeAxes.Height,
            SuppressDescendantTransitions: true);
        static readonly LayoutTransition s_growBand = new(TransitionChannels.Size, MotionTok.ControlNormal.ToDynamics(),
            Enter: new EnterExit(Active: true), Size: SizeMode.Reflow, Anchor: SizeAnchor.Leading, Axes: SizeAxes.Width,
            SuppressDescendantTransitions: true);
        static readonly LayoutTransition s_fadeIn = new(TransitionChannels.Opacity, MotionTok.ControlNormal.ToDynamics(),
            Enter: new EnterExit(Opacity: 0f, Active: true));

        bool _shown, _failed, _rendered, _instant;

        /// <summary>The photo band itself (150 tall under the comment, or a 300-wide right column): the ONE builder the
        /// revealed band and the skeleton proxy share.</summary>
        internal static BoxEl Shape(string url, bool band) => new()
        {
            Key = "pick-photo-band",
            Height = band ? float.NaN : WinUiArtistRules.PickPhotoHeight,
            Width = band ? WinUiArtistRules.PickPhotoColumnW : float.NaN,
            Shrink = 0f, AlignSelf = band ? FlexAlign.Stretch : FlexAlign.Auto,
            ZStack = true, ClipToBounds = true,
            Children =
            [
                // ch 08 BUG F: WATCHED, not the raw `R.Layer.ArtPlaceholder` ColorF (Controls.ArtworkFill's own
                // pattern). The entry is Ready when this mounts, so the placeholder only covers an eviction race.
                Ui.Image(url, ImageFit.Cover, aspect: WinUiArtistRules.PickPhotoAspect, decodePx: WinUiArtistRules.PickPhotoDecodeW, corners: 0f,
                         placeholder: (ColorF?)null) with
                {
                    Placeholder = Design.WatchedPlaceholder(url),
                    AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch,
                },
            ],
        };

        public override Element Render()
        {
            if (!_shown && !_failed)
            {
                var image = UseImage(Url, WinUiArtistRules.PickPhotoDecodeW, s_photoDecodeH);
                if (image.State == ImageState.Ready) { _shown = true; _instant = !_rendered; }
                else if (image.State == ImageState.Failed && image.Failure != ImageFailureKind.Canceled) _failed = true;
            }
            _rendered = true;

            var root = new BoxEl
            {
                Direction = (byte)(Band ? 0 : 1), Shrink = 0f, MinWidth = 0f,
                AlignSelf = Band ? FlexAlign.Stretch : FlexAlign.Auto,
            };
            if (!_shown) return root;   // zero extent: no band, no placeholder, nothing reserved

            Element picture = Shape(Url, Band) with { Animate = _instant ? null : s_fadeIn };
            return root with
            {
                Children =
                [
                    new BoxEl
                    {
                        // The CLIP box eases its size only (a position terminal here would move the window itself); the
                        // band inside it fades under the stationary window. Reduced motion snaps the size, keeps the fade.
                        Key = "pick-photo", Direction = (byte)(Band ? 0 : 1), MinWidth = 0f, Shrink = 0f, ClipToBounds = true,
                        AlignSelf = Band ? FlexAlign.Stretch : FlexAlign.Auto,
                        Animate = _instant || Design.Reduced ? null : Band ? s_growBand : s_growRail,
                        Children = [picture],
                    },
                ],
            };
        }
    }
}
