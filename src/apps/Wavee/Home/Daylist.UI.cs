// ── Home/Daylist.UI.cs — the daylist "play-now" card over stock controls (home rebuild, fifth → seventh pass) ───────
//
// Role: UI
// Spec: docs/plans/wavee/home-rebuild-implementation.md "Fifth pass — stock controls" (zone → control table, Daylist row)
//        + "Seventh pass" (the daypart timeline) + docs/plans/wavee/daylist-hero-fade-implementation.md (variant A: the
//        header art full-bleed inside the card, under the artist hero's veil).
//
// One `Ui.Card` (border, 8-DIP corners, at least 320 tall, NO padding, ClipToBounds — the art and the veil clip to its
// rounded corners; its border paints after its children, so it stays on top of the art) around a `Responsive.Of`
// ZStack built at the card's width — the artist hero's layering (`Artist.UI.cs` `HeroBanner`: media · veil · copy):
//   art:  `Controls.CoverFill` of `DaylistArt.Of` across the WHOLE card (corner 0: the card's clip rounds it), focused a
//         little above centre (the crop stays centred horizontally);
//   veil: `Palette.ArtistHeroVeil`, horizontal (0 → .96, .30 → .92, .62 → .35, 1 → 0), cover-keyed, in its STRETCH arm
//         (NaN extent: it takes the card's content-driven height), hit-test free;
//   copy: the left column over the veil's opaque side — `DaylistForm.CopyWidth` of the card's content width (half of
//         it within [340, 560]; the whole card below `DaylistForm.SplitMin`) inside `Daylist.CopyPadding` 32/32/12/24,
//         the card's insets, which ride on the copy so the art reaches the card's edges.
//   No art url ⇒ neither layer: the plain card, the copy alone. The whole card is the click target.
//   copy column (SpaceBetween)
//     top:    eyebrow (body, secondary: the greeting lives ONLY here) · the title (≤ 2 lines; the 40/52
//             `Design.Type.HeroTitle` while the copy is ≥ `DaylistForm.HeroTitleMinText`, else the 28/36 display
//             rung at 600, so a narrow copy keeps the name on one line and the card at its 320 floor) ·
//             tags (ONE 14/20 `SpanTextEl` of link spans, a tag opens Search) · meta (caption, tertiary) ·
//             ONE action row: stock Accent Play + Standard Shuffle + `Controls.SaveButton` + a standalone "…" icon button
//     bottom: `DaylistClock` — a stock determinate `ProgressRing` centred beside TWO lines ("Next daylist in
//             **hh:mm:ss**" body primary, then the caption "{weekday} {next daypart} arrives at HH:mm" secondary),
//             then five stock determinate `ProgressBar`s (done / current / future, `DaypartTimeline.Fill`)
//             over their five daypart labels (the current one primary 600, never truncated). That is the Counting face;
//             once the window has ended `DaylistClock` swaps it (keyed by window + face) for Updating (indeterminate
//             ring, "Updating your daylist…", "{Next daypart} is on its way", the next segment an indeterminate bar)
//             and, when the rollover ladder gives up, Late ("Your next daylist is running late" + "Check again") —
//             `DaylistClockFace` over `Home.Feeds.RolloverState`.
//
// The "…" button re-enters the engine's context funnel (`ClickRequestsContext`) and finds the card's ATTACHED playlist
// menu (`HomeCardNav.MenuOf`, the grammar every other card wears) — attaching it needs the overlay service, which the
// caller hands in. What must SUBSCRIBE (the greeted name) is read in `DaylistCard.Render`; the saved heart is the stock
// `SaveButton`, which subscribes on its own. The 1-Hz tick lives in `DaylistCountingClock` and reaches the screen through BINDS
// only (the countdown spans, the ring, the five bars): no component re-renders on the tick. The cold-load skeleton is
// the SAME builders fed placeholders (`Daylist.Card`'s `SkeletonProxy`) — the placeholder art slot across the card, no
// veil — so it shimmers as this card's real shape.
// Pure rules: `DaylistForm` / `DaypartTimeline` / `DaylistCountdownLine` / `DaylistArt` (Items.Rules.cs) and
// `DaypartRules` / `DaylistCountdown` / `DaylistNext`.

using System;
using System.Collections.Generic;
using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee;

namespace Wavee.HomeUi;

public static class Daylist
{
    /// <summary>The decode target for the art: it cover-fills the whole card (~1000 DIP on a wide Home), so the square
    /// hint is sized to that, not to a column.</summary>
    public const int ArtDecodePx = 1024;
    /// <summary>The art's focal line: a little above centre, where a daylist header's subject sits.
    /// (<c>Controls.CoverFill</c> takes no horizontal focus, so the crop stays centred on that axis.)</summary>
    public const float ArtFocusY = 0.4f;
    /// <summary>The card's floor height (the prototype's 320 hero).</summary>
    public const float CardMinHeight = 320f;
    /// <summary>The copy column's insets, which are the card's own (the card has no padding, so the art reaches its
    /// edges): 32 from the left edge, the eyebrow 32 below the top, 12 from the right edge (where a narrow card's copy
    /// ends), the timeline labels 24 above the foot.</summary>
    public static readonly Edges4 CopyPadding = new(Spacing.XXXL, Spacing.XXXL, Spacing.M, Spacing.XXL);
    /// <summary>At most this many tag links on the description line.</summary>
    public const int MaxTags = 6;
    /// <summary>The countdown ring's size (the stock ring's minimum).</summary>
    public const float RingSize = ProgressRing.MinSize;

    static readonly Func<Element> s_skeleton = DaylistCard.Skeleton;

    /// <summary>The play-now card (`zone.Kind == ZoneKind.Daylist`, `zone.Items[0]` the daylist card). The skeleton
    /// deriver cannot see into a component, so the card hands it its real shape built from placeholders.</summary>
    public static Element Card(Zone zone, IOverlayService? overlay)
        => Embed.Comp(new DaylistProps(zone, overlay), static () => new DaylistCard()) with { SkeletonProxy = s_skeleton };
}

/// <summary>Props for <see cref="DaylistCard"/> — the zone by value (a re-plan with equal content gates the re-render at
/// the embed boundary), the overlay service by reference.</summary>
public sealed record DaylistProps(Zone Zone, IOverlayService? Overlay)
{
    public bool Equals(DaylistProps? o) => o is not null && Zone.Equals(o.Zone) && ReferenceEquals(Overlay, o.Overlay);
    public override int GetHashCode() => Zone.GetHashCode();
}

public sealed class DaylistCard : Component
{
    /// <summary>The card's content, built once per render (or from placeholders for the skeleton) and laid out by
    /// <see cref="Shape"/> at whatever width the card measures. <c>Art</c> / <c>Veil</c> are the two layers under the
    /// copy — both null for a daylist without art (the plain card); the skeleton has the placeholder
    /// art slot and no veil.</summary>
    readonly record struct Parts(string Eyebrow, string Title, Element Tags, string Meta, Element Actions, Element Clock,
                                 Element? Art, Element? Veil);

    public override Element Render()
    {
        var p = UseProps<DaylistProps>();
        var zone = p.Zone;
        string eyebrow = Eyebrow();                          // subscribing read (users table + scope) — hooks first

        if (zone.Items.Count == 0) return new BoxEl();
        var card = zone.Items[0];
        string uri = card.Uri;
        if (uri.Length == 0) return new BoxEl();

        var host = Controls.IsNullOverlay(p.Overlay) ? null : p.Overlay;
        var menu = HomeCardNav.MenuOf(in card);
        var c = card;
        string title = card.Title;

        void Play() => HomeCardNav.Play(in c);
        // Shuffle STARTS the daylist shuffled; it never toggles (HomeCardNav.Play pauses a daylist that already plays).
        void Shuffle() { Playback.SetShuffle(true); Playback.PlayContext(c.Uri); }
        // The ▶ names what a press does: Pause while this daylist is the context playing (a subscribing, coarse-first read).
        bool pauses = Controls.ShowsPause(uri);

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

        // The clock's props freeze at mount, so a new window (or a new current daypart) remounts it through its key.
        var current = DaypartTimeline.Current(title, DateTime.Now.Hour);
        long expiresAtMs = card.ExpiresAtMs, createdAtMs = card.CreatedAtMs;
        var clock = Embed.Comp(() => new DaylistClock { ExpiresAtMs = expiresAtMs, CreatedAtMs = createdAtMs, Current = current })
            with { Key = "daylist-clock:" + uri + ":" + expiresAtMs.ToString(CultureInfo.InvariantCulture) + ":" + ((int)current).ToString(CultureInfo.InvariantCulture) };

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
        var root = Shape(new Parts(eyebrow, title, Tags(card), Strings.Home.DaylistMeta(card.TrackCount), actions, clock,
                                   art, veil))
            .Interactive(Interaction.Tile) with
            {
                OnClick = () => HomeCardNav.Open(in c), Cursor = CursorId.Hand,
                Role = AutomationRole.Button, Focusable = true,
            };
        // The playlist menu rides the card root: a right-click anywhere on the card and the "…" button (which climbs
        // to this handler through the context funnel) open the SAME menu, the button's anchored at itself.
        return menu is not null && host is not null ? root.WithContextMenu(host, menu) : root;
    }

    /// <summary>The cold-load shape: the SAME builders as <see cref="Render"/>, fed placeholder words, stand-in buttons
    /// and a static ring/timeline. Never early-outs — the seed zone's card is a blank (no uri).</summary>
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
        var line = DaylistClockBlock.Line((TextSpans)new TextSpan[] { new(Strings.Home.NextDaylistIn(DaypartRules.Countdown(0))) });
        // The caption's placeholder is the real rule read at "now": a caption-width line with the next daypart's word.
        string caption = DaylistNext.Caption(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), current,
                                             TimeZoneInfo.Local, CultureInfo.CurrentCulture);
        var clock = DaylistClockBlock.Shape(ProgressRing.Determinate(0f, Daylist.RingSize), line, caption, null, (int)current);
        // The art slot as its watched placeholder across the card (the real shape), and no veil: a component the
        // deriver cannot see into would shimmer as one stray bar.
        return Shape(new Parts(words, Loc.Get(Strings.Home.Layout.Daylist), TagLine(new TextSpan[] { new(words) }, null),
                               Strings.Home.DaylistMeta(50), actions, clock, ArtLayer(null), null));
    }

    /// <summary>The card: a Fluent card frame (border, 8-DIP corners) with NO padding — the art reaches its edges and
    /// the insets ride on the copy (<see cref="Daylist.CopyPadding"/>) — that clips its layers to its rounded corners
    /// (the border paints after the children, so it stays on top of the art), around the layers built at the card's
    /// width (<see cref="Row"/>; the copy's width follows <see cref="DaylistForm"/>).</summary>
    static BoxEl Shape(Parts parts)
        => Ui.Card(Responsive.Of(w => Row(parts, w), fallback: HomeModuleLayout.FallbackWidth, grow: 1f))
            with
            {
                Padding = default, ClipToBounds = true, MinHeight = Daylist.CardMinHeight,
                MinWidth = 0f, AlignSelf = FlexAlign.Stretch,
            };

    /// <summary>The art layer: <see cref="Controls.CoverFill"/> across the whole card, corner 0 (the card's rounded clip
    /// rounds it), focused a little above centre. A null <paramref name="url"/> is the watched placeholder slot (the
    /// skeleton's).</summary>
    static Element ArtLayer(string? url) => Controls.CoverFill(url, 0f, Daylist.ArtDecodePx, focusY: Daylist.ArtFocusY);

    /// <summary>The card's layers at card width <paramref name="w"/> (the card has no padding, so this is its whole
    /// width), in a ZStack: the art, its veil, then the copy over the veil's opaque side —
    /// <see cref="DaylistForm.CopyWidth"/> of the content width inside <see cref="Daylist.CopyPadding"/>, plus that
    /// padding. Neither layer measures anything, so the copy alone decides the card's height above its 320 floor, and
    /// the stack stretches both layers over the whole card and the copy over its full height (SpaceBetween pins the
    /// clock to the foot). The three slots never move — an absent layer is an empty, hit-test-free box — and the copy
    /// is keyed, so art arriving late mounts the layers without remounting the copy and its clock.</summary>
    static Element Row(Parts p, float w)
    {
        var pad = Daylist.CopyPadding;
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
            ? Design.Type.HeroTitle(title)
            : Design.Type.DetailHero(title) with { Weight = 600 };

    /// <summary>The ONE action row: 20 below the meta, 8 apart, never wrapping (the copy's floor fits it).</summary>
    static BoxEl ActionRow(params Element[] actions) => new()
    {
        Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, Wrap = false, MinWidth = 0f,
        Margin = new Edges4(0f, Spacing.XL, 0f, 0f),
        Children = actions,
    };

    /// <summary>"Good morning, Chris · your daylist" — a local hour-based greeting paired with the signed-in account's
    /// name (<see cref="Zone"/> carries no greeting field, so the server's own string never reaches this card). The
    /// page has no greeting headline: this eyebrow is the greeting.</summary>
    static string Eyebrow()
    {
        string part = DateTime.Now.Hour switch
        {
            >= 4 and < 12 => Loc.Get(Strings.Home.GoodMorning),
            >= 12 and < 17 => Loc.Get(Strings.Home.GoodAfternoon),
            _ => Loc.Get(Strings.Home.GoodEvening),
        };
        string? who = GreetedName();
        string yours = Loc.Get(Strings.Home.YourDaylist);
        return who is null ? part + " · " + yours : Strings.Home.HeroEyebrow(part, who, yours);
    }

    /// <summary>The signed-in account's greetable name, or null. A SUBSCRIBING read (the users table + the scope), so
    /// a late login re-describes the card.</summary>
    static string? GreetedName()
    {
        _ = Entities.ScopeEpoch.Value;
        _ = Entities.Current.Users.Changed.Value;
        var me = User.Me;
        if (!me.IsValid || !me.Knows(UserFields.Identity)) return null;
        string name = Entities.Strings.Resolve(me.NameId);
        return name.Length > 0 ? name : null;
    }

    /// <summary>The tags as ONE 14/20 line: every tag a secondary link span (the engine's Hand cursor over its own laid
    /// rects), " · " separators in tertiary, one line with an ellipsis. Clicks resolve by span INDEX
    /// (`OnSpanClick`): tags sit at the even indices, separators at the odd ones.</summary>
    static Element Tags(HomeCard card)
    {
        int seedCount = Math.Min(card.SeedCount, Daylist.MaxTags);
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
        return TagLine(spans, i => { if ((i & 1) == 0 && (i >> 1) < tags.Count) OpenTag(tags[i >> 1]); });
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

    static void OpenTag(string tag)
        => Shell.GoTo(new Shell.Route(Shell.RouteKind.Search, default, Entities.Strings.Intern(tag)));
}

/// <summary>The card's clock, in one of three faces (<see cref="DaylistClockFace"/>): the countdown while the window is
/// open (<see cref="DaylistCountingClock"/>); once it has ended and the next edition is on its way, a stock
/// indeterminate ring beside "Updating your daylist…" / "{Next daypart} is on its way" over the timeline with the
/// finished segment full and the next one an indeterminate bar; and, when the rollover ladder gave up (or never ran —
/// offline), a static ring beside "Your next daylist is running late" and a "Check again" link that restarts the ladder
/// (<see cref="Home.Feeds.CheckDaylistAgain"/>). This host reads the window's phase (a 1-Hz watch that only writes on the
/// flip, so a tick never re-renders it) and the ladder's signal, and keys the face by (window, face) so a face change
/// remounts only the clock's leaf. Props freeze at mount; the card keys this host on its window and daypart.</summary>
public sealed class DaylistClock : Component
{
    public required long ExpiresAtMs { get; init; }
    public required long CreatedAtMs { get; init; }
    public required Daypart Current { get; init; }

    readonly Signal<int> _flip = new(0);
    readonly Action _watch;

    public DaylistClock() => _watch = Watch;

    /// <summary>The 1-Hz watch while counting: the first tick past the window re-renders this host once.</summary>
    void Watch()
    {
        if (DaylistCountdown.PhaseOf(ExpiresAtMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) != DaylistCountdown.Phase.Counting)
            _flip.Value++;
    }

    public override Element Render()
    {
        _ = _flip.Value;                                           // the window ended: swap the face once
        var ladder = Home.Feeds.RolloverState.Value;               // the ladder moved: Updating ↔ Late
        long expiresAtMs = ExpiresAtMs, createdAtMs = CreatedAtMs;
        var phase = DaylistCountdown.PhaseOf(expiresAtMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        UseInterval(_watch, 1000f, enabled: phase == DaylistCountdown.Phase.Counting);
        if (expiresAtMs <= 0) return new BoxEl();

        var face = DaylistClockFace.Of(phase, ladder);
        var current = Current;
        string key = "face:" + expiresAtMs.ToString(CultureInfo.InvariantCulture) + ":" + ((int)face).ToString(CultureInfo.InvariantCulture);
        Element leaf = face switch
        {
            DaylistClockFace.Face.Updating => Updating(current),
            DaylistClockFace.Face.Late => Late(current),
            _ => Embed.Comp(() => new DaylistCountingClock { ExpiresAtMs = expiresAtMs, CreatedAtMs = createdAtMs, Current = current }),
        };
        // The face is the keyed CHILD of a stretch column (a root's own Key is inert in a component's single-child slot).
        return new BoxEl { Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch, Children = [leaf with { Key = key }] };
    }

    /// <summary>The next edition is on its way: the stock indeterminate ring at the countdown ring's size, the
    /// card's own "Updating your daylist…" line, then "{Next daypart} is on its way" (the segment AFTER the card's
    /// current one, the countdown caption's rule), over the timeline whose next segment is an indeterminate bar.</summary>
    static Element Updating(Daypart current)
    {
        string next = Loc.Get(DaypartTimeline.LabelKey((int)DaypartRules.Next(current)));
        return DaylistClockBlock.Shape(ProgressRing.Indeterminate(Daylist.RingSize), StaticLine(Loc.Get(Strings.Home.DaylistUpdating)),
                                       Strings.Home.Daylist.OnItsWay(next), null, (int)current, DaylistClockFace.Face.Updating);
    }

    /// <summary>The ladder ran out: a full, static ring in tertiary ink, the "running late" line and the "Check again"
    /// link (small, left-aligned in the lines column) that restarts the ladder.</summary>
    static Element Late(Daypart current)
    {
        var check = HyperlinkButton.Create(Loc.Get(Strings.Home.Daylist.CheckAgain), Home.Feeds.CheckDaylistAgain,
                                           size: ControlSize.Small) with { AlignSelf = FlexAlign.Start, BlocksDragArm = true };
        return DaylistClockBlock.Shape(ProgressRing.Determinate(1f, Daylist.RingSize, foreground: Tok.TextTertiary, track: Tok.StrokeControlStrongDefault),
                                       StaticLine(Loc.Get(Strings.Home.Daylist.Late)), "", null, (int)current, DaylistClockFace.Face.Late, check);
    }

    /// <summary>The first line of a face with a fixed sentence (no per-second count to bind).</summary>
    static SpanTextEl StaticLine(string text) => DaylistClockBlock.Line((TextSpans)new TextSpan[] { new(text) });
}

/// <summary>The countdown face: a stock determinate <see cref="ProgressRing"/> beside two lines — "Next daylist in
/// <b>hh:mm:ss</b>" and, under it, the caption "{weekday} {next daypart} arrives at HH:mm" (<see cref="DaylistNext"/>)
/// — over the five-segment daypart timeline (five stock determinate <see cref="ProgressBar"/>s over their labels).
/// A 1-Hz <c>UseInterval</c> writes caller-owned signals — the countdown text, the elapsed fraction, one fill per
/// segment — and every one of them reaches the screen through a BIND (the first line's bound spans, the ring, the bars'
/// indicator widths): <see cref="Render"/> reads none of them, so neither this leaf nor anything above it re-renders on
/// the tick. The caption is static per mount. Props freeze at mount; <see cref="DaylistClock"/> keys this leaf on its
/// window, so a new window remounts it (and re-reads the caption).</summary>
public sealed class DaylistCountingClock : Component
{
    public required long ExpiresAtMs { get; init; }
    public required long CreatedAtMs { get; init; }
    public required Daypart Current { get; init; }

    public override Element Render()
    {
        var buf = UseRef(new char[16]).Value;
        var countdown = UseRef(new Signal<string>("")).Value;
        var elapsed = UseRef(new FloatSignal(0f)).Value;
        var segs = UseRef(NewSegments()).Value;
        var spans = UseRef(new SpanBuffer()).Value;

        long expiresAtMs = ExpiresAtMs, createdAtMs = CreatedAtMs;
        int current = (int)Current;
        void Tick()
        {
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            int len = DaypartRules.FormatCountdown(DaylistCountdown.RemainingMs(expiresAtMs, nowMs), buf);
            countdown.Value = new string(buf, 0, len);
            float e = (float)DaypartRules.Elapsed(createdAtMs, expiresAtMs, nowMs);
            elapsed.Value = e;
            for (int i = 0; i < segs.Length; i++) segs[i].Value = DaypartTimeline.Fill(i, current, e);
        }

        UseEffect(() => Tick());                                   // seed immediately (mount)
        UseInterval(Tick, 1000f, enabled: ExpiresAtMs > 0);
        if (ExpiresAtMs <= 0) return new BoxEl();

        // The sentence stays the translator's; only the count is cut out of it to be its own semibold span.
        var (before, after) = DaylistCountdownLine.Split(Strings.Home.NextDaylistIn(DaylistCountdownLine.Slot));
        // The second line names the timeline segment AFTER the card's current one; static per mount.
        string caption = DaylistNext.Caption(expiresAtMs, Current, TimeZoneInfo.Local, CultureInfo.CurrentCulture);

        // The bound line: one fill of the reused buffer per tick (the scene copies it), a relayout of this one run.
        var line = DaylistClockBlock.Line(Prop.Of(() =>
        {
            string count = countdown.Value;                        // subscribe the bind → the per-second update
            spans.Clear();
            if (before.Length > 0) spans.Add(new TextSpan(before));
            spans.Add(new TextSpan(count, Weight: 600));
            if (after.Length > 0) spans.Add(new TextSpan(after));
            return spans.Current;
        }));
        var ring = ProgressRing.Create(elapsed, Daylist.RingSize, track: Tok.StrokeControlStrongDefault);
        return DaylistClockBlock.Shape(ring, line, caption, segs, current);
    }

    static FloatSignal[] NewSegments()
    {
        var segs = new FloatSignal[DaypartTimeline.Segments];
        for (int i = 0; i < segs.Length; i++) segs[i] = new FloatSignal(0f);
        return segs;
    }
}

/// <summary>The clock block every face (and the card's skeleton) is built from: [ring · (line / second line)] over the
/// five-segment timeline.</summary>
internal static class DaylistClockBlock
{
    /// <summary>The countdown line's paragraph: body 14/20, primary, one line; it stretches to the lines column's width
    /// and ellipsises only when the column is narrower than the sentence (the ring never gives way).</summary>
    internal static SpanTextEl Line(Prop<TextSpans> spans)
    {
        var body = Ui.Body("");
        return new SpanTextEl(spans)
        {
            Size = body.Size, LineHeight = body.LineHeight, Color = Tok.TextPrimary,
            Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
        };
    }

    /// <summary>The clock block — shared by every face and the card's skeleton: [ring · (line / second line)], then the
    /// timeline at the block's measured width. The ring is centred against the two-line column (the Fluent
    /// settings-card stack: a 16–20 icon beside a 14/20 header over a 12/16 description). The second line is
    /// <paramref name="action"/> when given (the Late face's link), else the <paramref name="caption"/>; an empty
    /// caption leaves the first line alone. <paramref name="segs"/> null = static bars: empty for the Counting face (the
    /// skeleton), the finished segments full for Updating/Late — Updating's next segment an indeterminate bar.</summary>
    internal static Element Shape(Element ring, SpanTextEl line, string caption, FloatSignal[]? segs, int current,
                                  DaylistClockFace.Face face = DaylistClockFace.Face.Counting, Element? action = null) => new BoxEl
    {
        Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch,
        Children =
        [
            new BoxEl
            {
                Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, MinWidth = 0f,
                Children =
                [
                    ring,
                    // Grow 1 / Basis 0: the column takes exactly what the ring leaves, so both lines ellipsise at it.
                    new BoxEl
                    {
                        Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, AlignItems = FlexAlign.Stretch,
                        Children = Lines(line, caption, action),
                    },
                ],
            },
            Responsive.Of(w => Timeline(w, segs, current, face), fallback: DaylistForm.TextMin),
        ],
    };

    /// <summary>The lines column's children: the first line alone, or under it the action (the Late face's link) or the caption.</summary>
    static Element[] Lines(SpanTextEl line, string caption, Element? action)
    {
        if (action is not null) return [line, action];
        return caption.Length > 0 ? [line, CaptionLine(caption)] : [line];
    }

    /// <summary>The second line: "{weekday} {next daypart} arrives at HH:mm" at the caption rung (12/16), secondary,
    /// one line with an ellipsis.</summary>
    static TextEl CaptionLine(string caption)
        => Ui.Caption(caption).Secondary() with
        {
            MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
        };

    /// <summary>The timeline at width <paramref name="w"/>: five equal cells (<see cref="DaypartTimeline.CellWidth"/>)
    /// of stock bars, and the labels on the same cells — tertiary and ellipsised, except the current one, primary 600,
    /// which holds at least its cell and never truncates (its neighbours give way instead). The bars are determinate
    /// (live per <paramref name="segs"/>, or static: empty for the skeleton, the finished window's fills once it has
    /// ended); Updating makes the segment after the current one an indeterminate bar.</summary>
    static Element Timeline(float w, FloatSignal[]? segs, int current, DaylistClockFace.Face face)
    {
        float cell = DaypartTimeline.CellWidth(w);
        int next = (int)DaypartRules.Next((Daypart)current);
        var bars = new Element[DaypartTimeline.Segments];
        var labels = new Element[DaypartTimeline.Segments];
        for (int i = 0; i < DaypartTimeline.Segments; i++)
        {
            bars[i] = face == DaylistClockFace.Face.Updating && i == next ? ProgressBar.Indeterminate(cell)
                : segs is not null ? ProgressBar.Create(segs[i], cell)
                : ProgressBar.Determinate(face == DaylistClockFace.Face.Counting ? 0f : DaypartTimeline.Fill(i, current, 1f), cell);
            string label = Loc.Get(DaypartTimeline.LabelKey(i));
            labels[i] = i == current
                ? Ui.Caption(label).Primary() with { Weight = 600, MaxLines = 1, Wrap = TextWrap.NoWrap, MinWidth = cell, Shrink = 0f }
                : Ui.Caption(label).Tertiary() with
                {
                    MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis,
                    Basis = cell, Shrink = 1f, MinWidth = 0f,
                };
        }
        return new BoxEl
        {
            Direction = 1, MinWidth = 0f,
            Children =
            [
                new BoxEl
                {
                    Direction = 0, Gap = DaypartTimeline.Gap, AlignItems = FlexAlign.Center, MinWidth = 0f,
                    Margin = new Edges4(0f, Spacing.S + Spacing.XXS, 0f, 0f), Children = bars,
                },
                new BoxEl
                {
                    Direction = 0, Gap = DaypartTimeline.Gap, MinWidth = 0f,
                    Margin = new Edges4(0f, Spacing.XS + Spacing.XXS, 0f, 0f), Children = labels,
                },
            ],
        };
    }
}
