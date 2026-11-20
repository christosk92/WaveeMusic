// ── Themes/Common/RateNudgeCard.cs — the rate & star card, one picture for every theme (rate-nudge-implementation.md §5.3) ─────────────
//
// `CommonBlocks.Register` registers it under `ViewKeys.ShellRateNudge` for Classic, WinUI and Zune. The core layer (`RateNudge.Chrome()`, mounted
// last in the overlay lane) decides when it shows, resolves its words and hands it the verbs; this only draws what `View` carries.
//
//   lane     a full-bleed, hit-test pass-through ZStack: nothing in the page moves when the card comes or goes, and the page under the
//            lane's empty area keeps every click.
//   card     `rate-nudge`: bottom right, the toast lane's side margin in from the window's edge and `AboveBar` above the player bar (the bar's
//            height as the toast lane reads it: the band's own bar where the overlay policy names one, else the player bar policy's). It
//            hugs its words up to `Width` and never grows past the window. It MOUNTS to show and unmounts to leave, so the exit plays
//            (a bound Visible would collapse with no exit): a 12-DIP rise with a fade, the same curve and time both ways; reduced
//            motion keeps the fade alone. It never takes focus; the pointer over it or keyboard focus on one of its buttons holds its
//            clock (`Hold`), so it never tucks away under a reader.
//
//   ├─ row     the app's mark (the About hero's accent tile and music note, `rate-nudge:icon`) beside the words: the milestone's big line
//   │          and its caption (`rate-nudge:big`), the title (`:title`), the body (`:body`), the star count (`:meta`, a GitHub card only)
//   └─ actions the primary (accent, the Store's bag or a star, `:primary`), Not now (`:later`), and Don't ask again pushed to the end as a
//              link (`:never`). A narrow card wraps the link onto its own line. At Compact every target is 40 DIP (`CommonTouch`).

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Rules = Wavee.Themes.Common.RateNudgeRules;

namespace Wavee.Themes.Common;

/// <summary>The rate &amp; star card (see the file header).</summary>
internal static class RateNudgeCard
{
    /// <summary>The registered template of <see cref="ViewKeys.ShellRateNudge"/>.</summary>
    public static Element Template(RateNudgeCardProps p, in ViewContext ctx)
    {
        // The pointer and keyboard focus are tracked apart and handed to the core as one "held" edge, once per change (OnHoverMove fires
        // per move: the ref keeps that from reaching the core more than once).
        var hovered = ctx.Hooks.UseRef(false);
        var focused = ctx.Hooks.UseRef(false);
        var held = ctx.Hooks.UseRef(false);
        var factsSignal = ctx.Hooks.UseContext(ThemeFactsContext.Signal);
        ThemeFacts facts = factsSignal?.Value ?? ThemeFacts.Default;     // a tier crossing re-docks the card with the bar
        RateNudgeView? view = p.View.Value;                              // a show or a leave re-renders: the card mounts or unmounts

        if (view is null)
        {
            // A card that left under the pointer gets no pointer-exit (its node is gone): the next card starts untouched.
            hovered.Value = focused.Value = held.Value = false;
            return Lane([]);
        }

        Action<bool> hold = p.Hold;
        void Sync()
        {
            bool on = hovered.Value || focused.Value;
            if (on == held.Value) return;
            held.Value = on;
            hold(on);
        }
        void Focus(bool on) { focused.Value = on; Sync(); }

        var dock = facts.Policies.Overlays.For(TierContext.WindowOf(facts.Tiers)).Toast;
        float side = (dock.Lane ?? ToastLaneStyle.Default).SideMargin;
        float bottom = (dock.BarHeight ?? facts.Policies.PlayerBar.Height) + Rules.AboveBar;
        float target = CommonTiers.Target(TierContext.WindowOf(facts.Tiers));
        EnterExit motion = ctx.Reduced ? Rules.Fade : Rules.FadeUp;

        var card = new BoxEl
        {
            Key = Rules.Key, Direction = 1, Gap = Spacing.M, MinWidth = 0f, MaxWidth = Rules.Width,
            AlignSelf = FlexAlign.End, JustifySelf = FlexAlign.End,
            Margin = new Edges4(0f, 0f, side, bottom),
            Padding = Edges4.All(Spacing.L),
            Fill = Tok.FillSolidTertiary, BorderWidth = 1f, BorderColor = Tok.StrokeSurfaceDefault,
            Corners = R.Shape.All(ShapeRole.Overlay), Shadow = Elevation.Flyout,
            Role = AutomationRole.InfoBar,
            Enter = motion, Exit = motion, Transition = Rules.Motion,
            OnHoverMove = _ => { if (hovered.Value) return; hovered.Value = true; Sync(); },
            OnPointerExit = () => { hovered.Value = false; Sync(); },
            Children =
            [
                new BoxEl
                {
                    Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Start, MinWidth = 0f,
                    Children = [Mark(), Words(view)],
                },
                Actions(p, view, target, Focus),
            ],
        };
        return Lane([card]);
    }

    /// <summary>The full-bleed lane the card floats in (the overlay lane's last child: it takes what is left of the window, and its empty area
    /// passes every pointer through to the page). With no card it is out of hit-testing altogether, as the engine's dormant toast lane is.</summary>
    static BoxEl Lane(Element[] children) => new()
    {
        ZStack = true, Grow = 1f, Basis = 0f, MinHeight = 0f, AlignSelf = FlexAlign.Stretch,
        HitTestPassThrough = true, HitTestVisible = children.Length > 0,
        Children = children,
    };

    /// <summary>The app's mark: the About hero's accent tile with the music note, at the card's scale.</summary>
    static BoxEl Mark() => new()
    {
        Key = Rules.IconKey, Width = Rules.Icon, Height = Rules.Icon, Shrink = 0f,
        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Corners = R.Shape.All(ShapeRole.Control), Fill = Tok.AccentSubtle,
        Children = [Ui.Icon(Icons.MusicNote, Rules.IconGlyph, Tok.AccentTextPrimary)],
    };

    /// <summary>The words: the milestone line (when the ask is a milestone's), the title, the body, the star count (a GitHub card with a count).</summary>
    static BoxEl Words(RateNudgeView view)
    {
        var lines = new List<Element>(4);
        if (view.Big is { } big)
        {
            var milestone = new List<Element>(2) { R.Type.StatHero(big, null) with { Wrap = TextWrap.Wrap } };
            if (view.BigCaption is { } caption) milestone.Add(Secondary(R.Type.DenseMeta(caption)));
            lines.Add(new BoxEl { Key = Rules.BigKey, Direction = 1, MinWidth = 0f, Children = [.. milestone] });
        }
        lines.Add(R.Type.TrackTitle(view.Title) with
        {
            Key = Rules.TitleKey, Wrap = TextWrap.Wrap, MaxLines = 0, Trim = TextTrim.None, Shrink = 1f, MinWidth = 0f,
            Case = R.Case.For(LabelKind.Heading),
        });
        lines.Add(Secondary(R.Type.DenseMeta(view.Body)) with { Key = Rules.BodyKey });
        if (view.Meta is { } meta)
        {
            lines.Add(new BoxEl
            {
                Key = Rules.MetaKey, Direction = 0, Gap = Spacing.XS, AlignItems = FlexAlign.Center, MinWidth = 0f,
                Children = [Ui.Icon(Icons.FavoriteStar, Rules.MetaGlyph, Tok.TextSecondary), Secondary(R.Type.MicroMeta(meta))],
            });
        }
        return new BoxEl { Direction = 1, Gap = Spacing.XS, Shrink = 1f, MinWidth = 0f, Children = [.. lines] };
    }

    /// <summary>A caption line in the secondary ink that wraps (never trims: every word of the ask is read).</summary>
    static TextEl Secondary(TextEl text)
        => text with { Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxLines = 0, Trim = TextTrim.None, Shrink = 1f, MinWidth = 0f };

    /// <summary>The buttons: the ask itself, Not now, and Don't ask again pushed to the end as a link. Focus on any of them holds the card.</summary>
    static BoxEl Actions(RateNudgeCardProps p, RateNudgeView view, float target, Action<bool> focus)
    {
        string glyph = view.Glyph == RateNudgeGlyph.Star ? Icons.FavoriteStar : Rules.StoreGlyph;
        return new BoxEl
        {
            Direction = 0, Wrap = true, Gap = Spacing.S, AlignItems = FlexAlign.Center, MinWidth = 0f,
            Children =
            [
                CommonTouch.Button(view.Primary, p.Primary, target, accent: true, glyph: glyph) with { Key = Rules.PrimaryKey, OnFocusChanged = focus },
                CommonTouch.Button(view.Later, p.Later, target) with { Key = Rules.LaterKey, OnFocusChanged = focus },
                new BoxEl { Grow = 1f, MinWidth = 0f, HitTestVisible = false },
                CommonTouch.Link(view.Never, p.Never, target) with { Key = Rules.NeverKey, OnFocusChanged = focus },
            ],
        };
    }
}
