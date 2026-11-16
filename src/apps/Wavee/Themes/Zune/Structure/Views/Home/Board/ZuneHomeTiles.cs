// ── Themes/Zune/Structure/Views/Home/Board/ZuneHomeTiles.cs — Zune's `Home.Tile.*` (pinned-home-tech.md 1.7; spec 4.2, 8.2) ─────────────────
//
// Windows Phone Start's tiles: flat square-cornered PLATES, the art full bleed. Per form (the form the packed box carries, spec 4.2):
//   S (1 x 1)    the art alone; a widget its glyph and one big number or word
//   M (2 x 2)    the art full bleed with the name over a scrim at its foot; a widget its head over two rows
//   L (4 x 2)    the art square at the left, a text panel beside it (the name in the 22/28 light voice, a line); a widget its head over four rows
//   Full (band)  today's chapter of Home: the daylist strip, a section's shelf, the release list, the history group (no plate)
//
// THE CHROME (`TileChrome`, every form): a press tilts the tile (`TileMotion.Tilt`); a held press (600 ms mouse, 500 ms touch) or a right-click enters
// customize with the tile selected. Customizing, the selected tile wears an outline in the ink and two badges (unpin top right, resize bottom right;
// a band's both at its foot, under its heading row: never over text), every other tile falls back to 94 % and dims, and a tap on it selects it
// (a layer over its face takes the tap, so nothing opens). Both are seeded channels on the chrome's own node: no relayout. The ring (2, the accent)
// marks a tile just added.

using System.Collections.Generic;
using System.Globalization;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.HomeUi;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using B = Wavee.Themes.Zune.ZuneHomeBoardRules;
using K = Wavee.Themes.Zune.ZuneDiscoveryKit;

namespace Wavee.Themes.Zune;

/// <summary>Zune's tile templates and the board's chrome pieces.</summary>
internal static class ZuneHomeTiles
{
    // ══ the board's chrome ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>A group head: the 40/48 light title, lowercase, ONE line that bleeds under a fade at the board's edge (never smaller), its undo word
    /// at its end.</summary>
    public static Element GroupHead(HomeBoardProps board, BoardEntry e, ZuneHomeBoardLayout lay)
        => Embed.Comp(new HeadProps(board, e.GroupId, e.Title), static () => new HeadView());

    internal sealed record HeadProps(HomeBoardProps Board, string GroupId, string Title);

    sealed class HeadView : Component
    {
        readonly Signal<bool> _hover = new(false);

        public override Element Render()
        {
            var p = UseProps<HeadProps>();
            return new BoxEl
            {
                Direction = 0, Grow = 1f, MinWidth = 0f, AlignItems = FlexAlign.Start,
                OnHoverMove = _ => { if (!_hover.Peek()) _hover.Value = true; },
                OnPointerExit = () => { if (_hover.Peek()) _hover.Value = false; },
                Children =
                [
                    new BoxEl
                    {
                        // The title hugs its words (the undo word follows them) and yields only when the line is too short: then it bleeds under the fade.
                        // Its fade band is padding: a title that fits fades nothing; one that does not runs under it.
                        Direction = 0, Shrink = 1f, MinWidth = 0f, ClipToBounds = true, Padding = new Edges4(0f, 0f, B.TitleFade, 0f),
                        EdgeFade = new EdgeFadeSpec(EdgeMask.Right, 0f, 0f, B.TitleFade, 0f),
                        Children =
                        [
                            R.Type.PageHero(p.Title) with
                            {
                                Key = HomeBoardKeys.Group(p.GroupId) + ":title", Case = R.Case.For(LabelKind.Heading),
                                MaxLines = 1, Wrap = TextWrap.NoWrap, Shrink = 0f,
                            },
                        ],
                    },
                    ZuneHomeUndoWord.Word(p.Board, p.GroupId, _hover),
                ],
            };
        }
    }

    /// <summary>The empty Pinned group: large light words at the group's height (spec 8.2 "Empty states").</summary>
    public static Element EmptyPinned() => new BoxEl
    {
        Direction = 1, Grow = 1f, MinWidth = 0f, Justify = FlexJustify.Center, Gap = B.TextGap,
        Children =
        [
            R.Type.Heading(Loc.Get(Strings.Theme.Zune.Home.EmptyTitle)) with { Case = R.Case.For(LabelKind.Heading), Wrap = TextWrap.Wrap, MinWidth = 0f },
            R.Type.DenseMeta(Loc.Get(Strings.Theme.Zune.Home.EmptyHint)) with
                { Color = Tok.TextSecondary, Case = R.Case.For(LabelKind.Label), Wrap = TextWrap.Wrap, MinWidth = 0f },
        ],
    };

    /// <summary>The drop outline at the lifted tile's packed place: a dashed outline in the ink.</summary>
    public static Element Slot(RectF rect) => new BoxEl
    {
        Key = HomeBoardKeys.Slot, Width = rect.W, Height = rect.H,
        BorderColor = Tok.TextSecondary, BorderWidth = B.RingW, BorderDashOn = 4f, BorderDashOff = 4f, HitTestVisible = false,
    };

    /// <summary>The board's model as shown now (set by the board: the tiles name and step through it).</summary>
    internal static Func<BoardModel>? Shown { get; set; }

    // ══ the tile chrome ══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>A tile around <paramref name="face"/>: the plate (none on a band), the tilt, the hold, the customize state.</summary>
    static Element Tile(HomeTileFrame f, Element face, bool plate = true)
        => Embed.Comp(new ChromeProps(f, face, plate), static () => new TileChrome());

    internal sealed record ChromeProps(HomeTileFrame Frame, Element Face, bool Plate);

    /// <summary>The tile's chrome: hold, right-click, recede, select, badges, ring.</summary>
    internal sealed class TileChrome : Component
    {
        readonly Action<NodeHandle> _onRealized;
        readonly Action<HoldEventArgs> _onHold;
        readonly Action<ContextRequestEventArgs> _onContext;
        readonly Action _select;
        ChromeProps? _p;
        NodeHandle _node;
        bool _first = true;
        bool _receded;
        float _initialScale = 1f, _initialOpacity = 1f;

        public TileChrome()
        {
            _onRealized = h => _node = h;
            _onHold = _ => EnterHere();
            _onContext = e => { if (e.Trigger != ContextRequestTrigger.Hold) EnterHere(); };
            _select = () => { if (_p is { } p) ZuneHomeBoard.Select(p.Frame.View, p.Frame.Verbs, Model(p.Frame), p.Frame.Entry.Id); };
        }

        static BoardModel Model(HomeTileFrame f) => Shown?.Invoke() ?? new BoardModel([f.Entry], 0);

        void EnterHere()
        {
            if (_p is not { } p) return;
            var f = p.Frame;
            ZuneHomeBoard.Customize(f.View, f.Verbs, Model(f), f.Entry.Id);
        }

        public override Element Render()
        {
            var p = UseProps<ChromeProps>();
            _p = p;
            var f = p.Frame;
            var view = f.View;
            string id = f.Entry.Id;
            bool preview = f.Cell is null;   // a candidate's live preview outside the grid: a face, nothing else
            bool editing = !preview && view.Editing.Value;
            bool selected = editing && view.SelectedId.Value == id;
            bool recede = editing && !selected;
            if (_first) { _first = false; _receded = recede; _initialScale = recede ? B.Recede : 1f; _initialOpacity = recede ? B.RecedeDim : 1f; }
            UseLayoutEffect(() => Recede(recede), DepKey.From(recede ? 1 : 0));

            bool full = f.Form == HomeTileForm.Full;
            var kids = new List<Element>(4) { preview ? p.Face : TileMotion.Tilt(p.Face) };
            if (!preview)
                // A tap on a receded tile selects it: a layer over the face takes it (nothing opens while customizing). Always mounted, so it rides the
                // tile's recede instead of arriving in the middle of it; it takes the pointer only while the tile is receded.
                kids.Add(new BoxEl
                {
                    Key = "home:tile-select", Cursor = recede ? CursorId.Hand : null, OnClick = _select, Role = AutomationRole.None, HitTestVisible = recede, Focusable = false,
                    Fill = ColorF.Transparent, HoverFill = ColorF.Transparent, PressedFill = ColorF.Transparent,
                });
            if (selected)
            {
                kids.Add(new BoxEl { BorderColor = Tok.TextPrimary, BorderWidth = B.SelectedRing, HitTestVisible = false });
                kids.Add(Badges(f, full));
            }
            if (!preview)
                kids.Add(new BoxEl
                {
                    BorderColor = Tok.AccentDefault, BorderWidth = B.RingW, HitTestVisible = false,
                    Visible = Prop.Of(() => view.RingId.Value == id && !view.Editing.Value),
                });
            return new BoxEl
            {
                Direction = 1, Grow = 1f, MinWidth = 0f, ZStack = true, ClipToBounds = !full,
                Fill = p.Plate && !full ? Tok.FillSubtleSecondary : ColorF.Transparent,
                ScaleX = _initialScale, ScaleY = _initialScale, Opacity = _initialOpacity,
                OnRealized = _onRealized,
                // Press and hold enters customize with this tile (the selected tile's hold is the grid's: hold, then drag).
                Hold = preview || selected ? null : new HoldSpec(TouchMs: B.HoldTouchMs, MouseMs: B.HoldMouseMs),
                OnHold = preview || selected ? null : _onHold,
                OnContextRequested = preview ? null : _onContext,
                Children = kids.ToArray(),
            };
        }

        void Recede(bool recede)
        {
            if (recede == _receded) return;
            _receded = recede;
            if (Context.Anim is not { } anim || Context.Scene is not { } s || _node.IsNull || !s.IsLive(_node)) return;
            float scale = recede ? B.Recede : 1f, opacity = recede ? B.RecedeDim : 1f;
            bool reduced = Design.Reduced;
            var motion = MotionTokenDef.Eased(reduced ? B.ReducedMs : B.RecedeMs, Easing.FluentStandard);   // eases in: no first-frame lurch
            // Reduced motion: the dim fades, the scale lands at once (no travel).
            var scaleMotion = reduced ? MotionTokenDef.Eased(0f, Easing.Linear) : motion;
            anim.SeedValue(_node, AnimChannel.ScaleX, scale, scaleMotion);
            anim.SeedValue(_node, AnimChannel.ScaleY, scale, scaleMotion);
            anim.SeedValue(_node, AnimChannel.Opacity, opacity, motion);
        }
    }

    /// <summary>The selected tile's badges: unpin (top right) and resize (bottom right), 36 discs in 40 targets inside the tile; a band carries both at
    /// its foot (its heading row is text).</summary>
    static Element Badges(HomeTileFrame f, bool full)
    {
        var e = f.Entry;
        var verbs = f.Verbs;
        string id = e.Id;
        string name = e.Title;
        string removeLabel = e.Kind switch
        {
            BoardEntryKind.FillCell or BoardEntryKind.FeedSection or BoardEntryKind.Fold => Strings.Theme.Zune.Home.Hide(name: name),
            _ when e.Raw is SpotifySectionSource => Strings.Theme.Zune.Home.Hide(name: name),
            _ when e.Raw is PinSource => Strings.Theme.Zune.Home.Unpin(name: name),
            _ => Strings.Theme.Zune.Home.Remove(name: name),
        };
        var size = ZuneHomeBoardRules.SizeOf(e);
        string sizeWord = Loc.Get(size switch
        {
            HomeSize.S => Strings.Theme.Zune.Home.SizeS, HomeSize.M => Strings.Theme.Zune.Home.SizeM,
            HomeSize.L => Strings.Theme.Zune.Home.SizeL, _ => Strings.Theme.Zune.Home.SizeFull,
        });
        bool cycles = e.Source is { Sizes.Length: > 1 } && !e.OnlyHide;
        var unpin = Badge(HomeBoardKeys.Tools(id) + B.UnpinSuffix, Icons.UnPin, removeLabel, () =>
        {
            ZuneHomeBoard.RemoveSelected(f.View, verbs, Shown?.Invoke() ?? new BoardModel([e], 0));
        }, null);
        var resize = cycles
            ? Badge(HomeBoardKeys.Tools(id) + B.ResizeSuffix, Icons.FullScreen, Strings.Theme.Zune.Home.Resize(name: name, size: sizeWord),
                () => verbs.CycleSize(id),
                // Tab from the resize badge selects the next tile (spec 8.2: "Tab selects the next").
                k =>
                {
                    if (k.KeyCode != Keys.Tab || k.Shift || k.Ctrl || k.Alt) return;
                    var model = Shown?.Invoke();
                    if (model is not null && ZuneHomeBoard.Step(model, id, 1) is { } next) { ZuneHomeBoard.Select(f.View, verbs, model, next); k.Handled = true; }
                })
            : null;
        Element column = full
            ? new BoxEl
            {
                Direction = 1, Justify = FlexJustify.End, AlignItems = FlexAlign.End, Gap = B.BadgeInset, HitTestPassThrough = true,
                Padding = new Edges4(0f, 0f, B.BadgeInset, B.BadgeInset),
                Children = resize is { } r1 ? [unpin, r1] : [unpin],
            }
            : new BoxEl
            {
                Direction = 1, Justify = FlexJustify.SpaceBetween, AlignItems = FlexAlign.End, HitTestPassThrough = true,
                Padding = new Edges4(0f, B.BadgeInset, B.BadgeInset, B.BadgeInset),
                Children = resize is { } r2 ? [unpin, r2] : [unpin],
            };
        return new BoxEl { Key = HomeBoardKeys.Tools(id), Direction = 1, HitTestPassThrough = true, Children = [column] };
    }

    /// <summary>One badge: a 36 disc (the ground, a 2 ring in the ink, the glyph) in a 40 target; the accent on hover.</summary>
    static Element Badge(string key, string glyph, string label, Action act, Action<KeyEventArgs>? onKey) => Controls.Named(new BoxEl
    {
        Key = key, Width = B.BadgeHit, Height = B.BadgeHit, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Focusable = true, Role = AutomationRole.Button, Cursor = CursorId.Hand, OnClick = act, OnKeyDown = onKey,
        Children =
        [
            new BoxEl
            {
                Width = B.BadgeEdge, Height = B.BadgeEdge, Corners = CornerRadius4.All(B.BadgeEdge / 2f), Direction = 0,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, HitTestVisible = false,
                Fill = Tok.FillSolidBase, BorderWidth = B.BadgeStroke, BorderColor = Tok.TextPrimary,
                Children = [new TextEl(glyph) { Size = B.BadgeGlyph, FontFamily = Tok.Definition.Fonts.Icon, Color = Tok.TextPrimary }],
            },
        ],
    }, label);

    // ══ faces ═════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The art filling its box (object-fit: cover); a drawn cover (Liked Songs' heart plate) at the box's longer edge, centred and cropped.</summary>
    static Element Art(HomeTileFrame f, string? url, Func<float, Element>? drawn = null)
    {
        if (drawn is { } d)
        {
            var box = f.Cell?.Box.Peek() ?? default;
            float edge = MathF.Max(1f, MathF.Max(box.Width, box.Height));   // it fills the box (cropped by the tile), as a cover does
            return new BoxEl { Grow = 1f, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, ClipToBounds = true, Children = [d(edge)] };
        }
        return url is { Length: > 0 }
            ? Controls.CoverFill(url, 0f, B.ArtDecodePx)
            : new BoxEl { Grow = 1f, AlignSelf = FlexAlign.Stretch, Fill = R.Layer.ArtPlaceholder };
    }

    /// <summary>The wide form's square art: as wide as the tile is tall (a bound width, so a re-pack that changes the height follows it with no render).</summary>
    static Element Square(HomeTileFrame f, Element art)
    {
        var cell = f.Cell;
        return new BoxEl
        {
            Shrink = 0f, AlignSelf = FlexAlign.Stretch, Direction = 1, ZStack = true, ClipToBounds = true,
            Width = cell is null ? Prop.Of(static () => B.PreviewSquare) : Prop.Of(() => cell.Box.Value.Height),
            Children = [art],
        };
    }

    /// <summary>A name over a scrim at the tile's foot (the medium form), the badges' lane left free at its end.</summary>
    static Element ScrimName(Func<string> name, Func<string>? line = null) => new BoxEl
    {
        Direction = 1, Justify = FlexJustify.End, MinWidth = 0f, HitTestVisible = false,
        Children =
        [
            new BoxEl
            {
                Direction = 1, MinWidth = 0f, MinHeight = B.ScrimH, Justify = FlexJustify.End,
                Padding = new Edges4(B.TilePad, 0f, B.BadgeLane, B.SmallPad),
                Gradient = B.NameScrim,
                Children = line is null
                    ? [NameText(name, 2)]
                    : [NameText(name, 1), R.Type.MicroMeta("") with { Text = Prop.Of(line), Color = Tok.OnMediaPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f }],
            },
        ],
    };

    static TextEl NameText(Func<string> name, int lines) => R.Type.CardTitle("") with
    {
        Text = Prop.Of(name), Color = Tok.OnMediaPrimary, MaxLines = lines, Wrap = TextWrap.Wrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
    };

    /// <summary>The wide form's text panel beside the art: the name in the 22/28 light voice, its line, then <paramref name="foot"/>.</summary>
    static Element Panel(Func<string> name, Func<string> line, Element? foot = null, TextCase nameCase = TextCase.AsWritten) => new BoxEl
    {
        Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, Gap = B.TextGap,
        Padding = new Edges4(B.TilePad + 2f, B.TilePad, B.BadgeLane, B.TilePad),
        Children =
        [
            R.Type.SheetTitle("") with { Text = Prop.Of(name), Case = nameCase, MaxLines = 3, Wrap = TextWrap.Wrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
            R.Type.DenseMeta("") with { Text = Prop.Of(line), Color = Tok.TextSecondary, MaxLines = 2, Wrap = TextWrap.Wrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
            new BoxEl { Grow = 1f },
            foot ?? new BoxEl(),
        ],
    };

    /// <summary>A widget's head inside a tile: its glyph in the accent and its name (the 14 strong voice, lowercase).</summary>
    static Element Head(string glyph, string title, Func<string>? line = null) => new BoxEl
    {
        Direction = 0, MinWidth = 0f, AlignItems = FlexAlign.Center, Gap = Spacing.S, Padding = new Edges4(0f, 0f, B.BadgeLane - B.TilePad, 0f),
        Children =
        [
            Ui.Icon(glyph, B.HeadGlyph, Tok.AccentTextPrimary) with { Shrink = 0f },
            R.Type.DenseTitle(title) with { Case = R.Case.For(LabelKind.Heading), MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f },
            line is null ? new BoxEl() : R.Type.MicroMeta("") with { Text = Prop.Of(line), Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f },
        ],
    };

    /// <summary>A small widget face: its glyph at the top, one big word or number at the foot (the 22/28 light voice).</summary>
    static Element SmallWidget(string glyph, Func<string> big) => new BoxEl
    {
        Direction = 1, Grow = 1f, MinWidth = 0f, Justify = FlexJustify.SpaceBetween, Padding = new Edges4(B.SmallPad, B.SmallPad, B.SmallPad, B.SmallPad),
        Children =
        [
            Ui.Icon(glyph, B.WidgetGlyph, Tok.AccentTextPrimary) with { Shrink = 0f },
            R.Type.SheetTitle("") with { Text = Prop.Of(big), MaxLines = 1, Trim = TextTrim.None, MinWidth = 0f },
        ],
    };

    /// <summary>A widget's padded column (head over rows).</summary>
    static BoxEl Pad(params Element[] kids) => new()
    {
        Direction = 1, Grow = 1f, MinWidth = 0f, Gap = B.ListGap, Padding = new Edges4(B.TilePad, B.SmallPad, B.TilePad, B.TilePad), Children = kids,
    };

    /// <summary>The rows a widget lists: two in a medium tile, four in a wide one (two columns of two).</summary>
    static int Rows(HomeTileForm form) => form == HomeTileForm.M ? B.ListRowsM : B.ListRowsL;

    /// <summary>A widget's head over its rows: one column in a medium tile, two columns in a wide one.</summary>
    static Element[] Listed(List<Element> kids, HomeTileForm form)
    {
        if (form != HomeTileForm.L || kids.Count <= 1 + B.ListRowsM) return kids.ToArray();
        var left = new List<Element>(B.ListRowsM);
        var right = new List<Element>(B.ListRowsM);
        for (int i = 1; i < kids.Count; i++) (i <= B.ListRowsM ? left : right).Add(kids[i]);
        return
        [
            kids[0],
            new BoxEl
            {
                Direction = 0, MinWidth = 0f, Gap = B.ListColGap, AlignItems = FlexAlign.Start,
                Children =
                [
                    new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, Shrink = 1f, MinWidth = 0f, Gap = B.ListGap, Children = left.ToArray() },
                    new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, Shrink = 1f, MinWidth = 0f, Gap = B.ListGap, Children = right.ToArray() },
                ],
            },
        ];
    }

    static SurfaceShape ListShape => Shape.Row(B.ListArt) with { MinHeight = B.ListRowH };

    static BoxEl Clickable(BoxEl box, Action open) => box with
    {
        OnClick = open, Cursor = CursorId.Hand, HoverFill = R.Layer.ChromeHover, PressedFill = R.Layer.ChromePressed, Role = AutomationRole.Button,
    };

    // ══ Home.Tile.Pin ═══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The registered template of <see cref="ViewKeys.HomeTilePin"/>.</summary>
    public static Element Pin(HomePinTileProps p, in ViewContext ctx)
    {
        var f = p.Frame;
        string? cover = p.Cover();
        Element face = f.Form switch
        {
            HomeTileForm.S => Clickable(new BoxEl { Grow = 1f, Direction = 1, ZStack = true, Children = [Art(f, cover, p.CoverArt)] }, p.Open),
            HomeTileForm.M => Clickable(new BoxEl { Grow = 1f, Direction = 1, ZStack = true, Children = [Art(f, cover, p.CoverArt), ScrimName(p.Name)] }, p.Open),
            _ => Clickable(new BoxEl
            {
                Grow = 1f, Direction = 0, MinWidth = 0f, AlignItems = FlexAlign.Stretch,
                Children = [Square(f, Art(f, cover, p.CoverArt)), Panel(p.Name, p.Line)],
            }, p.Open),
        };
        return Tile(f, face);
    }

    // ══ Home.Tile.NowPlaying ════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The registered template of <see cref="ViewKeys.HomeTileNowPlaying"/>.</summary>
    public static Element NowPlaying(HomeNowPlayingTileProps p, in ViewContext ctx)
    {
        var f = p.Frame;
        Func<string> name = () => p.HasTrack() ? p.Title() : Loc.Get(Strings.Theme.Zune.Home.NothingPlaying);
        var progress = new BoxEl { Height = B.ProgressH, Shrink = 0f, Direction = 1, Children = [p.ProgressLine()] };
        var playPause = new BoxEl
        {
            Shrink = 0f,
            Children =
            [
                Controls.Named(Controls.IconAction(Icons.Play, p.PlayPause) with { Visible = Prop.Of(() => !p.Playing.Value) }, Loc.Get(Strings.Theme.Zune.Home.Play)),
                Controls.Named(Controls.IconAction(Icons.Pause, p.PlayPause) with { Visible = Prop.Of(() => p.Playing.Value) }, Loc.Get(Strings.Theme.Zune.Home.Pause)),
            ],
        };
        Element face;
        switch (f.Form)
        {
            case HomeTileForm.S:
                face = new BoxEl
                {
                    Grow = 1f, Direction = 1, ZStack = true,
                    Children =
                    [
                        Art(f, p.Cover()),
                        new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Children = [playPause] },
                        new BoxEl { Direction = 1, Justify = FlexJustify.End, HitTestPassThrough = true, Children = [progress] },
                    ],
                };
                break;
            case HomeTileForm.M:
                face = new BoxEl
                {
                    Grow = 1f, Direction = 1, ZStack = true,
                    Children =
                    [
                        Clickable(new BoxEl { Grow = 1f, Direction = 1, ZStack = true, Children = [Art(f, p.Cover()), ScrimName(name, p.Line)] }, p.Open),
                        new BoxEl { Direction = 1, Justify = FlexJustify.End, HitTestPassThrough = true, Children = [progress] },
                    ],
                };
                break;
            default:
            {
                var transport = new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.XS,
                    Children =
                    [
                        Controls.Named(Controls.IconAction(Icons.Previous, p.Previous), Loc.Get(Strings.Theme.Zune.Home.Previous)), playPause,
                        Controls.Named(Controls.IconAction(Icons.Next, p.Next), Loc.Get(Strings.Theme.Zune.Home.Next)),
                    ],
                };
                Element foot = p.Lyric is { } lyric
                    ? new BoxEl { Direction = 1, MinWidth = 0f, Gap = B.TextGap, Children = [lyric(LyricLine), transport] }
                    : transport;
                face = new BoxEl
                {
                    Grow = 1f, Direction = 1, MinWidth = 0f,
                    Children =
                    [
                        new BoxEl
                        {
                            Grow = 1f, Direction = 0, MinWidth = 0f, AlignItems = FlexAlign.Stretch,
                            Children =
                            [
                                Clickable(new BoxEl { Direction = 1, AlignSelf = FlexAlign.Stretch, Children = [Square(f, Art(f, p.Cover()))] }, p.Open),
                                Panel(name, p.Line, foot, p.HasTrack() ? TextCase.AsWritten : R.Case.For(LabelKind.Label)),
                            ],
                        },
                        progress,
                    ],
                };
                break;
            }
        }
        return Tile(f, face);
    }

    /// <summary>The lyric reel's line: the active line in the accent.</summary>
    static Element LyricLine(MiniLyricsFrame frame)
    {
        var lines = frame.Lines;
        var active = frame.Active;
        return R.Type.DenseMeta("") with
        {
            Color = Tok.AccentTextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
            Text = Prop.Of(() => lines.Count == 0 ? "" : lines[Math.Clamp(active.Value, 0, lines.Count - 1)]),
        };
    }

    // ══ Home.Tile.UpNext ════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The registered template of <see cref="ViewKeys.HomeTileUpNext"/>.</summary>
    public static Element UpNext(HomeUpNextTileProps p, in ViewContext ctx)
    {
        var f = p.Frame;
        var rows = p.Rows();
        string title = Loc.Get(Strings.Theme.Zune.Home.UpNext);
        string count = Strings.Theme.Zune.Home.InQueue(n: rows.Length);
        if (f.Form == HomeTileForm.S)
        {
            string n = rows.Length.ToString(CultureInfo.CurrentCulture);
            return Tile(f, Clickable(new BoxEl { Grow = 1f, Direction = 1, Children = [SmallWidget(Icons.List, () => n)] }, p.OpenQueue));
        }
        int max = Rows(f.Form);
        var kids = new List<Element> { Head(Icons.List, title, () => count) };
        for (int i = 0; i < rows.Length && i < max; i++)
            kids.Add(Controls.Surface(rows[i].Data, ListShape) with { Key = "upnext:" + rows[i].Key });
        if (rows.Length == 0) kids.Add(R.Type.DenseMeta(Loc.Get(Strings.Theme.Zune.Home.QueueEmpty)) with { Color = Tok.TextSecondary, Case = R.Case.For(LabelKind.Label) });
        return Tile(f, Pad(Listed(kids, f.Form)));
    }

    // ══ Home.Tile.Recents ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The registered template of <see cref="ViewKeys.HomeTileRecents"/>: its band is Zune's history group.</summary>
    public static Element Recents(HomeRecentsTileProps p, in ViewContext ctx)
    {
        var f = p.Frame;
        var r = p.Recents;
        string title = Loc.Get(Strings.Sidebar.Section.RecentlyPlayed);
        string week = Strings.Home.PlaysThisWeek(r.Week.Total);
        switch (f.Form)
        {
            case HomeTileForm.Full:
                return Tile(f, new BoxEl { Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch, Children = [ctx.View(ViewKeys.HomeZoneRecentGrid, r)] }, plate: false);
            case HomeTileForm.S:
            {
                string n = r.Week.Total.ToString(CultureInfo.CurrentCulture);
                return Tile(f, Clickable(new BoxEl { Grow = 1f, Direction = 1, Children = [SmallWidget(Icons.History, () => n)] }, p.OpenHistory));
            }
            default:
            {
                int max = Rows(f.Form);
                var kids = new List<Element> { Head(Icons.History, title, () => week) };
                for (int i = 0; i < r.Cells.Count && i < max; i++)
                {
                    var cell = r.Cells[i];
                    var c = cell.Card;
                    var data = new Controls.CardData(c.Uri, cell.Title, K.Sub(cell.When.Length > 0 ? cell.When : cell.Detail), c.ImageUrl,
                        () => HomeCardNav.Open(in c), () => HomeCardNav.Play(in c), Circular: cell.Round) { Menu = HomeCardNav.MenuOf(in c) };
                    kids.Add(Controls.Surface(data, ListShape) with { Key = "recents:" + (c.IsBlank ? i.ToString(CultureInfo.InvariantCulture) : c.Uri) });
                }
                return Tile(f, Pad(Listed(kids, f.Form)));
            }
        }
    }

    // ══ Home.Tile.Releases ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The registered template of <see cref="ViewKeys.HomeTileReleases"/>: its band is the release list chapter.</summary>
    public static Element Releases(HomeReleasesTileProps p, in ViewContext ctx)
    {
        var f = p.Frame;
        var zone = p.Zone;
        if (f.Form == HomeTileForm.Full)
            return Tile(f, ZuneHomePage.Body(zone, f.Overlay, ZuneHomeTiers.Of(ctx.Tier())), plate: false);
        string title = zone.Title ?? Loc.Get(Strings.Home.Zone.FromArtistsYouFollow);
        int max = Rows(f.Form);
        var kids = new List<Element> { Head(Icons.RefineSparkle, title) };
        for (int i = 0; i < zone.Items.Count && i < max; i++) kids.Add(CardRow(zone.Items[i], i));
        return Tile(f, Pad(Listed(kids, f.Form)));
    }

    static Element CardRow(HomeCard card, int i)
    {
        var c = card;
        var data = new Controls.CardData(c.Uri, c.Title, K.Sub(HomeCards.PlainText(c.Subtitle)), c.ImageUrl,
            () => HomeCardNav.Open(in c), () => HomeCardNav.Play(in c)) { Menu = HomeCardNav.MenuOf(in c) };
        return Controls.Surface(data, ListShape) with { Key = "row:" + (c.IsBlank ? i.ToString(CultureInfo.InvariantCulture) : c.Uri) };
    }

    // ══ Home.Tile.Daylist ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The registered template of <see cref="ViewKeys.HomeTileDaylist"/>: its band is today's daylist strip.</summary>
    public static Element Daylist(HomeDaylistTileProps p, in ViewContext ctx)
    {
        var f = p.Frame;
        if (f.Form == HomeTileForm.Full) return Tile(f, p.Card, plate: false);
        var card = p.Zone.Items.Count > 0 ? p.Zone.Items[0] : default;
        string title = card.Title ?? "";
        Action open = p.Play ?? (() => { if (!card.IsBlank) HomeCardNav.Open(in card); });
        if (f.Form == HomeTileForm.M || f.Form == HomeTileForm.S)
            return Tile(f, Clickable(new BoxEl
            {
                Grow = 1f, Direction = 1, ZStack = true,
                Children = f.Form == HomeTileForm.S ? [Art(f, card.ImageUrl)] : [Art(f, card.ImageUrl), ScrimName(() => title, p.Eyebrow)],
            }, open));
        return Tile(f, Clickable(new BoxEl
        {
            Grow = 1f, Direction = 0, MinWidth = 0f, AlignItems = FlexAlign.Stretch,
            Children = [Square(f, Art(f, card.ImageUrl)), Panel(() => title, p.Eyebrow)],
        }, open));
    }

    // ══ Home.Tile.Section ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The registered template of <see cref="ViewKeys.HomeTileSection"/>: the band is today's chapter; a medium tile is a mosaic of its first
    /// covers with its title over the scrim, a wide one four covers across.</summary>
    public static Element Section(HomeSectionTileProps p, in ViewContext ctx)
    {
        var f = p.Frame;
        var zone = p.Zone;
        if (f.Form == HomeTileForm.Full) return Tile(f, ZuneHomePage.Body(zone, f.Overlay, ZuneHomeTiers.Of(ctx.Tier())), plate: false);
        string title = zone.Title ?? f.Entry.Title;
        bool wide = f.Form == HomeTileForm.L;
        int n = f.Form == HomeTileForm.S ? 1 : 4;
        var covers = new List<Element>(n);
        for (int i = 0; i < n; i++)
            covers.Add(new BoxEl { Grow = 1f, Basis = 0f, Shrink = 1f, MinWidth = 0f, Direction = 1, Children = [Art(f, i < zone.Items.Count ? zone.Items[i].ImageUrl : null)] });
        Element mosaic = wide || n == 1
            ? new BoxEl { Grow = 1f, Direction = 0, MinWidth = 0f, AlignItems = FlexAlign.Stretch, Children = covers.ToArray() }
            : new BoxEl
            {
                Grow = 1f, Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch,
                Children =
                [
                    new BoxEl { Grow = 1f, Basis = 0f, Direction = 0, AlignItems = FlexAlign.Stretch, Children = [covers[0], covers[1]] },
                    new BoxEl { Grow = 1f, Basis = 0f, Direction = 0, AlignItems = FlexAlign.Stretch, Children = [covers[2], covers[3]] },
                ],
            };
        var face = new BoxEl
        {
            Grow = 1f, Direction = 1, ZStack = true,
            Children = n == 1 ? [mosaic] : [mosaic, ScrimName(() => title)],
        };
        return Tile(f, p.SeeAll is { } open ? Clickable(face, open) : face);
    }

    // ══ Home.Tile.Vacant ════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The registered template of <see cref="ViewKeys.HomeTileVacant"/>: a flat plate at the box while loading (never a pulse, owner rule
    /// 11-15); its glyph and a quiet word when the thing is gone, paused, offline or unknown.</summary>
    public static Element Vacant(HomeTileVacantProps p, in ViewContext ctx)
    {
        var f = p.Frame;
        if (p.Reason == HomeTileVacancy.Loading) return Tile(f, new BoxEl { Grow = 1f, Fill = R.Layer.ArtPlaceholder });
        string word = Loc.Get(p.Reason switch
        {
            HomeTileVacancy.Unavailable => Strings.Theme.Zune.Home.Unavailable,
            HomeTileVacancy.Paused => Strings.Theme.Zune.Home.Paused,
            HomeTileVacancy.Offline => Strings.Theme.Zune.Home.Offline,
            _ => Strings.Theme.Zune.Home.Unknown,
        });
        string name = p.Name;
        Element face = f.Form == HomeTileForm.S
            ? SmallWidget(Icons.Info, () => "")
            : new BoxEl
            {
                Direction = 1, Grow = 1f, MinWidth = 0f, Justify = FlexJustify.End, Gap = B.TextGap, Padding = new Edges4(B.TilePad, B.TilePad, B.BadgeLane, B.TilePad),
                Children =
                [
                    Ui.Icon(Icons.Info, B.HeadGlyph, Tok.TextTertiary) with { Shrink = 0f },
                    R.Type.CardTitle(name) with { MaxLines = 2, Wrap = TextWrap.Wrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                    R.Type.DenseMeta(word) with { Color = Tok.TextSecondary, Case = R.Case.For(LabelKind.Label), MaxLines = 1, MinWidth = 0f },
                ],
            };
        return Tile(f, face);
    }
}
