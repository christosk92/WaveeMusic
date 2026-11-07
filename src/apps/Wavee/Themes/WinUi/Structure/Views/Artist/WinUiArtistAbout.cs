// ── Themes/WinUi/Structure/Views/Artist/WinUiArtistAbout.cs — WinUI's "About" card on the artist page (W-Artist-Huge) ──────────────────────────────────
//
// Board W-Artist-Huge draws About as a CARD, not a link: the photograph band (168), the stats (the monthly listeners and the world rank, Subtitle numbers over
// Caption labels, 32 apart), the bio and a "Read more" hyperlink. The page used to draw the shared about card (`Controls.ArtistAboutCard`, the album page's
// aside: the band, the name beside Follow, a four-line bio), whose whole body is a link to the artist: on the artist's own page that link navigated to the
// page already open, so the card lit on hover and took the hand and the click did nothing (owner, 2026-11-07, INNA's About tab).
//
// Here nothing on the card's body is a target. "Read more" opens the full biography IN PLACE (the board's link under the bio): the whole text as rich text
// (its links live), and the artist's gallery photographs as a row of thumbnails that open the lightbox; "Show less" folds it back. The block's height eases
// through the engine's flow reveal (the sections under it ride it, nothing steps). The links and the cities stay under the card (`AboutBlock`).
//
// The band's crop keeps the subject in view: a header photograph is a wide picture of a person, and a centred crop of it into a short band showed the
// waist and the legs (the face cut off above the band). The band reads where the photograph's subject stands (`Palette.Subject`: the faces, or Spotify's
// safe zone a third of the way down when none is found) and frames that point in the band's middle, waiting for the measure (it always settles) so the
// crop never moves once the photograph shows, as the hero does.

using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;
using A = Wavee.Themes.WinUi.WinUiArtistRules;

namespace Wavee.Themes.WinUi;

internal static class WinUiArtistAbout
{
    /// <summary>The About card for <paramref name="a"/>; <paramref name="openGallery"/> opens the lightbox at a photograph.</summary>
    public static Element Card(Artist a, Action<string[], int> openGallery)
        => Embed.Comp(new AboutProps(a, openGallery), static () => new AboutCard()) with { Key = "about:" + a.Uri.Text };

    sealed record AboutProps(Artist A, Action<string[], int> OpenGallery)
    {
        public bool Equals(AboutProps? o) => o is not null && o.A == A;
        public override int GetHashCode() => A.Slot;
    }

    sealed class AboutCard : Component
    {
        readonly Signal<bool> _open = new(false);
        readonly Signal<bool> _longer = new(false);
        readonly Action _toggle;
        readonly Action<RectF> _onFull, _onClamped;
        float _fullH = float.NaN, _clampedH = float.NaN;

        public AboutCard()
        {
            _toggle = () => _open.Value = !_open.Peek();
            // "Read more" stands where the bio runs past the clamp: the whole text, laid out unseen at the same width, is taller than the clamped one.
            _onFull = r => { _fullH = r.H; Measure(); };
            _onClamped = r => { _clampedH = r.H; Measure(); };
        }

        void Measure()
        {
            if (float.IsNaN(_fullH) || float.IsNaN(_clampedH)) return;
            bool longer = _fullH > _clampedH + 0.5f;
            if (longer != _longer.Peek()) _longer.Value = longer;
        }

        public override Element Render()
        {
            var p = UseProps<AboutProps>();
            Artist a = p.A;
            bool open = _open.Value;
            string bio = ArtistVerbs.Text(a.BioId);
            if (bio.Length == 0) bio = ArtistVerbs.Text(a.BioLeadId);
            // A closing paragraph tag would leave an empty line under the text (the rich paragraph breaks at it), between the bio and "Read more".
            bio = bio.TrimEnd();
            if (bio.EndsWith("</p>", StringComparison.OrdinalIgnoreCase)) bio = bio[..^4].TrimEnd();
            bool hasBio = ArtistText.StripHtml(bio).Length > 0;

            var body = new List<Element>(4);
            Element? stats = Stats(a);
            if (stats is not null) body.Add(stats.Value);
            if (hasBio)
            {
                var gallery = open ? Gallery(a, p.OpenGallery) : null;
                // The open biography adds the gallery's photographs, so "Read more" also stands for an artist with photographs and a short bio.
                bool photos = a.GallerySlots.Length > 0;
                bool longer = _longer.Value;
                body.Add(Responsive.Of(w =>
                {
                    float width = MathF.Max(1f, w);
                    var kids = new List<Element>(4)
                    {
                        new BoxEl
                        {
                            Direction = 1, MinWidth = 0f, OnBoundsChanged = open ? null : _onClamped,
                            Children = [Controls.RichText(bio, A.AboutBioSize, Tok.TextSecondary, Tok.AccentTextPrimary, width, open ? 0 : A.AboutBioLines, ArtistVerbs.OpenRoute)],
                        },
                    };
                    if (gallery is not null) kids.Add(gallery.Value);
                    if (longer || photos || open)
                        kids.Add(HyperlinkButton.Create(Loc.Get(open ? Strings.Theme.Winui.Detail.ShowLess : Strings.Theme.Winui.Detail.ReadMore), _toggle)
                            with { Key = "about:more", AlignSelf = FlexAlign.Start });
                    // The measure: the whole text at this width in a 0-tall clip (laid out, never drawn, no room taken).
                    if (!open)
                        kids.Add(new BoxEl
                        {
                            Height = 0f, Shrink = 0f, ClipToBounds = true, HitTestVisible = false, Opacity = 0f, Margin = new Edges4(0f, -A.AboutBodyGap, 0f, 0f),
                            Children =
                            [
                                new BoxEl
                                {
                                    Direction = 1, MinWidth = 0f, Shrink = 0f, OnBoundsChanged = _onFull,
                                    Children = [Controls.RichText(bio, A.AboutBioSize, Tok.TextSecondary, Tok.AccentTextPrimary, width, 0, null)],
                                },
                            ],
                        });
                    return new BoxEl
                    {
                        Key = "about:bio", Direction = 1, Gap = A.AboutBodyGap, MinWidth = 0f, Layout = A.AboutReveal, ClipToBounds = true,
                        Children = kids.ToArray(),
                    };
                }));
            }

            return new BoxEl
            {
                Direction = 1, MinWidth = 0f, Corners = Radii.CardAll, ClipToBounds = true,
                Fill = Tok.FillCardDefault, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
                Children =
                [
                    Band(a),
                    new BoxEl
                    {
                        Direction = 1, Gap = A.AboutBodyGap, MinWidth = 0f,
                        Padding = new Edges4(A.AboutPadX, A.AboutPadTop, A.AboutPadX, A.AboutPadBottom), Children = body.ToArray(),
                    },
                ],
            };
        }
    }

    /// <summary>The photograph band: the header photograph (else the portrait) cropped to keep its subject in the band's middle. The image waits for the
    /// subject's measure (faces, or the safe zone), so its crop never moves once it shows; the band's neutral fill stands meanwhile.</summary>
    static Element Band(Artist a)
    {
        string? url = Controls.ArtUrl(a.HeroImageId) is { Length: > 0 } hero ? hero : Controls.ArtUrl(a.ImageId);
        PhotoSubject? subject = url is { Length: > 0 } measured ? Palette.Subject(measured) : null;
        Element[] kids = url is { Length: > 0 } src && subject is { } s
            ?
            [
                new ImageEl
                {
                    Source = src, Fit = ImageFit.Cover, DecodePx = A.AboutDecode, AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch,
                    FocusX = s.X, FocusY = s.Y, FocusFrameX = A.AboutFrameX, FocusFrameY = A.AboutFrameY, Placeholder = Design.WatchedPlaceholder(src),
                },
            ]
            : [];
        return new BoxEl
        {
            Key = "about:band", Height = A.AboutBand, Shrink = 0f, ZStack = true, ClipToBounds = true, Fill = Tok.FillSubtleSecondary, Children = kids,
        };
    }

    /// <summary>The stats: the monthly listeners and the world rank, a Subtitle number over its Caption label each (W-Artist-Huge), or null with neither.</summary>
    static Element? Stats(Artist a)
    {
        var cells = new List<Element>(2);
        if (a.MonthlyListeners > 0)
            cells.Add(Stat(a.MonthlyListeners.ToString("N0", CultureInfo.CurrentCulture), Loc.Get(Strings.Artist.MetaMonthly)));
        if (a.WorldRank > 0)
        {
            string rank = Strings.Artist.WorldRank(a.WorldRank.ToString("N0", CultureInfo.CurrentCulture));
            // "#112 in the world": the number over the rest.
            int cut = rank.IndexOf(' ');
            cells.Add(cut > 0 ? Stat(rank[..cut], rank[(cut + 1)..]) : Stat(rank, ""));
        }
        if (cells.Count == 0) return null;
        return new BoxEl { Key = "about:stats", Direction = 0, Gap = A.AboutStatGap, Wrap = true, MinWidth = 0f, Children = cells.ToArray() };
    }

    static Element Stat(string value, string label) => new BoxEl
    {
        Direction = 1, MinWidth = 0f,
        Children =
        [
            Subtitle(value) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
            Caption(label) with { Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
        ],
    };

    /// <summary>The open biography's photographs: the gallery's first few as square thumbnails that open the lightbox at that photograph.</summary>
    static Element? Gallery(Artist a, Action<string[], int> openGallery)
    {
        var ids = a.GallerySlots;
        int n = Math.Min(ids.Length, ArtistSections.GalleryCap);
        if (n == 0) return null;
        var urls = new string[n];
        for (int i = 0; i < n; i++) urls[i] = Controls.ArtUrl(ids[i]) ?? "";
        int shown = Math.Min(n, A.AboutGalleryThumbs);
        var thumbs = new Element[shown];
        for (int i = 0; i < shown; i++)
        {
            int index = i;
            thumbs[i] = new BoxEl
            {
                Key = "about:photo:" + i.ToString(CultureInfo.InvariantCulture),
                Width = A.AboutThumb, Height = A.AboutThumb, Shrink = 0f, Corners = Radii.ControlAll, ClipToBounds = true,
                Role = AutomationRole.Button, Focusable = true, FocusVisualMargin = Design.FocusInsetBordered, Cursor = CursorId.Hand,
                HoverScale = R.Motion.ScaleStandard.Hover, PressScale = R.Motion.ScaleStandard.Press,
                OnClick = () => openGallery(urls, index),
                Children = [Controls.Artwork(urls[index], A.AboutThumb, A.AboutThumb, Radii.Control, decodePx: A.AboutThumbDecode)],
            };
        }
        return new BoxEl { Key = "about:photos", Direction = 0, Gap = A.AboutThumbGap, Wrap = true, MinWidth = 0f, Children = thumbs };
    }
}
