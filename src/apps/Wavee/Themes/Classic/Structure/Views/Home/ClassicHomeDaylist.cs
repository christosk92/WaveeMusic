// ── Themes/Classic/Structure/Views/Home/ClassicHomeDaylist.cs — Classic's `Home.Daylist` (themes plan 6.1, T10a1) ────────────
//
// From `Home\Daylist.UI.cs` (home rebuild, fifth → seventh pass): the daylist "play-now" card over stock controls.
//
// One `Ui.Card` (border, 8-DIP corners, at least 320 tall, NO padding, ClipToBounds — the art and the veil clip to its
// rounded corners; its border paints after its children, so it stays on top of the art) around a `Responsive.Of`
// ZStack built at the card's width — the artist hero's layering (`ClassicArtistHero.Build`: media · veil · copy):
//   art:  `Controls.CoverFill` of `DaylistArt.Of` across the WHOLE card (corner 0: the card's clip rounds it), focused a
//         little above centre (the crop stays centred horizontally);
//   veil: `Palette.ArtistHeroVeil`, horizontal (0 → .96, .30 → .92, .62 → .35, 1 → 0), cover-keyed, in its STRETCH arm
//         (NaN extent: it takes the card's content-driven height), hit-test free;
//   copy: the left column over the veil's opaque side — `DaylistForm.CopyWidth` of the card's content width (half of
//         it within [340, 560]; the whole card below `DaylistForm.SplitMin`) inside `DaylistCopyPadding` 32/32/12/24,
//         the card's insets, which ride on the copy so the art reaches the card's edges.
//   No art url ⇒ neither layer: the plain card, the copy alone. The whole card is the click target.
//   copy column (SpaceBetween)
//     top:    eyebrow (body, secondary: the greeting lives ONLY here) · the title (≤ 2 lines; the 40/52
//             `R.Type.HeroTitle` while the copy is ≥ `DaylistForm.HeroTitleMinText`, else the 28/36 display
//             rung at 600, so a narrow copy keeps the name on one line and the card at its 320 floor) ·
//             tags (ONE 14/20 `SpanTextEl` of link spans, a tag opens Search) · meta (caption, tertiary) ·
//             ONE action row: stock Accent Play + Standard Shuffle + `Controls.SaveButton` + a standalone "…" icon button
//     bottom: the clock (`Home.DaylistClock`, mounted by the clock host the card's host built and keyed)
//
// The "…" button re-enters the engine's context funnel (`ClickRequestsContext`) and finds the card's ATTACHED playlist
// menu (`HomeCardNav.MenuOf`, the grammar every other card wears) — attaching it needs the overlay service, which the host
// hands in. What must SUBSCRIBE (the greeted name, whether the ▶ names Pause) was read by the host; the saved heart is the stock
// `SaveButton`, which subscribes on its own. The cold-load skeleton is the SAME builders fed placeholders (`Skeleton`) — the placeholder
// art slot across the card, no veil — so it shimmers as this card's real shape.
// Pure rules (core): `DaylistForm` / `DaypartTimeline` / `DaylistCountdownLine` / `DaylistArt` (Items.Rules.cs) and
// `DaypartRules` / `DaylistCountdown` / `DaylistNext`.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Theming;
using Wavee.HomeUi;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's daylist card.</summary>
internal static class ClassicHomeDaylist
{
    /// <summary>The card's content, built once per render (or from placeholders for the skeleton) and laid out by
    /// <see cref="CardShape"/> at whatever width the card measures. <c>Art</c> / <c>Veil</c> are the two layers under the
    /// copy — both null for a daylist without art (the plain card); the skeleton has the placeholder
    /// art slot and no veil.</summary>
    readonly record struct Parts(string Eyebrow, string Title, Element Tags, string Meta, Element Actions, Element Clock,
                                 Element? Art, Element? Veil);

    /// <summary>The registered template of <see cref="ViewKeys.HomeDaylist"/>.</summary>
    public static Element Template(HomeDaylistProps props, in ViewContext ctx)
    {
        var zone = props.Zone;
        if (zone.Items.Count == 0) return new BoxEl();
        var card = zone.Items[0];
        string uri = card.Uri;
        if (uri.Length == 0) return new BoxEl();

        var host = props.Overlay;
        var menu = HomeCardNav.MenuOf(in card);
        var c = card;
        string title = card.Title;

        void Play() => HomeCardNav.Play(in c);
        // Shuffle STARTS the daylist shuffled; it never toggles (HomeCardNav.Play pauses a daylist that already plays).
        void Shuffle() => HomeVerbs.ShuffleContext(c.Uri);
        // The ▶ names what a press does: Pause while this daylist is the context playing (read by the host: a subscribing, coarse-first read).
        bool pauses = props.Pauses;

        var actions = ActionRow(
            Button.Create(Loc.Get(pauses ? Strings.Home.Pause : Strings.Home.Play), Play, ButtonAppearance.Accent,
                          glyph: pauses ? Icons.Pause : Icons.Play)
                with { MinWidth = Controls.PrimaryMinWidth, Shrink = 0f },
            Button.Create(Loc.Get(Strings.Detail.Shuffle), Shuffle, ButtonAppearance.Standard, glyph: Icons.Shuffle)
                with { Shrink = 0f },
            Controls.Named(Embed.Comp(() => new Controls.SaveButton { Uri = uri, Name = title }) with { Key = "save:" + uri },
                           Loc.Get(Strings.Home.Save)),
            // A STANDALONE icon button, not `Controls.MoreButton`: that one is the list-row overflow whose 0.45-opacity
            // rest + HoverOpacity reveal lights up when its ANCESTOR is hovered — on this card it lit (and pressed) the
            // "…" whenever the card was hovered or pressed.
            Controls.Named(Controls.IconAction(Icons.More, null, requestsContext: true) with { BlocksDragArm = true },
                           Loc.Get(Strings.Home.More)));

        // The header art across the whole card under the artist hero's horizontal veil; no art ⇒ neither layer (the
        // plain card, like the artist hero's "no url ⇒ flat"). The veil leaf is cover-keyed (a late grading swaps its
        // gradient without rebuilding this card) and keyed on the CARD, the surface, never the url: a new daylist's art
        // re-grades the mounted leaf, which cross-fades its tone instead of remounting. The card's accent is the
        // ladder's payload rung until the grading lands.
        string? artUrl = DaylistArt.Of(card.HeaderImageUrl, card.ImageUrl);
        Element? art = artUrl is null ? null : ArtLayer(artUrl);
        Element? veil = artUrl is null ? null
            : Palette.ArtistHeroVeil(artUrl, vertical: false, float.NaN, float.NaN, key: "daylist-veil:" + uri,
                                     payloadAccent: card.Accent);

        // The card IS the daylist: a click anywhere outside its buttons opens the playlist (the buttons are nested
        // click targets, so Play/Shuffle/♡/… keep their own action). The tile ramp is the card's hover/press feedback.
        var root = CardShape(new Parts(props.Eyebrow, title, Tags(card), Strings.Home.DaylistMeta(card.TrackCount), actions,
                                       props.Clock ?? new BoxEl(), art, veil))
            .Interactive(Interaction.Tile) with
            {
                OnClick = () => HomeCardNav.Open(in c), Cursor = CursorId.Hand,
                Role = AutomationRole.Button, Focusable = true,
            };
        // The playlist menu rides the card root: a right-click anywhere on the card and the "…" button (which climbs
        // to this handler through the context funnel) open the SAME menu, the button's anchored at itself.
        return menu is not null && host is not null ? root.WithContextMenu(host, menu) : root;
    }

    /// <summary>The cold-load shape: the SAME builders as <see cref="Template"/>, fed placeholder words, stand-in buttons
    /// and a static ring/timeline. Never early-outs — the seed zone's card is a blank (no uri). The embed's
    /// <see cref="ComponentEl.SkeletonProxy"/>.</summary>
    internal static Element Skeleton()
    {
        string words = Loc.Get(Strings.Home.YourDaylist);
        var actions = ActionRow(
            Button.Create(Loc.Get(Strings.Home.Play), static () => { }, ButtonAppearance.Accent, glyph: Icons.Play)
                with { MinWidth = Controls.PrimaryMinWidth, Shrink = 0f },
            Button.Create(Loc.Get(Strings.Detail.Shuffle), static () => { }, ButtonAppearance.Standard, glyph: Icons.Shuffle)
                with { Shrink = 0f },
            Controls.IconAction(Icons.More, null),
            Controls.IconAction(Icons.More, null));
        var current = DaypartRules.OfHour(DateTime.Now.Hour);
        var line = ClassicHomeDaylistClock.Line((TextSpans)new TextSpan[] { new(Strings.Home.NextDaylistIn(DaypartRules.Countdown(0))) });
        // The caption's placeholder is the real rule read at "now": a caption-width line with the next daypart's word.
        string caption = DaylistNext.Caption(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), current,
                                             TimeZoneInfo.Local, System.Globalization.CultureInfo.CurrentCulture);
        var clock = ClassicHomeDaylistClock.Shape(ProgressRing.Determinate(0f, ClassicHomeRules.RingSize), line, caption, null, (int)current);
        // The art slot as its watched placeholder across the card (the real shape), and no veil: a component the
        // deriver cannot see into would shimmer as one stray bar.
        return CardShape(new Parts(words, Loc.Get(Strings.Home.Layout.Daylist), TagLine(new TextSpan[] { new(words) }, null),
                                   Strings.Home.DaylistMeta(50), actions, clock, ArtLayer(null), null));
    }

    /// <summary>The card: a Fluent card frame (border, 8-DIP corners) with NO padding — the art reaches its edges and
    /// the insets ride on the copy (<see cref="ClassicHomeRules.DaylistCopyPadding"/>) — that clips its layers to its rounded corners
    /// (the border paints after the children, so it stays on top of the art), around the layers built at the card's
    /// width (<see cref="Row"/>; the copy's width follows <see cref="DaylistForm"/>).</summary>
    static BoxEl CardShape(Parts parts)
        => Ui.Card(Responsive.Of(w => Row(parts, w), fallback: HomeModuleLayout.FallbackWidth, grow: 1f))
            with
            {
                Padding = default, ClipToBounds = true, MinHeight = ClassicHomeRules.DaylistCardMinHeight,
                MinWidth = 0f, AlignSelf = FlexAlign.Stretch,
            };

    /// <summary>The art layer: <see cref="Controls.CoverFill"/> across the whole card, corner 0 (the card's rounded clip
    /// rounds it), focused a little above centre. A null <paramref name="url"/> is the watched placeholder slot (the
    /// skeleton's).</summary>
    static Element ArtLayer(string? url)
        => Controls.CoverFill(url, 0f, ClassicHomeRules.DaylistArtDecodePx, focusY: ClassicHomeRules.DaylistArtFocusY);

    /// <summary>The card's layers at card width <paramref name="w"/> (the card has no padding, so this is its whole
    /// width), in a ZStack: the art, its veil, then the copy over the veil's opaque side —
    /// <see cref="DaylistForm.CopyWidth"/> of the content width inside the copy padding, plus that
    /// padding. Neither layer measures anything, so the copy alone decides the card's height above its 320 floor, and
    /// the stack stretches both layers over the whole card and the copy over its full height (SpaceBetween pins the
    /// clock to the foot). The three slots never move — an absent layer is an empty, hit-test-free box — and the copy
    /// is keyed, so art arriving late mounts the layers without remounting the copy and its clock.</summary>
    static Element Row(Parts p, float w)
    {
        var pad = ClassicHomeRules.DaylistCopyPadding;
        float text = DaylistForm.CopyWidth(MathF.Max(0f, w - pad.Horizontal));
        var copy = new BoxEl
        {
            Key = "daylist-copy",
            Direction = 1, Width = text + pad.Horizontal, MinWidth = 0f,
            // SpaceBetween pins the clock to the card's foot; the Gap is its floor when a two-line title fills the card.
            Justify = FlexJustify.SpaceBetween, Padding = pad, Gap = Spacing.XL,
            Children = [Top(p, text), p.Clock],
        };
        return new BoxEl
        {
            ZStack = true, Grow = 1f, MinWidth = 0f, AlignItems = FlexAlign.Stretch,
            Children = [p.Art ?? NoLayer(), p.Veil ?? NoLayer(), copy],
        };
    }

    /// <summary>An absent layer's slot: empty and hit-test free.</summary>
    static BoxEl NoLayer() => new() { HitTestVisible = false };

    /// <summary>The copy's top block at text width <paramref name="text"/>: eyebrow · title · tags · meta · the ONE
    /// action row.</summary>
    static BoxEl Top(Parts p, float text)
        => new()
        {
            Direction = 1, MinWidth = 0f,
            Children =
            [
                Ui.Body(p.Eyebrow).Secondary() with { MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                TitleRung(p.Title, text) with
                {
                    MaxLines = 2, Wrap = TextWrap.Wrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                    Margin = new Edges4(0f, Spacing.XS, 0f, 0f),
                },
                p.Tags,
                Ui.Caption(p.Meta).Tertiary() with
                {
                    MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                    Margin = new Edges4(0f, Spacing.XS, 0f, 0f),
                },
                p.Actions,
            ],
        };

    /// <summary>The title's rung at the copy's text width <paramref name="text"/>: the 40/52 <c>HeroTitle</c> while
    /// the copy is at least <see cref="DaylistForm.HeroTitleMinText"/> wide, else the 28/36 display rung at 600
    /// (<see cref="DaylistForm.UseHeroTitle"/> over <see cref="DaylistForm.CopyWidth"/>).</summary>
    static TextEl TitleRung(string title, float text)
        => DaylistForm.UseHeroTitle(text)
            ? R.Type.HeroTitle(title)
            : R.Type.DetailHero(title) with { Weight = 600 };

    /// <summary>The ONE action row: 20 below the meta, 8 apart, never wrapping (the copy's floor fits it).</summary>
    static BoxEl ActionRow(params Element[] actions) => new()
    {
        Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, Wrap = false, MinWidth = 0f,
        Margin = new Edges4(0f, Spacing.XL, 0f, 0f),
        Children = actions,
    };

    /// <summary>The tags as ONE 14/20 line: every tag a secondary link span (the engine's Hand cursor over its own laid
    /// rects), " · " separators in tertiary, one line with an ellipsis. Clicks resolve by span INDEX
    /// (`OnSpanClick`): tags sit at the even indices, separators at the odd ones.</summary>
    static Element Tags(HomeCard card)
    {
        int seedCount = Math.Min(card.SeedCount, ClassicHomeRules.DaylistMaxTags);
        var tags = new List<string>(seedCount);
        for (int i = 0; i < seedCount; i++)
        {
            string tag = card.SeedAt(i);
            if (tag.Length > 0) tags.Add(tag);
        }
        if (tags.Count == 0) return new BoxEl();

        var spans = new TextSpan[tags.Count * 2 - 1];
        for (int i = 0; i < tags.Count; i++)
        {
            if (i > 0) spans[2 * i - 1] = new TextSpan(" · ", Color: Tok.TextTertiary);
            spans[2 * i] = new TextSpan(tags[i], Color: Tok.TextSecondary, IsLink: true);
        }
        return TagLine(spans, i => { if ((i & 1) == 0 && (i >> 1) < tags.Count) HomeVerbs.OpenTag(tags[i >> 1]); });
    }

    /// <summary>The tag line's paragraph: the body rung's metrics (never a hand size), tertiary base, 6 below the title.</summary>
    static SpanTextEl TagLine(TextSpan[] spans, Action<int>? onSpanClick)
    {
        var body = Ui.Body("");
        return new SpanTextEl(spans)
        {
            Size = body.Size, LineHeight = body.LineHeight, Color = Tok.TextTertiary,
            Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
            Margin = new Edges4(0f, Spacing.XS + Spacing.XXS, 0f, 0f),
            OnSpanClick = onSpanClick,
        };
    }
}
