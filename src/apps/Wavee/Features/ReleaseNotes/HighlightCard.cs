using System;
using System.Collections.Generic;
using System.IO;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using Wavee.Core;
using Wavee.Core.ReleaseNotes;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>One hero highlight: poster, kind pill, title, one sentence, and (when the document names a
/// <c>wavee://open?route=…</c> deep link) a "try it" line that navigates.
///
/// <para>MOTION: the card renders a POSTER, never a live video. Wavee's only video surface is
/// <c>MediaPlayerElement</c>, which binds an <c>IMediaPlayer</c> (the Spotify playback session) — there is no
/// file-backed player to hand a cached <c>.mp4</c> to, so a video highlight shows its poster plus the play glyph the
/// prototype draws, and the mp4 stays a release asset for the GitHub page. Reduced motion is therefore already
/// honoured by construction; nothing here branches on <c>Motion.ReducedMotion</c>.</para></summary>
static class HighlightCard
{
    /// <summary>The poster's shape, and the ONLY thing that decides the media band's height: release images are
    /// authored 1200×675 (see <c>ops/release/wavee/README.md</c>), so the band takes the card's width and derives
    /// 16:9 from it rather than pinning a fixed pixel height a wide card would letterbox and a narrow one would crop.</summary>
    const float PosterAspect = 16f / 9f;

    /// <summary>The decode target for a poster. The band is FLUID (its width is whatever the card column resolved to),
    /// so the request-time box is unknown and <c>ImageEl.DecodePx</c> is what the decoder sizes against — 1200 px, the
    /// authored width, with the reconciler deriving 675 from <c>AspectRatio</c>. Without it the decode target collapses
    /// to the band's only known extent and the poster arrives as a smear.</summary>
    const float PosterDecodePx = 1200f;

    /// <summary>The dialog is a modal plate, not a page: its cards must stay inside one viewport, so the 16:9 band is
    /// capped here. The full page has no cap — a wider card simply gets a taller, correctly-proportioned poster.</summary>
    const float CompactMediaMaxH = 236f;

    /// <summary>A highlight card never grows past this: with one or two highlights a full-row card would carry a
    /// 16:9 band several hundred pixels tall (or, capped, a cropped one). 420 px keeps the poster at its authored
    /// proportion (~236 px tall) and a lone card reads as a card, not a banner.</summary>
    const float CardMaxW = 420f;

    /// <summary>The full card (What's-new page): poster, pill, title, body, deep link.</summary>
    public static Element Create(HighlightItem item, Action<string, string?>? nav)
        => Build(item, nav, float.NaN, compact: false);

    /// <summary>The dialog card: the same 16:9 poster, capped in height, and no deep-link line (the dialog's own
    /// buttons are the actions).</summary>
    public static Element Compact(HighlightItem item) => Build(item, null, CompactMediaMaxH, compact: true);

    static Element Build(HighlightItem item, Action<string, string?>? nav, float mediaMaxH, bool compact)
    {
        var h = item.Highlight;
        bool store = HighlightVisibility.IsStore(h);
        var body = new List<Element>(3)
        {
            new TextEl(h.Title) { Size = 13.5f, Weight = 600, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap },
            // The body is markdown-lite like every changelog item (authors write **bold** and `code` in highlights too).
            RichTextBlock.Paragraph(ReleaseNotesText.ToSpans(MarkdownLite.Tokenize(h.Body), url => ShellOpen.OpenUrl(url)))
                with { Size = 12.5f, Color = Tok.TextSecondary },
        };

        // The store announcement carries its own call to action — a real accent button opening the listing in the
        // Store app — and, below, never the whole-card deep-link treatment: a card that is one big button AND holds a
        // button is two nested click targets.
        if (store)
            body.Add(new BoxEl
            {
                Direction = 0, Margin = new Edges4(0f, 8f, 0f, 0f),
                Children = [ Button.Accent(Loc.Get(Strings.WhatsNew.StoreCta), OpenStoreListing) ],
            });

        (string Route, string? Arg)? link = null;
        if (!store && !compact && h.DeepLink is { Length: > 0 } dl && DeepLink.TryParse(dl, out var verb) && verb.Kind == DeepLinkKind.Open)
        {
            link = (verb.Route, verb.Arg.Length == 0 ? null : verb.Arg);
            body.Add(new TextEl(Loc.Get(Strings.WhatsNew.TryIt))
                { Size = 12f, Weight = 600, Color = Tok.AccentTextPrimary, Margin = new Edges4(0f, 4f, 0f, 0f) });
        }

        var card = new BoxEl
        {
            Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, MaxWidth = CardMaxW,
            Corners = CornerRadius4.All(Radii.Card), ClipToBounds = true,
            // The store card wears the accent edge (the SinceBanner treatment): the one announcement with a button
            // should read as the strip's headline without leaving the stock card language.
            BorderWidth = 1f, BorderColor = store ? Tok.AccentDefault : Tok.StrokeCardDefault,
            Children =
            [
                Media(h, item.Poster, mediaMaxH, store),
                new BoxEl
                {
                    Direction = 1, Gap = 4f, MinWidth = 0f,
                    Padding = new Edges4(12f, 10f, 12f, 12f),
                    Children = body.ToArray(),
                },
            ],
        };

        if (link is not { } target || nav is null) return card.Interactive(Interaction.Card);

        return (card with
        {
            Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = true,
            OnClick = () => nav(target.Route, target.Arg),
        }).Interactive(Interaction.Card);
    }

    /// <summary>The poster band. A document with no media (or one whose file never made it into the cache) still gets a
    /// band — a tinted plate carrying the kind pill — because a card that suddenly loses its top third reads as broken
    /// rather than as "no screenshot this time".
    ///
    /// <para>The band has NO fixed height: it stretches to the card's width and derives 16:9 from it
    /// (<see cref="PosterAspect"/>), which is the shape release images are authored at. <paramref name="maxHeight"/>
    /// caps that on the dialog's compact card and is <c>NaN</c> (uncapped) on the page.</para></summary>
    /// <param name="maxHeight">A cap on the derived 16:9 height, or <c>NaN</c> for none.</param>
    /// <param name="store">The store announcement: its plate and pill go accent-tinted (the card has no poster to
    /// carry, so the tint is what makes the band read as deliberate rather than as a missing screenshot).</param>
    static Element Media(ReleaseHighlight h, string? poster, float maxHeight, bool store)
    {
        var layers = new List<Element>(3);
        if (poster is { Length: > 0 })
            // ImageEl carries no Grow/Shrink (it is a leaf with its own intrinsic sizing), so the poster fills the band
            // by STRETCHING across the ZStack — both extents left fluid so it takes the band's whole box.
            //
            // AspectRatio + DecodePx are what make the DECODE right, and they are not decoration: the reconciler sizes
            // the decode from Width/Height, else from DecodePx with the missing extent derived from AspectRatio. A
            // poster that named only its band height decoded at 1×<height> and arrived as a smear. 1200×675 is the
            // authored size, so the decode is 1:1 with the file and the GPU never upsamples.
            //
            // The top corners are rounded on the IMAGE as well as clipped by the card: the card's ClipToBounds
            // constrains the band's box, and rounding the poster itself is what keeps the card's own radius reading as
            // one continuous edge rather than a square photo peeking out of a rounded frame.
            layers.Add(new ImageEl
            {
                Source = poster, Fit = ImageFit.Cover,
                AspectRatio = PosterAspect, DecodePx = PosterDecodePx,
                Corners = new CornerRadius4(Radii.Card, Radii.Card, 0f, 0f),
                Placeholder = Tok.FillSubtleSecondary, AlignSelf = FlexAlign.Stretch,
            });

        layers.Add(new BoxEl
        {
            Grow = 1f, Direction = 0, AlignItems = FlexAlign.Start, Padding = Edges4.All(8f),
            HitTestVisible = false,
            Children = [ KindPill(h.Kind, store), Spacer(), PlayGlyph(h) ],
        });

        return new BoxEl
        {
            AspectRatio = PosterAspect, MaxHeight = maxHeight,
            AlignSelf = FlexAlign.Stretch, MinWidth = 0f, ZStack = true, ClipToBounds = true,
            Fill = store ? Tok.AccentSubtle : Tok.FillSubtleSecondary,
            Children = layers.ToArray(),
        };
    }

    /// <summary>Open the Wavee listing in the Store app. <c>StoreId</c> is stamped for EVERY channel
    /// (<c>Wavee.csproj</c> defaults <c>WaveeStoreId</c>, so feed builds carry it too); the literal only covers an
    /// unstamped build (a headless test host, a hand-built MSIX), where a dead button would read as broken.</summary>
    static void OpenStoreListing()
    {
        string id = AppVersion.Info.StoreId is { Length: > 0 } stamped ? stamped : "9NJPVWTQPT9H";
        ShellOpen.OpenUrl(StoreLinks.ProductPage(id));
    }

    /// <summary>Resolve a highlight's poster to a real on-disk path, or null.
    /// <para>Called from the LOADER, never from <c>Render</c>. It touches the file system (<c>MediaPath</c> plus a
    /// <c>File.Exists</c>), and a render pass does that on the UI thread once per card, on every hover, scroll,
    /// re-theme and reconcile — for an answer that cannot change while the page is open. The result rides on
    /// <see cref="HighlightItem.Poster"/> instead.</para></summary>
    public static string? ResolvePoster(ReleaseHighlight? h, ReleaseNotesStore? store, ReleaseNotesDocument? doc)
    {
        if (store is null || doc is null || h?.Media is not { } m) return null;
        // A video highlight names its own still in Poster; an image highlight IS its still.
        string? src = string.Equals(m.Kind, "video", StringComparison.OrdinalIgnoreCase) ? m.Poster : m.Src;
        if (string.IsNullOrEmpty(src)) return null;
        try
        {
            string path = store.MediaPath(doc, src);
            return File.Exists(path) ? path : null;
        }
        catch { return null; }   // a malformed src is a missing poster, never a crash on a notes page
    }

    static Element KindPill(string? kind, bool store)
    {
        string label = Loc.Get(store ? Strings.WhatsNew.Kind.Store : kind switch
        {
            "rebuilt" => Strings.WhatsNew.Kind.Rebuilt,
            "improved" => Strings.WhatsNew.Kind.Improved,
            _ => Strings.WhatsNew.Kind.New,
        });
        return new BoxEl
        {
            Shrink = 0f, Padding = new Edges4(8f, 2f, 8f, 2f), Corners = CornerRadius4.All(Radii.Pill),
            Fill = store ? Tok.AccentDefault : Tok.FillSolidBase,
            Children = [ new TextEl(label)
                { Size = 11f, Weight = 600, Color = store ? Tok.TextOnAccentPrimary : Tok.TextPrimary } ],
        };
    }

    static Element PlayGlyph(ReleaseHighlight h)
    {
        if (h.Media is not { } m || !string.Equals(m.Kind, "video", StringComparison.OrdinalIgnoreCase))
            return new BoxEl { Width = 0f, HitTestVisible = false };
        return new BoxEl
        {
            Width = 32f, Height = 32f, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = CornerRadius4.All(16f), Fill = Tok.MediaScrim,
            Children = [ Icon(Icons.Play, 14f, Tok.OnMediaPrimary) ],
        };
    }
}
