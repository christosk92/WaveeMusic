// ── Home/Daylist.UI.cs — the daylist "play-now" card over stock controls (home rebuild, fifth → seventh pass) ───────
//
// Role: UI
// Spec: docs/plans/wavee/home-rebuild-implementation.md "Fifth pass — stock controls" (zone → control table, Daylist row)
//        + "Seventh pass" (the Daylist card wireframe: text left, cover-cropped art right, the daypart timeline).
//
// One `Ui.Card` (padding 32/12/12/12, at least 320 tall) around a `Responsive.Of` row, so the card's own content width
// picks its form through the pure `DaylistForm` rule: beside the art the text column holds `Daylist.TextMinWidth`,
// the art takes the rest up to its 540 basis (it shrinks, the text never does), and once less than `DaylistForm.ArtMin`
// of art would be left the card is the text column alone.
//   text column (Grow 1, SpaceBetween, padding 0/20/0/12)
//     top:    eyebrow (body, secondary: the greeting lives ONLY here) · `Design.Type.HeroTitle` (≤ 2 lines) ·
//             tags (ONE 14/20 `SpanTextEl` of link spans, a tag opens Search) · meta (caption, tertiary) ·
//             ONE action row: stock Accent Play + Standard Shuffle + `Controls.SaveButton` + `Controls.MoreButton`
//     bottom: `DaylistClock` — a stock determinate `ProgressRing` + "Next daylist in **hh:mm:ss** · {next} arrives at
//             HH:mm", then five stock determinate `ProgressBar`s (done / current / future, `DaypartTimeline.Fill`)
//             over their five daypart labels (the current one primary 600, never truncated)
//   art column (Basis 540, Shrink 1, stretch): `Controls.CoverFill` of `DaylistArt.Of`, focused a little above centre.
//
// The "…" button re-enters the engine's context funnel (`ClickRequestsContext`) and finds the card's ATTACHED playlist
// menu (`HomeCardNav.MenuOf`, the grammar every other card wears) — attaching it needs the overlay service, which the
// caller hands in. What must SUBSCRIBE (the greeted name) is read in `DaylistCard.Render`; the saved heart is the stock
// `SaveButton`, which subscribes on its own. The 1-Hz tick lives in `DaylistClock` and reaches the screen through BINDS
// only (the countdown spans, the ring, the five bars): no component re-renders on the tick. The cold-load skeleton is
// the SAME builders fed placeholders (`Daylist.Card`'s `SkeletonProxy`), so it shimmers as this card's real shape.
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
    /// <summary>The text column's floor beside the art: the 40/52 hero title's line plus the ONE action row (Play at
    /// its primary floor, Shuffle, the heart and "…") fit without wrapping. Below it the art goes, not the text
    /// (<see cref="DaylistForm"/>).</summary>
    public const float TextMinWidth = DaylistForm.TextMin;
    /// <summary>The art column's flex basis — its width on a wide card; a narrower card shrinks the art, never the text.</summary>
    public const float ArtBasis = 540f;
    /// <summary>The decode target for the art (a 540-wide header image decodes near its own size, never a 256 square).</summary>
    public const int ArtDecodePx = 512;
    /// <summary>The art's focal line: a little above centre, where a daylist header's subject sits.</summary>
    public const float ArtFocusY = 0.4f;
    /// <summary>The card's floor height (the prototype's 320 hero).</summary>
    public const float CardMinHeight = 320f;
    /// <summary>The card's padding — a wide left inset for the text, the art near-flush to the other three edges.</summary>
    public static readonly Edges4 CardPadding = new(Spacing.XXXL, Spacing.M, Spacing.M, Spacing.M);
    /// <summary>The text column's own inset: the eyebrow sits 20 below the card's edge, the timeline labels 12 above it.</summary>
    public static readonly Edges4 TextPadding = new(0f, Spacing.XL, 0f, Spacing.M);
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
    /// <see cref="Shape"/> at whatever width the card measures.</summary>
    readonly record struct Parts(string Eyebrow, string Title, Element Tags, string Meta, Element Actions, Element Clock, string? ArtUrl);

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
        void Shuffle() { Playback.SetShuffle(true); HomeCardNav.Play(in c); }

        var actions = ActionRow(
            Button.Create(Loc.Get(Strings.Home.Play), Play, ButtonAppearance.Accent, glyph: Icons.Play)
                with { MinWidth = Controls.PrimaryMinWidth, Shrink = 0f },
            Button.Create(Loc.Get(Strings.Detail.Shuffle), Shuffle, ButtonAppearance.Standard, glyph: Icons.Shuffle)
                with { Shrink = 0f },
            Controls.Named(Embed.Comp(() => new Controls.SaveButton { Uri = uri, Name = title }) with { Key = "save:" + uri },
                           Loc.Get(Strings.Home.Save)),
            Controls.Named(Controls.MoreButton(null, requestsContext: true), Loc.Get(Strings.Home.More)));

        // The clock's props freeze at mount, so a new window (or a new current daypart) remounts it through its key.
        var current = DaypartTimeline.Current(title, DateTime.Now.Hour);
        long expiresAtMs = card.ExpiresAtMs, createdAtMs = card.CreatedAtMs;
        var clock = Embed.Comp(() => new DaylistClock { ExpiresAtMs = expiresAtMs, CreatedAtMs = createdAtMs, Current = current })
            with { Key = "daylist-clock:" + uri + ":" + expiresAtMs.ToString(CultureInfo.InvariantCulture) + ":" + ((int)current).ToString(CultureInfo.InvariantCulture) };

        var root = Shape(new Parts(eyebrow, title, Tags(card), Strings.Home.DaylistMeta(card.TrackCount), actions, clock,
                                   DaylistArt.Of(card.HeaderImageUrl, card.ImageUrl)));
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
            Controls.MoreButton(null, requestsContext: false),
            Controls.MoreButton(null, requestsContext: false));
        var line = DaylistClock.Line((TextSpans)new TextSpan[] { new(Strings.Home.NextDaylistIn(DaypartRules.Countdown(0))) });
        var clock = DaylistClock.Shape(ProgressRing.Determinate(0f, Daylist.RingSize), line, null,
                                       (int)DaypartRules.OfHour(DateTime.Now.Hour));
        return Shape(new Parts(words, Loc.Get(Strings.Home.Layout.Daylist), TagLine(new TextSpan[] { new(words) }, null),
                               Strings.Home.DaylistMeta(50), actions, clock, null));
    }

    /// <summary>The card: a Fluent card whose content width decides the form (<see cref="DaylistForm"/>).</summary>
    static BoxEl Shape(Parts parts)
        => Ui.Card(Responsive.Of(w => Row(parts, w), fallback: HomeModuleLayout.FallbackWidth, grow: 1f))
            with
            {
                Padding = Daylist.CardPadding, MinHeight = Daylist.CardMinHeight,
                MinWidth = 0f, AlignSelf = FlexAlign.Stretch,
            };

    /// <summary>The card's row at content width <paramref name="w"/>: the text column, and the art beside it while
    /// <see cref="DaylistForm.ShowArt"/> holds. The text never shrinks (Shrink 0) — a narrow card takes it out of the
    /// art, whose basis is its wide width and whose floor is 0.</summary>
    static Element Row(Parts p, float w)
    {
        var top = new BoxEl
        {
            Direction = 1, MinWidth = 0f,
            Children =
            [
                Ui.Body(p.Eyebrow).Secondary() with { MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                Design.Type.HeroTitle(p.Title) with
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

        var text = new BoxEl
        {
            Direction = 1, Grow = 1f, Shrink = 0f, Basis = 0f, MinWidth = DaylistForm.TextMinFor(w),
            // SpaceBetween pins the clock to the card's foot; the Gap is its floor when a two-line title fills the card.
            Justify = FlexJustify.SpaceBetween, Padding = Daylist.TextPadding, Gap = Spacing.XL,
            Children = [top, p.Clock],
        };

        if (!DaylistForm.ShowArt(w))
            return new BoxEl { Direction = 0, Grow = 1f, MinWidth = 0f, AlignItems = FlexAlign.Stretch, Children = [text] };

        // A column box, so the cover-fill image grows to the column's full height; the row stretches it to the card's.
        var art = new BoxEl
        {
            Direction = 1, AlignItems = FlexAlign.Stretch, AlignSelf = FlexAlign.Stretch,
            Grow = 0f, Shrink = 1f, Basis = Daylist.ArtBasis, MinWidth = 0f,
            Children = [Controls.CoverFill(p.ArtUrl, Radii.Control, Daylist.ArtDecodePx, focusY: Daylist.ArtFocusY)],
        };
        return new BoxEl
        {
            Direction = 0, Gap = Spacing.XXXL, Grow = 1f, MinWidth = 0f, AlignItems = FlexAlign.Stretch,
            Children = [text, art],
        };
    }

    /// <summary>The ONE action row: 20 below the meta, 8 apart, never wrapping (the text column's floor fits it).</summary>
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

/// <summary>The card's clock: a stock determinate <see cref="ProgressRing"/> beside "Next daylist in <b>hh:mm:ss</b> ·
/// {weekday} {daypart} arrives at HH:mm", over the five-segment daypart timeline (five stock determinate
/// <see cref="ProgressBar"/>s over their labels). A 1-Hz <c>UseInterval</c> writes caller-owned signals — the countdown
/// text, the elapsed fraction, one fill per segment — and every one of them reaches the screen through a BIND (the
/// line's bound spans, the ring, the bars' indicator widths): <see cref="Render"/> reads none of them, so neither this
/// leaf nor anything above it re-renders on the tick. Props freeze at mount; the card keys this leaf on its window and
/// daypart, so a new window remounts it.</summary>
public sealed class DaylistClock : Component
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
        string arrives = DaylistNext.Caption(expiresAtMs, TimeZoneInfo.Local, CultureInfo.CurrentCulture);
        string tail = arrives.Length > 0 ? " · " + arrives : "";

        // The bound line: one fill of the reused buffer per tick (the scene copies it), a relayout of this one run.
        var line = Line(Prop.Of(() =>
        {
            string count = countdown.Value;                        // subscribe the bind → the per-second update
            spans.Clear();
            if (before.Length > 0) spans.Add(new TextSpan(before));
            spans.Add(new TextSpan(count, Weight: 600));
            if (after.Length > 0) spans.Add(new TextSpan(after));
            if (tail.Length > 0) spans.Add(new TextSpan(tail, Color: Tok.TextSecondary));
            return spans.Current;
        }));
        var ring = ProgressRing.Create(elapsed, Daylist.RingSize, track: Tok.StrokeControlStrongDefault);
        return Shape(ring, line, segs, current);
    }

    static FloatSignal[] NewSegments()
    {
        var segs = new FloatSignal[DaypartTimeline.Segments];
        for (int i = 0; i < segs.Length; i++) segs[i] = new FloatSignal(0f);
        return segs;
    }

    /// <summary>The countdown line's paragraph: body 14/20, primary, one line, ellipsised before the ring ever is.</summary>
    internal static SpanTextEl Line(Prop<TextSpans> spans)
    {
        var body = Ui.Body("");
        return new SpanTextEl(spans)
        {
            Size = body.Size, LineHeight = body.LineHeight, Color = Tok.TextPrimary,
            Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f,
        };
    }

    /// <summary>The clock block — shared by <see cref="Render"/> and the card's skeleton: [ring · line], then the
    /// timeline at the block's measured width. <paramref name="segs"/> null = static empty bars (the skeleton).</summary>
    internal static Element Shape(Element ring, SpanTextEl line, FloatSignal[]? segs, int current) => new BoxEl
    {
        Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch,
        Children =
        [
            new BoxEl { Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, MinWidth = 0f, Children = [ring, line] },
            Responsive.Of(w => Timeline(w, segs, current), fallback: DaylistForm.TextMin),
        ],
    };

    /// <summary>The timeline at width <paramref name="w"/>: five equal cells (<see cref="DaypartTimeline.CellWidth"/>)
    /// of stock determinate bars, and the labels on the same cells — tertiary and ellipsised, except the current one,
    /// primary 600, which holds at least its cell and never truncates (its neighbours give way instead).</summary>
    static Element Timeline(float w, FloatSignal[]? segs, int current)
    {
        float cell = DaypartTimeline.CellWidth(w);
        var bars = new Element[DaypartTimeline.Segments];
        var labels = new Element[DaypartTimeline.Segments];
        for (int i = 0; i < DaypartTimeline.Segments; i++)
        {
            bars[i] = segs is null ? ProgressBar.Determinate(0f, cell) : ProgressBar.Create(segs[i], cell);
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
