// ── Themes/Zune/Structure/Views/Library/ZuneLibraryPage.Nav.cs — the library navigator's body (themes plan 6.3; P5 Z7) ────────────────────────
//
// Boards Z-Library-*: the navigator is the page's main column. A GRID is covers with the name under each (13) and the line under that (12, secondary), the lit one
// ringed in the accent 2 outside its cover; a LIST is rows of art, name and line, the lit one on a faint plate with its name in the accent. The artists' list is the
// board's letter · name · count column: the letter heads each group (the core's inline headers, drawn as the 20 light numeral), the count is the row's line at the end.
//
// ZERO ALLOCATION ON A SCROLL FRAME. Every navigator row is an `ItemsView.CreateBound` slot: the template runs ONCE per slot and every per-item value is a bind over
// `BoundItemScope<LibraryNavItem>` (a slot + its row version), so a recycle rewrites the slot's item signal and re-fires only those binds. Handlers resolve the
// CURRENT item at invocation, never a mount-time capture.
//
// The host owns the mount: the shape memo, the selection pre-write, the cached layout and options per mount key (`LibraryNavMountPlan`). A row keeps the host's pitch
// (`User.NavRowPlate` in a `User.NavRowMarginY` margin, the same outer extent the letters' arithmetic sums at); a card is measured by the fitted grid.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using LR = Wavee.Themes.Zune.ZuneLibraryRules;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Zune;

internal static partial class ZuneLibraryPage
{
    /// <summary>The navigator, as ONE skeleton boundary: shimmer rows while the relation has not answered, the no-match vacancy for a filter that matched nothing, otherwise the
    /// bound list.</summary>
    static Element ListBody(LibraryPageProps p, PageCache c)
        => Skel.Region(p.Navigator.Load, c.NavShimmer!, c.NavContent!, SkelReveal.StaggerRows,
                       isEmpty: static n => n == 0, onEmpty: c.NavNoMatch!, group: p.SkelGroup);

    // ══ THE SHIMMER ══════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The navigator's shimmer, in the persisted view's own shape so the content lands where the bars stood. A list: the bound row's geometry (the plate at the host's
    /// pitch, the 40 art, the title and subtitle lines). A grid: the cards at the fitted grid's own cells. <paramref name="fullBase"/> is the full grid's size-S floor of the tier
    /// (NaN: the core's) and <paramref name="gaps"/> its gaps.</summary>
    internal static Element NavSkeleton(int view, int size, EntityKind entity, float fullBase, LibraryGridGaps gaps)
    {
        bool compact = User.IsCompactView(view);
        if (User.IsGridView(view)) return NavGridShimmer(compact, LibraryNavMount.CellMinFor(view, size, fullBase), entity == EntityKind.Artist, gaps);
        var rows = new Element[LR.NavSkelRows];
        for (int i = 0; i < rows.Length; i++) rows[i] = NavShimmerRow(compact, entity == EntityKind.Artist) with { Key = "navskel:" + i };
        return new BoxEl { Key = "navskel", Direction = 1, Children = rows };
    }

    static Element NavShimmerRow(bool compact, bool circular)
    {
        Element title = R.Type.TrackTitle("Loading title") with { Width = LR.NavSkelTitleW };   // loc-allow: skeleton shimmer template: the leaf only sizes the bar
        Element text = new BoxEl
        {
            Direction = 1, Grow = 1f, Basis = 0f, Gap = 1f, MinWidth = 0f,
            Children = compact
                ? [title]
                : [title, R.Type.MicroMeta("Loading subtitle") with { Width = LR.NavSkelSubtitleW }],   // loc-allow: skeleton shimmer template
        };
        return new BoxEl
        {
            Height = compact ? User.NavRowCompactPlate : User.NavRowPlate,
            Margin = new Edges4(0f, User.NavRowMarginY, 0f, User.NavRowMarginY),
            Direction = 0, AlignItems = FlexAlign.Center, Gap = LR.RowGap, Padding = new Edges4(LR.RowPadX, 0f, LR.RowPadX, 0f),
            Children = compact
                ? [text]
                : [new BoxEl
                {
                    Width = LR.RowArt, Height = LR.RowArt, Shrink = 0f,
                    Corners = circular ? CornerRadius4.All(LR.RowArt) : R.Shape.All(ShapeRole.Cover), Fill = Tok.FillSubtleSecondary,
                }, text],
        };
    }

    /// <summary>The grid view's shimmer: <see cref="NavCard"/>'s shape at the fitted grid's own cells (the column count and cell width are <see cref="RepeatLayout.GridFit"/>'s
    /// formula, <c>GridEl.AutoFillColumnCount</c>), each card the fill-width square cover and, outside the compact grid, the name line.</summary>
    static Element NavGridShimmer(bool compact, float cellMin, bool circular, LibraryGridGaps gaps)
    {
        var cards = new Element[LR.NavSkelCards];
        for (int i = 0; i < cards.Length; i++) cards[i] = NavShimmerCard(compact, circular) with { Key = "navskel:" + i };
        return new BoxEl
        {
            Key = "navskel", Direction = 1,
            Children = [new GridEl { MinColWidth = cellMin, ColGap = gaps.Column, RowGap = gaps.Row, Children = cards }],
        };
    }

    static Element NavShimmerCard(bool compact, bool circular)
    {
        // The cover is the media leaf itself (the deriver keeps an ImageEl's fluid square and paints only its placeholder).
        Element art = new ImageEl
        {
            Source = "", Fit = ImageFit.Cover, AspectRatio = 1f,
            Corners = circular ? CornerRadius4.All(R.Shape.For(ShapeRole.Avatar)) : R.Shape.All(ShapeRole.Cover),
        };
        Element content = new BoxEl
        {
            Direction = 1, Gap = LR.CardNameGap, ClipToBounds = true,
            Children = compact
                ? [art]
                : [art, R.Type.DenseMeta("Loading title") with { Width = LR.NavSkelCardNameW }],   // loc-allow: skeleton shimmer template: the leaf only sizes the bar
        };
        return new BoxEl { Direction = 1, Children = [content] };
    }

    // ══ THE BOUND LIST ═══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The real navigator. The ROOT returned here is the region's single-child slot: UNKEYED and the SAME shape (a plain row `BoxEl`) every single render, because a `Key`
    /// in that slot is INERT. Everything that must actually remount is a KEYED CHILD, one level down: the nav list itself (<c>mount.Key</c>), the sticky letter ("nav:sticky",
    /// present only while <c>Mount.Lettered</c>) and the jump strip ("nav:strip", present only while <c>Mount.Strip</c>). None of the three ever changes ELEMENT TYPE across a
    /// render, so the reconciler never has a chance to reuse one node as another.</summary>
    static Element NavList(PageCache c)
    {
        var nav = c.Props.Navigator;
        var plan = nav.Mount();                          // the ONLY reactive read: the mount rides the shape memo
        var mount = plan.Mount;
        Element list = new BoxEl
        {
            Key = mount.Key, OnRealized = nav.OnMounted,
            Grow = 1f, Direction = 1, MinHeight = 0f,
            Children = [ItemsView.CreateBound(nav.Items, c.TemplateOf(mount.Template), plan.Layout, plan.Options)],
        };
        Element body = new BoxEl
        {
            Key = "nav:body", Grow = 1f, Basis = 0f, MinWidth = 0f, MinHeight = 0f, ZStack = true, ClipToBounds = true,
            Children = mount.Lettered
                ? [list, c.Sticky ??= ZuneProfileLetters.StickyLetter(nav.StickyLetter, nav.StickyPush)]
                : [list],
        };
        return new BoxEl                                  // the region's single child: unkeyed, shape-stable
        {
            Direction = 0, Grow = 1f, MinHeight = 0f, AlignItems = FlexAlign.Stretch,
            Children = mount.Strip
                ? [body, c.Strip ??= ZuneProfileLetters.JumpStrip(nav.Present, nav.StickyLetter, nav.Jump)]
                : [body],
        };
    }

    /// <summary>The list slot: the template runs ONCE per recycled slot and branches at BUILD time on the pooled content type (a header and a row never share a pool).</summary>
    static Element NavSlot(BoundItemScope<LibraryNavItem> scope, EntityKind kind, bool compact, Func<bool>? hideSelection = null)
        => scope.Item.Peek().Slot < Table.None ? LetterHeader(scope) : NavRow(scope, kind, compact, hideSelection);

    /// <summary>The inline header item (the flat projection's <c>ContentType 1</c>, extent <see cref="LibraryLetters.HeaderExtent"/>): the letter, read out of the slot.</summary>
    static Element LetterHeader(BoundItemScope<LibraryNavItem> scope) => new BoxEl
    {
        Height = LibraryLetters.HeaderExtent, Direction = 0, AlignItems = FlexAlign.End, HitTestVisible = false,
        Padding = new Edges4(LR.RowPadX, 0f, LR.RowPadX, 0f),
        Children = [ZuneProfileLetters.Glyph(scope.Text(static it => ZuneProfileLetters.LetterText(-it.Slot - 1)))],
    };

    // ══ THE BOUND ROWS AND CARDS ═════════════════════════════════════════════════════════════════════════════════════

    /// <summary>A navigator LIST row (plate 40 compact / 56): the 40 art (a circle for an artist) + the name + the line under it; compact drops both the art and the line,
    /// except an artist's, which stays at the end of the row as the count (the board's letter · name · count). Wears the bound plate chrome.</summary>
    static Element NavRow(BoundItemScope<LibraryNavItem> scope, EntityKind kind, bool compact, Func<bool>? hideSelection)
    {
        bool circular = kind == EntityKind.Artist;
        Func<bool> sel = Lit(scope.Row.IsSelected, hideSelection);
        Element text = new BoxEl
        {
            Direction = 1, Grow = 1f, Basis = 0f, Gap = 1f, MinWidth = 0f, Justify = FlexJustify.Center,
            Children = compact ? [TitleText(scope, sel)] : [TitleText(scope, sel), NavSubtitle(scope, kind)],
        };
        var kids = new List<Element>(3);
        if (!compact) kids.Add(ArtBox(scope, LR.RowArt, circular ? R.Shape.For(ShapeRole.Avatar) : R.Shape.For(ShapeRole.Cover)));
        kids.Add(text);
        if (compact && circular) kids.Add(NavSubtitle(scope, kind) with { Grow = 0f, Shrink = 0f, MinWidth = 0f });
        // Beside the pinned "your episodes" row a followed show with news carries the accent dot (Z-Library-Podcasts: unplayed, published after the last play).
        if (kind == EntityKind.Show && hideSelection is not null)
            kids.Add(new BoxEl
            {
                Width = LR.NewDot, Height = LR.NewDot, Shrink = 0f, Corners = Radii.Circle(LR.NewDot), HitTestVisible = false,
                Fill = Prop.Of(() => User.ShowHasNew(scope.Item.Value.Slot) ? Tok.AccentDefault : ColorF.Transparent),
            });
        Element content = new BoxEl
        {
            Direction = 0, Grow = 1f, AlignItems = FlexAlign.Center, Gap = LR.RowGap, Padding = new Edges4(LR.RowPadX, 0f, LR.RowPadX, 0f),
            Draggable = User.DragOf(scope), Children = kids.ToArray(),
        };
        // The PLATE height, not the row extent: the chrome's own margin is what makes the outer extent the host's seed. Setting the extent here would measure the plate plus the margin.
        return RowChrome(scope.Row, content, compact ? User.NavRowCompactPlate : User.NavRowPlate, sel);
    }

    /// <summary>A navigator GRID card: the fill-width square cover + the name (13) and its line (12); compact drops both. The selection is the accent ring outside the cover.</summary>
    static Element NavCard(BoundItemScope<LibraryNavItem> scope, EntityKind kind, bool compact)
    {
        bool circular = kind == EntityKind.Artist;
        Func<bool> sel = scope.Row.IsSelected;
        // The selection is the accent ring on the cover alone (the board draws it as a 2 outline just outside; the list clips at the column's edge, so it is drawn on the
        // cover's own edge, never clipped); the name under it stays clear of it.
        Element art = new BoxEl
        {
            ZStack = true,
            Children =
            [
                FillArt(scope, circular ? R.Shape.For(ShapeRole.Avatar) : R.Shape.For(ShapeRole.Cover)),
                new BoxEl
                {
                    Key = "ic-ring", HitTestVisible = false,
                    Corners = circular ? CornerRadius4.All(R.Shape.For(ShapeRole.Avatar)) : R.Shape.All(ShapeRole.Cover), BorderWidth = LR.CardRing,
                    BorderColor = Prop.Of(() => sel() ? Tok.AccentDefault : ColorF.Transparent), BrushTransitionMs = Design.Motion.Fast,
                },
            ],
        };
        Element content = new BoxEl
        {
            Direction = 1, Gap = LR.CardNameGap, Draggable = User.DragOf(scope),
            Children = compact
                ? [art]
                : [art, new BoxEl
                {
                    Direction = 1, Gap = 1f, MinWidth = 0f, AlignItems = circular ? FlexAlign.Center : FlexAlign.Start,
                    Children = [CardName(scope, sel), NavSubtitle(scope, kind)],
                }],
        };
        return CardChrome(scope.Row, content);
    }

    /// <summary>A row's lit state, dark while <paramref name="hide"/> says another body owns the highlight (the pinned "your episodes" row).</summary>
    static Func<bool> Lit(Func<bool> selected, Func<bool>? hide) => hide is null ? selected : () => selected() && !hide();

    static TextEl TitleText(in BoundItemScope<LibraryNavItem> scope, Func<bool> selected)
        => R.Type.TrackTitle("") with
        {
            Text = scope.Text(static it => LibraryRows.TitleOf(it.Kind, it.Slot)),
            Color = Prop.Of(() => selected() ? Tok.AccentTextPrimary : Tok.TextPrimary),
            MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
        };

    static TextEl CardName(in BoundItemScope<LibraryNavItem> scope, Func<bool> selected)
        => R.Type.DenseMeta("") with
        {
            Text = scope.Text(static it => LibraryRows.TitleOf(it.Kind, it.Slot)),
            Color = Prop.Of(() => selected() ? Tok.AccentTextPrimary : Tok.TextPrimary),
            MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
        };

    /// <summary>The navigator row's line under the name. The TEXT is the core's (<see cref="User.NavSubtitleText"/>: an artist row says what the library HOLDS of them, an album
    /// row "artist · year", a show row its publisher: every one a bind through a HOISTED cache); the line's type is the row's.</summary>
    static TextEl NavSubtitle(in BoundItemScope<LibraryNavItem> scope, EntityKind kind) => R.Type.MicroMeta("") with
    {
        Text = User.NavSubtitleText(scope, kind), Color = Tok.TextSecondary, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
    };

    static Element ArtBox(in BoundItemScope<LibraryNavItem> scope, float edge, float corners) => new BoxEl
    {
        Width = edge, Height = edge, Shrink = 0f, Corners = CornerRadius4.All(corners), ClipToBounds = true,
        Children =
        [
            new ImageEl
            {
                Source = scope.Image(static it => User.ArtOf(it)), Width = edge, Height = edge, Fit = ImageFit.Cover,
                Corners = CornerRadius4.All(corners), DecodePx = edge,
                Placeholder = scope.Color(static it => Design.PlaceholderFor(User.ArtOf(it))),
            },
        ],
    };

    static Element FillArt(in BoundItemScope<LibraryNavItem> scope, float corners) => new ImageEl
    {
        Source = scope.Image(static it => User.ArtOf(it)), Fit = ImageFit.Cover, AspectRatio = 1f, DecodePx = LR.CardDecodePx,
        Corners = CornerRadius4.All(corners),
        Placeholder = scope.Color(static it => Design.PlaceholderFor(User.ArtOf(it))),
    };

    // ══ THE SELECTION CHROME ═════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The list row's chrome, bound: a faint plate revealed by the slot's IsSelected predicate (a brush fade), the hover and press plates above the content, and the
    /// container's tap/keys/focus wiring: shape-stable, so selection never re-renders a row. The margin is the host's row margin (outer extent = plate + 2 x margin).</summary>
    static BoxEl RowChrome(in RowScope row, Element content, float plate, Func<bool>? lit = null)
    {
        Func<bool> isSel = lit ?? row.IsSelected;
        var interact = row.OnInteraction;
        return new BoxEl
        {
            ZStack = true, Height = plate, Margin = new Edges4(0f, User.NavRowMarginY, 0f, User.NavRowMarginY),
            Corners = R.Shape.All(ShapeRole.Cover), Fill = ColorF.Transparent,
            Focusable = false, FocusVisualMargin = Edges4.All(0f), Role = AutomationRole.Button, Cursor = CursorId.Hand,
            OnPointerReleased = args => interact(RowClickPolicy.TriggerOf(args.ClickCount, args.Mods), args.Mods),
            OnKeyDown = args =>
            {
                if (args.KeyCode == Keys.Enter) { interact(ItemContainerTrigger.EnterKey, args.Mods); args.Handled = true; }
                else if (args.KeyCode == Keys.Space && !args.IsRepeat) { interact(ItemContainerTrigger.SpaceKey, args.Mods); args.Handled = true; }
            },
            OnFocusChanged = row.OnFocusChanged,
            Children =
            [
                new BoxEl
                {
                    Key = "ic-sel", HitTestVisible = false, Corners = R.Shape.All(ShapeRole.Cover),
                    Fill = Prop.Of(() => isSel() ? Tok.FillSubtleTertiary : ColorF.Transparent), BrushTransitionMs = Design.Motion.Fast,
                },
                new BoxEl { Key = "ic-content", Direction = 1, Children = [content] },
                new BoxEl
                {
                    Key = "ic-common", HitTestVisible = false, Corners = R.Shape.All(ShapeRole.Cover),
                    HoverFill = R.Layer.ChromeHover, PressedFill = R.Layer.ChromePressed, HoverDurationMs = 0f, PressDurationMs = 0f,
                },
            ],
        };
    }

    /// <summary>The grid card's chrome, bound: the 2-DIP accent ring outside the cover revealed by the IsSelected predicate, the hover and press plates above the content, and
    /// the container's tap/keys/focus wiring.</summary>
    static BoxEl CardChrome(in RowScope row, Element content)
    {
        var interact = row.OnInteraction;
        return new BoxEl
        {
            ZStack = true, Corners = R.Shape.All(ShapeRole.Cover), Fill = ColorF.Transparent,
            Focusable = false, FocusVisualMargin = Edges4.All(0f), Role = AutomationRole.Button, Cursor = CursorId.Hand,
            OnPointerReleased = args => interact(RowClickPolicy.TriggerOf(args.ClickCount, args.Mods), args.Mods),
            OnKeyDown = args =>
            {
                if (args.KeyCode == Keys.Enter) { interact(ItemContainerTrigger.EnterKey, args.Mods); args.Handled = true; }
                else if (args.KeyCode == Keys.Space && !args.IsRepeat) { interact(ItemContainerTrigger.SpaceKey, args.Mods); args.Handled = true; }
            },
            OnFocusChanged = row.OnFocusChanged,
            Children =
            [
                new BoxEl { Key = "ic-content", Direction = 1, Children = [content] },
                new BoxEl
                {
                    Key = "ic-common", HitTestVisible = false, Corners = R.Shape.All(ShapeRole.Cover),
                    HoverFill = R.Layer.ChromeHover, PressedFill = R.Layer.ChromePressed, HoverDurationMs = 0f, PressDurationMs = 0f,
                },
            ],
        };
    }
}
