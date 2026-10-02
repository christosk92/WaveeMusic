// ── Platform/Controls.ArtistAbout.cs — the one "About the artist" card ─────────────────────────────────────────────────
//
// The right rail and the album page both show the artist as a card: a portrait, the name (+ verified check), the bio and a
// Follow toggle. They used to be two trees (the album one was not focusable and nested Follow inside the clickable block).
// This is the one tree: the LINK (portrait + text) is the card's single click owner and takes the hand, the Button role and
// the tab stop from SurfaceRules.Ownership; the Follow toggle is a SIBLING footer/trailing cell, never inside the link (a
// button nested in a button role is two tab stops for one gesture, and its press would start the link press). Each place
// keeps its own layout through AboutLayout.
//
// Role: UI
// Owner: P
// Wave: interaction-consistency (WP6)
// Budget: 200 lines

using System.Collections.Generic;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>Where the about card sits: <see cref="Rail"/> = a narrow column (hero band over name, fact chips, 5-line bio,
/// Follow footer under the link); <see cref="Page"/> = a full-width row (84 round portrait, eyebrow, name, 2-line bio,
/// Follow trailing).</summary>
public enum AboutLayout : byte { Rail, Page }

public static partial class Controls
{
    /// <summary>The about-the-artist card. Props freeze at mount, so a caller keys it on the artist.</summary>
    public static Element ArtistAboutCard(Artist artist, AboutLayout layout)
    {
        string name = artist.Name;
        string artistUri = artist.Uri.Text;
        var mode = SurfaceRules.Ownership(inSlot: false, hasClick: true);
        Action open = () => Track.GoToArtist(artist);
        Element follow = Embed.Comp(() => new FollowToggle { Uri = artistUri, Name = name }) with { Key = "follow:" + artistUri };
        return layout == AboutLayout.Rail ? RailAbout(artist, name, mode, open, follow) : PageAbout(artist, name, mode, open, follow);
    }

    static Element PageAbout(Artist artist, string name, SurfaceOwnership mode, Action open, Element follow)
    {
        string bio = artist.BioLeadId.IsEmpty ? "" : Entities.Strings.Resolve(artist.BioLeadId);
        var link = new BoxEl
        {
            Direction = 0, Gap = Spacing.L, AlignItems = FlexAlign.Center, Grow = 1f, Basis = 0f, MinWidth = 0f,
            Padding = new Edges4(Spacing.L, Spacing.M, 0f, Spacing.M),
            Corners = new CornerRadius4(Radii.Card, 0f, 0f, Radii.Card), HoverFill = Tok.FillCardDefault,
            Role = mode.Role, Focusable = mode.OwnsFocus, Cursor = SurfaceRules.Cursor(in mode),
            FocusVisualMargin = Design.FocusInsetBordered, OnClick = open,
            Children =
            [
                new BoxEl
                {
                    Width = 84f, Height = 84f, Shrink = 0f, Corners = CornerRadius4.All(42f), ClipToBounds = true,
                    Children = [PersonPicture.Create("", 84f, displayName: name, imageSourcePath: ArtUrl(artist.ImageId))],
                },
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = Spacing.XS,
                    Children =
                    [
                        Design.Type.Eyebrow(Loc.Get(Strings.Detail.AboutTheArtist))
                            with { Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                        new BoxEl
                        {
                            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, MinWidth = 0f,
                            Children =
                            [
                                // The name takes its measured width and only SHRINKS: the check sits right after the last glyph.
                                new TextEl(name)
                                {
                                    Size = 20f, LineHeight = 28f, Weight = 700, Color = Tok.TextPrimary,
                                    Shrink = 1f, MinWidth = 0f, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                                },
                                artist.IsVerified
                                    ? Icon(Icons.Check, 12f, Tok.TextSecondary) with { Shrink = 0f }
                                    : new BoxEl { Width = 12f, Height = 12f, Shrink = 0f },
                            ],
                        },
                        bio.Length > 0
                            ? Design.Type.DenseMeta(bio) with
                            {
                                Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.CharacterEllipsis,
                            }
                            : new BoxEl(),
                    ],
                },
            ],
        };
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, AlignSelf = FlexAlign.Stretch,
            Corners = Radii.CardAll, Fill = Tok.FillCardSecondary, ClipToBounds = true,
            BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            Children = [link, new BoxEl { Shrink = 0f, Padding = new Edges4(Spacing.L, 0f, Spacing.L, 0f), Children = [follow] }],
        };
    }

    static Element RailAbout(Artist artist, string name, SurfaceOwnership mode, Action open, Element follow)
    {
        const float heroH = 132f;
        var facts = new List<Element>(3);
        if (artist.MonthlyListeners > 0) facts.Add(AboutFact(artist.MonthlyListeners.ToString("N0"), Loc.Get(Strings.Artist.MetaMonthly)));
        if (artist.Followers > 0) facts.Add(AboutFact(artist.Followers.ToString("N0"), Loc.Get(Strings.Artist.MetaFollowers)));
        if (artist.WorldRank > 0) facts.Add(AboutFact("#" + artist.WorldRank.ToString("N0"), Strings.Artist.WorldRank("").Trim()));

        string? heroUrl = ArtUrl(artist.HeroImageId);
        Element band = heroUrl is { Length: > 0 }
            ? new BoxEl
            {
                Height = heroH, ZStack = true, ClipToBounds = true, Corners = Radii.CardAll,
                EdgeFade = new EdgeFadeSpec(EdgeMask.Bottom, 56f),
                Children =
                [
                    Ui.Image(heroUrl, ImageFit.Cover, 2.6f, 320f, Radii.Card, Tok.FillSubtleSecondary),
                    new BoxEl
                    {
                        Height = heroH, Corners = Radii.CardAll,
                        Gradient = GradientDown(new GradientStop(0f, ColorF.FromRgba(0, 0, 0, 0)),
                            new GradientStop(0.55f, ColorF.FromRgba(0, 0, 0, 31)), new GradientStop(1f, ColorF.FromRgba(0, 0, 0, 158))),
                    },
                ],
            }
            : new BoxEl
            {
                Height = 72f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Fill = Tok.FillSubtleSecondary,
                Corners = Radii.CardAll, ClipToBounds = true,
                Children = [PersonPicture.Create("", 56f, displayName: name, imageSourcePath: ArtUrl(artist.ImageId))],
            };

        // The name carries the trim tooltip (a long name is cut in a 200-DIP rail). The tip sits in a COLUMN slot the row
        // sizes: a ToolTip wrapper is Shrink 0 and may never be a shrinking row child.
        var nameStyle = new TextEl(name)
            { Size = 18f, LineHeight = 24f, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f };
        Element nameTip = TrimTip(
            new BoxEl { Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, AlignItems = FlexAlign.Stretch, Children = [nameStyle] }, name, nameStyle);
        var body = new List<Element>(4)
        {
            new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.XS,
                Children =
                [
                    new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Children = [nameTip] },
                    artist.IsVerified ? Icon(Icons.Check, 12f, Tok.AccentTextPrimary) : new BoxEl(),
                ],
            },
        };
        if (facts.Count > 0) body.Add(new BoxEl { Direction = 0, Wrap = true, Gap = Spacing.S, Children = facts.ToArray() });
        string bio = Entities.Strings.Resolve(artist.BioLeadId.IsEmpty ? artist.BioId : artist.BioLeadId);
        if (!string.IsNullOrWhiteSpace(bio))
            body.Add(new TextEl(bio) { Size = 14f, LineHeight = 20f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxLines = 5, Trim = TextTrim.CharacterEllipsis });

        return new BoxEl
        {
            Direction = 1, Corners = Radii.CardAll, Fill = Tok.FillCardSecondary, ClipToBounds = true,
            BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            Children =
            [
                new BoxEl
                {
                    Direction = 1, Corners = new CornerRadius4(Radii.Card, Radii.Card, 0f, 0f), HoverFill = Tok.FillCardDefault,
                    Role = mode.Role, Focusable = mode.OwnsFocus, Cursor = SurfaceRules.Cursor(in mode),
                    FocusVisualMargin = Design.FocusInsetBordered, OnClick = open,
                    Children = [band, new BoxEl { Direction = 1, Gap = Spacing.M, Padding = Edges4.All(Spacing.M), Children = body.ToArray() }],
                },
                new BoxEl { Direction = 0, Padding = new Edges4(Spacing.M, 0f, Spacing.M, Spacing.M), Children = [follow] },
            ],
        };
    }

    static Element AboutFact(string value, string label) => new BoxEl
    {
        Direction = 1, Gap = Spacing.XXS, Padding = new Edges4(Spacing.S, Spacing.XS, Spacing.S, Spacing.XS),
        Corners = Radii.ControlAll, Fill = Tok.FillSubtleSecondary,
        Children =
        [
            new TextEl(value) { Size = 12f, LineHeight = 16f, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
            new TextEl(label) { Size = 12f, LineHeight = 16f, Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
        ],
    };
}
