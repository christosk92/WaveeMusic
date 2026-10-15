// ── Themes/Classic/Structure/Views/Detail/ClassicPlaySplit.cs — the detail page's Play split and its action row (tuned Classic D1, D2, D20) ──
//
// THE ONE HEADER PLAY (D20): every detail header (the rail, the vertical hero, the show header) leads with ONE accent control,
// [Play 88 │ ⌄ 32]: the page accent IS the fill (`Controls.AccentFillPalette`, with its contrast floor, D13), one elevation border
// round the whole control, an opaque divider (a 20-DIP hairline of the label ink, never a gap), a 12-DIP chevron, and the menu
// "More ways to play" (Add to queue · Play next · Start radio, `DetailVerbs.PlayMenu`). The label follows playback (Play ▶ /
// Pause ⏸ while this subject is the one playing), so a flip re-renders THIS control and not the rail around it.
//
// The halves are stock accent buttons (focus ring, Space/Enter, the 83 ms brush ramp) with their own border dropped; the joined
// border is an overlay above both, so the halves' fills never cover it. Down / F4 on the control opens the menu, as WinUI's
// SplitButton does.
//
// THE ACTION ROW (D1): [split] 12 [Shuffle] 8 [♥] 8 [Share] 8 [⋯], one wrap, 8 apart both ways. Shuffle is the bordered standard
// button (the board's control fill and stroke), the heart is quiet (no plate), Share and ⋯ are the plain 32 icon buttons. The heart
// is the page's (Save for an album, Follow / Unfollow for a playlist; an owned playlist has none, `Config.Heart`).

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;
using RailLayout = Wavee.Detail.RailLayout;

namespace Wavee.Themes.Classic;

internal static partial class ClassicDetailKit
{
    // ══ THE PLAY SPLIT ══════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The detail header's Play split for <paramref name="subject"/>: <paramref name="play"/> on the left half, the "More
    /// ways to play" menu on the ⌄. <paramref name="accent"/> is read every render (a palette that lands later re-tints it).
    /// Keyed on the subject: the props freeze at mount.</summary>
    public static Element PlaySplit(EntityUri subject, Func<ColorF> accent, Action play)
        => Embed.Comp(() => new PlaySplitHost { Subject = subject, Accent = accent, Play = play })
            with { Key = "ctxplay:" + subject.Text };

    sealed class PlaySplitHost : Component
    {
        public required EntityUri Subject { get; init; }
        public required Func<ColorF> Accent { get; init; }
        public required Action Play { get; init; }

        readonly Signal<bool> _open = new(false);
        IOverlayService? _overlay;
        NodeHandle _anchor;
        OverlayHandle? _menu;
        Action? _toggle;
        Action<NodeHandle>? _capture;
        Action<KeyEventArgs>? _keys;

        public override Element Render()
        {
            _overlay = UseContext(Overlay.Service);
            _toggle ??= ToggleMenu;
            _capture ??= h => _anchor = h;
            _keys ??= OnKey;

            ColorF fill = Accent();
            ColorF ink = ColorContrast.PickContrast(fill);
            bool pause = PlayRelation.ShowsPause(Subject.Text);
            bool open = _open.Value;
            var palette = Controls.AccentFillPalette(fill);
            float r = Radii.Control;

            BoxEl playHalf = Controls.PrimaryButton(Loc.Get(pause ? Strings.Home.Pause : Strings.Detail.Play), Play, fill,
                pause ? Icons.Pause : Icons.Play) with
            {
                Key = "split:play", MinWidth = RailLayout.PlayHalf, Shrink = 0f,
                Corners = new CornerRadius4(r, 0f, 0f, r),
                BorderWidth = 0f, BorderBrush = null, HoverBorderBrush = null, PressedBorderBrush = null,
            };

            BoxEl menuHalf = Button.Create("", _toggle, ButtonAppearance.Accent,
                style: Button.DefaultStyle(ButtonAppearance.Accent, palette: palette) with { MinHeight = Controls.ButtonHeight }) with
            {
                Key = "split:menu", Width = RailLayout.MenuHalf, MinWidth = 0f, Shrink = 0f, Padding = default, Gap = 0f,
                Justify = FlexJustify.Center, AlignItems = FlexAlign.Center,
                Corners = new CornerRadius4(0f, r, r, 0f),
                BorderWidth = 0f, BorderBrush = null, HoverBorderBrush = null, PressedBorderBrush = null,
                // The menu's own open state holds the pressed fill, as the stock SplitButton's half does.
                Fill = open ? palette.Background.Pressed : fill,
                Children =
                [
                    Icon(Icons.ChevronDown, ClassicDetailKitLayout.SplitChevronGlyph, ink with { A = ClassicDetailKitLayout.SplitChevronInk }) with
                    {
                        PressedColor = ink with { A = Controls.OnFillSecondaryAlpha(ink) },
                    },
                ],
            };

            // The divider sits ON the accent: a full-height strip of the fill with the hairline centred in it.
            Element divider = new BoxEl
            {
                Width = RailLayout.SplitDivider, Height = Controls.ButtonHeight, Shrink = 0f, Fill = fill,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Children =
                [
                    new BoxEl
                    {
                        Width = RailLayout.SplitDivider, Height = ClassicDetailKitLayout.SplitDividerHeight,
                        Fill = ink with { A = ClassicDetailKitLayout.SplitDividerInk },
                    },
                ],
            };

            return new BoxEl
            {
                ZStack = true, Shrink = 0f, Height = Controls.ButtonHeight,
                Margin = new Edges4(0f, 0f, RailLayout.SplitTrail, 0f),
                OnRealized = _capture, OnKeyDown = _keys,
                Children =
                [
                    new BoxEl
                    {
                        Key = "split:halves", Direction = 0, AlignItems = FlexAlign.Center, Height = Controls.ButtonHeight,
                        Corners = Radii.ControlAll, ClipToBounds = true,
                        Children = [playHalf, divider, ToolTip.Wrap(menuHalf, Loc.Get(Strings.Detail.MoreWaysToPlay))],
                    },
                    // ONE elevation border round the whole control, above both halves (D2: no second outline on the chevron).
                    new BoxEl
                    {
                        Key = "split:border", HitTestVisible = false, Corners = Radii.ControlAll,
                        BorderWidth = 1f, BorderBrush = Tok.AccentControlElevationBorder,
                    },
                ],
            };
        }

        void OnKey(KeyEventArgs e)
        {
            if (e.KeyCode is not (Keys.Down or Keys.F4)) return;
            ToggleMenu();
            e.Handled = true;
        }

        /// <summary>The menu anchored under the whole control; a second press closes it. Built at open, so its verbs read the
        /// subject's members as they are then.</summary>
        void ToggleMenu()
        {
            if (_menu is { IsOpen: true } shown) { shown.Close(); return; }
            if (Controls.IsNullOverlay(_overlay)) return;
            var items = DetailVerbs.PlayMenu(Subject);
            OverlayHandle? opened = null;
            opened = _overlay.Open(() => _anchor, () => MenuFlyout.Create(items, () => opened?.Close()), FlyoutPlacement.BottomLeft,
                new PopupOptions(FocusTrap: true) { ConstrainToRootBounds = false });
            _menu = opened;
            opened.ClosedAction = () =>
            {
                if (!ReferenceEquals(_menu, opened)) return;
                _menu = null;
                _open.Value = false;
            };
            _open.Value = true;
        }
    }

    // ══ THE ACTION ROW ══════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>How many buttons <see cref="FixedActions"/> lays out for the same inputs: the vertical hero's skeleton reserves exactly these
    /// (tuned Classic X2), so a page without a heart or a link does not shimmer a square it never draws.</summary>
    public static int FixedActionCount(Detail.Identity id, bool heart, Detail.FrameActions acts)
        => (acts.Shuffle is not null ? 1 : 0) + (heart ? 1 : 0) + (ShareActionFor(id) is not null ? 1 : 0) + (acts.More is not null ? 1 : 0);

    /// <summary>The fixed action group after the split (D1): Shuffle (when the page offers it), the heart (when the kind has
    /// one), Share (when the page has a link) and the ⋯ (when the page has a menu), each keyed under <paramref name="keyPrefix"/>.</summary>
    public static List<Element> FixedActions(string keyPrefix, Detail.Identity id, bool heart, Detail.FrameActions acts, Func<ColorF> accent)
    {
        var actions = new List<Element>(4);
        if (acts.Shuffle is { } shuffle)
            actions.Add(Controls.Named(ShuffleButton(shuffle) with { Key = keyPrefix + "shuffle:button" }, Loc.Get(Strings.Detail.Shuffle))
                with { Key = keyPrefix + "shuffle" });
        if (heart)
        {
            string saveUri = SaveTargetOf(id).Text;
            string saveName = id.Title;
            actions.Add(Embed.Comp(() => new Controls.SaveButton { Uri = saveUri, Name = saveName, Glyph = 16f, Box = RailLayout.FabSize, Accent = accent })
                with { Key = keyPrefix + "save:" + saveUri });
        }
        if (ShareActionFor(id) is { } share)
            actions.Add(Controls.Named(Fab(Icons.Share, share) with { Key = keyPrefix + "share:button" }, Loc.Get(Strings.Menu.Share))
                with { Key = keyPrefix + "share" });
        if (acts.More is { } more)
            actions.Add(MoreButton(more, RailLayout.FabSize) with { Key = keyPrefix + "more:" + id.Subject.Text });
        return actions;
    }

    /// <summary>The primary and the actions in ONE wrap, 8 apart both ways (the split's own trailing margin makes its 12).</summary>
    public static BoxEl ActionRow(string key, Element primary, IReadOnlyList<Element> actions, float topMargin)
    {
        var kids = new Element[1 + actions.Count];
        kids[0] = primary;
        for (int i = 0; i < actions.Count; i++) kids[i + 1] = actions[i];
        return new BoxEl
        {
            Key = key, Layout = Shove,
            Direction = 0, Wrap = true, Gap = RailLayout.FabGap, AlignItems = FlexAlign.Center,
            Margin = new Edges4(0f, topMargin, 0f, 0f),
            Children = kids,
        };
    }

    /// <summary>Shuffle beside the split: the standard 32/r4 icon button on the control fill with its elevation stroke, the
    /// plate that says "a second way to play" next to the accent one.</summary>
    static BoxEl ShuffleButton(Action shuffle) => Fab(Icons.Shuffle, shuffle) with
    {
        Fill = Tok.FillControlDefault, HoverFill = Tok.FillControlSecondary, PressedFill = Tok.FillControlTertiary,
        BorderWidth = 1f, BorderBrush = Tok.ControlElevationBorder,
    };
}
