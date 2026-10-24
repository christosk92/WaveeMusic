// ── Themes/Zune/Structure/Views/Show/ZuneShowJumpList.cs — the show reader's month JUMP LIST (V19 at Compact: the owner's call, 2026-10-24) ─────
//
// Windows Phone's long-list jump list (semantic zoom), in Zune's voice. Where the reader's year strip does not stand in the frame's margin (under
// Standard, and wherever the reader pads its own trailing gutter: `ZuneShowRules.YearsInStrip`), a month header ("august 2026") is a button: a tap opens
// a full-window grid over the page, the years as headings and a tile per month under each, January to December.
//
//   TILES    a month the view holds is LIT (the accent, its word in the accent's contrast ink) and picks; a month without episodes is dim (the 5 % wash,
//            the tertiary word) and inert. Every tile is 72 tall and at least a quarter of the column wide: never under the 40 floor.
//   PICK     closes the grid and the list lands on that month's header (`ShowJumpList.Go`: the core's bring-into-view, eased, while the grid zooms away).
//   KEYS     Left / Right step through the lit tiles in reading order, Up / Down a row, Enter picks; Escape, Backspace, Alt+Left and the browser Back
//            key close it. The tile of the month that was tapped takes the focus on open (the first lit one where that month is not shown).
//   MOTION   the overlay's modal chrome zooms the grid in (1.05 to 1, fading); the tiles enter on a diagonal wave (each fades and grows from 86 %
//            after (row + column) x 18 ms, capped), as Windows Phone's tiles feathered in. Closing (a pick, Escape, Back) the grid ZOOMS BACK itself
//            (it grows to 106 % from its centre while it fades, on the smooth-out curve: the modal chrome's own close runs its scale on a front-loaded
//            curve), the list easing to the picked month under it; only then does the overlay close, over a grid already gone. Reduced motion: fades.
//
// The header keeps ONE shape at every tier (the target box over its 24 line, 8 into the air above and the foot below, so the row's extent is the
// core's to the DIP): a crossing only switches its interaction on or off, never rebuilds it.

using System.Globalization;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Input;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using static FluentGpu.Dsl.Ui;
using static Wavee.Themes.Zune.ZuneShowRules;

namespace Wavee.Themes.Zune;

/// <summary>The props of a month header: the core's group args.</summary>
internal sealed record ZuneMonthHeaderProps(ShowGroupArgs Args);

/// <summary>A month header: its word on the group's line, a button that opens the jump list where the year strip does not stand.</summary>
internal sealed class ZuneMonthHeader : Component
{
    public override Element Render()
    {
        var a = UseProps<ZuneMonthHeaderProps>().Args;
        bool ownsEdge = UseContext(global::Wavee.Show.ReaderOwnsEdge);
        var tiers = TierContext.DrawnIn(UseContext(ThemeFactsContext.Signal)).Value.Tiers;
        var overlay = UseContext(Overlay.Service);
        var jump = a.Jump;
        bool opens = jump is not null && !YearsInStrip(tiers.Window, ownsEdge) && !Controls.IsNullOverlay(overlay);
        var key = a.MonthKey;
        Action? open = opens ? () => ZuneShowJumpList.Open(overlay!, jump!, key?.Current() ?? DateKeys.None) : null;
        return new BoxEl
        {
            Direction = 1, MinWidth = 0f,
            Children =
            [
                new BoxEl { Height = a.Air },
                new BoxEl
                {
                    Key = MonthHeaderKey, Direction = 0, AlignItems = FlexAlign.Center, MinWidth = 0f, Shrink = 0f,
                    Height = ShowReaderRules.GroupLine + 2f * HeaderTargetPad, Margin = new Edges4(0f, -HeaderTargetPad, 0f, -HeaderTargetPad),
                    Role = opens ? AutomationRole.Button : AutomationRole.None, Focusable = opens, Cursor = opens ? CursorId.Hand : null,
                    HoverFill = opens ? R.Layer.ChromeHover : ColorF.Transparent, PressedFill = opens ? R.Layer.ChromePressed : ColorF.Transparent,
                    OnClick = open,
                    Children =
                    [
                        R.Type.SheetTitle("") with
                        {
                            // On its exact 24 line (block stacking): the 22 light face's natural line box is 29, which the core's stated line clipped.
                            Text = a.Month, LineHeight = ShowReaderRules.GroupLine, LineStacking = LineStacking.BlockLineHeight, Height = ShowReaderRules.GroupLine,
                            Case = R.Case.For(LabelKind.Heading), Color = Tok.TextPrimary, MaxLines = 1, Wrap = TextWrap.NoWrap, MinWidth = 0f, Shrink = 1f,
                        },
                    ],
                },
                new BoxEl { Height = ShowReaderRules.GroupFoot },
            ],
        };
    }
}

/// <summary>The jump list's overlay: opened from a month header.</summary>
internal static class ZuneShowJumpList
{
    /// <summary>Opens the grid over the window for the months <paramref name="jump"/> holds, the tile of <paramref name="from"/> focused.</summary>
    public static void Open(IOverlayService overlay, ShowJumpList jump, int from)
    {
        int[] months = jump.Months();
        if (months.Length == 0) return;
        OverlayHandle? handle = null;
        bool done = false;
        var closing = new Signal<bool>(false);
        var props = new ZuneJumpGridProps(months, from,
            Pick: key => { if (closing.Peek()) return; jump.Go(key); closing.Value = true; },
            Close: () => closing.Value = true,
            Closing: closing,
            Done: () => { done = true; handle?.Close(); });
        handle = overlay.Open(static () => NodeHandle.Null, () => Embed.Comp(props, static () => new ZuneJumpGrid()), FlyoutPlacement.BottomCenter,
            new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.Modal, Chrome: PopupChrome.Modal));
        // Every close (Escape included) runs the grid's own zoom back first; the overlay closes when it has run (Done).
        handle.ClosingAction = _ =>
        {
            if (done) return true;
            closing.Value = true;
            return false;
        };
    }
}

/// <summary>The jump grid's props: the months the view holds (in its order), the tapped month, the pick, the close request, whether it is closing and
/// the overlay's close once the grid's zoom back has run.</summary>
internal sealed record ZuneJumpGridProps(int[] Months, int From, Action<int> Pick, Action Close, IReadSignal<bool> Closing, Action Done);

/// <summary>The full-window jump grid.</summary>
internal sealed class ZuneJumpGrid : Component
{
    readonly List<int> _keys = [];          // every tile's month key, in reading order
    readonly List<bool> _lit = [];
    readonly List<NodeHandle> _nodes = [];
    int _cols = 4, _focus = -1;
    bool _gone;
    InputHooks? _input;
    ZuneJumpGridProps? _p;

    public override Element Render()
    {
        var p = _p = UseProps<ZuneJumpGridProps>();
        var size = UseContextSignal(Viewport.Size).Value;
        var tiers = TierContext.DrawnIn(UseContext(ThemeFactsContext.Signal)).Value.Tiers;
        _input = UseContext(InputHooks.Current);
        int cols = _cols = JumpColumns(tiers.Window);
        float gutter = ZuneTiers.GutterOf(in tiers);
        bool reduced = Design.Reduced;
        bool closing = p.Closing.Value;
        // The zoom back: the grid grows from its centre while it fades; when it has run the grid is emptied (nothing left to move) and the overlay closes.
        float closeMs = reduced ? 0f : JumpCloseMs;
        UseTransition(AnimChannel.Opacity, 1f, closing ? 0f : 1f, closing ? closeMs : 0f, Easing.SmoothOut, DepKey.From(closing ? 1 : 0));
        UseTransition(AnimChannel.ScaleX, 1f, closing && !reduced ? JumpCloseScale : 1f, closing ? closeMs : 0f, Easing.SmoothOut, DepKey.From(closing ? 1 : 0));
        UseTransition(AnimChannel.ScaleY, 1f, closing && !reduced ? JumpCloseScale : 1f, closing ? closeMs : 0f, Easing.SmoothOut, DepKey.From(closing ? 1 : 0));
        var gone = UseSignal(false);
        UseTimeout(() => { if (closing) gone.Value = true; }, closeMs, DepKey.From(closing ? 1 : 0));
        _gone = gone.Value;
        UseEffect(() => { if (_gone) p.Done(); }, DepKey.From(_gone ? 1 : 0));
        if (_gone) return new BoxEl { Width = size.Width, Height = size.Height, HitTestVisible = false };

        var lit = new HashSet<int>(p.Months);
        var years = new List<int>(4);
        foreach (int m in p.Months)
        {
            int y = DateKeys.YearOfMonth(m);
            if (y > 0 && !years.Contains(y)) years.Add(y);
        }
        _keys.Clear(); _lit.Clear();
        var blocks = new List<Element>(years.Count * 2);
        int row = 0;
        foreach (int year in years)
        {
            blocks.Add(R.Type.Heading(year.ToString(CultureInfo.InvariantCulture)) with
            {
                Color = Tok.TextPrimary, MaxLines = 1, Margin = new Edges4(0f, blocks.Count == 0 ? 0f : JumpYearGap, 0f, JumpGap),
            });
            for (int first = 1; first <= 12; first += cols)
            {
                var tiles = new Element[cols];
                for (int c = 0; c < cols; c++)
                {
                    int month = first + c;
                    if (month > 12) { tiles[c] = new BoxEl { Grow = 1f, Basis = 0f, MinWidth = 0f }; continue; }
                    int key = DateKeys.MonthKey(year, month);
                    tiles[c] = Tile(key, month, lit.Contains(key), _keys.Count, row + c, reduced);
                    _keys.Add(key);
                    _lit.Add(lit.Contains(key));
                }
                blocks.Add(new BoxEl { Direction = 0, Gap = JumpGap, MinWidth = 0f, Margin = new Edges4(0f, 0f, 0f, JumpGap), Children = tiles });
                row++;
            }
        }
        while (_nodes.Count < _keys.Count) _nodes.Add(NodeHandle.Null);

        // The tapped month's tile takes the focus once the grid is laid out (the first lit one where it is not shown).
        UseLayoutEffect(() =>
        {
            int at = _keys.IndexOf(p.From);
            if (at < 0 || !_lit[at]) at = _lit.IndexOf(true);
            if (at >= 0 && Context.Scene is { } scene && scene.IsLive(_nodes[at])) _input?.RestoreFocus?.Invoke(_nodes[at]);
        }, DepKey.From(p.From, p.Months.Length, 0, 0));

        return new BoxEl
        {
            Key = JumpGridKey, Width = size.Width, Height = size.Height, Direction = 1, Fill = R.Layer.ShellGround, OnKeyDown = Key,
            TransformOriginX = 0.5f, TransformOriginY = 0.5f, Opacity = closing ? 0f : 1f, HitTestVisible = !closing,
            Children =
            [
                ScrollView(new BoxEl
                {
                    Direction = 1, MinWidth = 0f,
                    Padding = new Edges4(gutter, ZuneTiers.FrameOf(in tiers).TitleBarH, gutter, gutter),
                    Children = blocks.ToArray(),
                }) with { Grow = 1f, MinHeight = 0f },
            ],
        };
    }

    /// <summary>One month: lit (the accent, picks) or dim (the wash, inert); it enters on the diagonal wave.</summary>
    Element Tile(int key, int month, bool lit, int index, int wave, bool reduced)
    {
        ColorF fill = lit ? R.Ink.AccentAction : R.Layer.ChromeHover;
        string word = CultureInfo.CurrentCulture.DateTimeFormat.GetAbbreviatedMonthName(month);
        var p = _p!;
        return new BoxEl
        {
            Key = JumpTilePrefix + key.ToString(CultureInfo.InvariantCulture), Direction = 1, Justify = FlexJustify.End, AlignItems = FlexAlign.Start,
            Grow = 1f, Basis = 0f, MinWidth = 0f, Height = JumpTileH, Padding = Edges4.All(JumpTilePad),
            Fill = fill, HoverFill = lit ? ColorContrast.Over(R.Layer.ChromeHover, fill) : fill, PressedFill = lit ? ColorContrast.Over(R.Layer.ChromePressed, fill) : fill,
            Role = lit ? AutomationRole.Button : AutomationRole.None, Focusable = lit, Cursor = lit ? CursorId.Hand : null,
            OnClick = lit ? () => p.Pick(key) : null,
            OnRealized = h => { if (index < _nodes.Count) _nodes[index] = h; else _nodes.Add(h); },
            OnFocusChanged = on => { if (on) _focus = index; },
            Enter = new EnterExit(Sx: reduced ? 1f : JumpTileScale, Sy: reduced ? 1f : JumpTileScale, Opacity: 0f, Active: true,
                DelayMs: reduced ? 0f : Math.Min(wave, JumpStaggerCap) * JumpStaggerMs),
            Transition = JumpTileMotion,
            Children =
            [
                R.Type.SheetTitle(word) with
                {
                    Case = R.Case.For(LabelKind.Label), MaxLines = 1, Wrap = TextWrap.NoWrap, MinWidth = 0f, Shrink = 1f,
                    Color = lit ? ColorContrast.PickContrast(fill) : Tok.TextTertiary,
                },
            ],
        };
    }

    /// <summary>The grid's keys: arrows move among the lit tiles, Escape / Backspace / Alt+Left / Back close.</summary>
    void Key(KeyEventArgs e)
    {
        if (e.Handled || _p is not { } p) return;
        if (e.KeyCode is Keys.Escape or Keys.Back or BrowserBackKey || (e.KeyCode == Keys.Left && e.Alt))
        {
            e.Handled = true;
            p.Close();
            return;
        }
        int step = e.KeyCode switch { Keys.Left => -1, Keys.Right => 1, Keys.Up => -_cols, Keys.Down => _cols, _ => 0 };
        if (step == 0 || _keys.Count == 0) return;
        e.Handled = true;
        int from = _focus >= 0 ? _focus : _lit.IndexOf(true);
        for (int i = from + step; i >= 0 && i < _keys.Count; i += step)
        {
            if (!_lit[i]) continue;
            if (Context.Scene is { } scene && scene.IsLive(_nodes[i])) (_input?.MoveFocusVisual ?? _input?.RestoreFocus)?.Invoke(_nodes[i]);
            return;
        }
    }
}
