// ── Themes/Classic/Structure/Views/Pages/ClassicLibraryPage.Nav.cs — the library navigator's body (themes plan 6.1, T10c1) ──────────────────────
//
// From `Entities\User.Page.Library.cs` (`ListBody`, `NavList`, the shimmer, the slot template) and `Entities\User.UI.cs` §4 and §6 (the letter header, the bound rows and cards and their
// grid selection chrome): the navigator as ONE skeleton boundary — eight shimmer rows while the relation has not answered, the no-match vacancy for a filter that matched nothing (the
// engine's own Empty arm), otherwise the bound list, and the answer CROSS-DISSOLVES into the rows (`SkelReveal.StaggerRows`) instead of swapping hard.
//
// ZERO ALLOCATION ON A SCROLL FRAME. Every navigator row is an `ItemsView.CreateBound` slot: the template runs ONCE per slot and every per-item value is a bind over
// `BoundItemScope<LibraryNavItem>` (a slot + its row version), so a recycle rewrites the slot's item signal and re-fires only those binds — no `List<Element>` per row, no remount.
// Handlers resolve the CURRENT item at invocation (`Peek`), never a mount-time capture.
//
// The host owns the mount: the shape memo, the selection pre-write, the cached layout and options per mount key (`LibraryNavMountPlan`). The view picks the slot template of the mount's kind and
// draws the body, the sticky letter and the strip.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Classic;

internal static partial class ClassicLibraryPage
{
    /// <summary>The navigator, as ONE skeleton boundary: eight shimmer rows while the relation has not answered, the no-match vacancy for a filter that matched nothing, otherwise the
    /// bound list.</summary>
    static Element ListBody(LibraryPageProps p, PageCache c)
        => Skel.Region(p.Navigator.Load, c.NavShimmer!, c.NavContent!, SkelReveal.StaggerRows,
                       isEmpty: static n => n == 0, onEmpty: c.NavNoMatch!, group: p.SkelGroup);

    /// <summary>The navigator's shimmer (tuned Classic X2), in the persisted view's own shape so the content lands where the bars stood. A list: the bound
    /// row's geometry — the plate (56, or 40 compact) at the 60 / 44 pitch, the 40 art at the row's corner and the title and subtitle lines on their
    /// rungs (the stock list-item shape stood 66 apart and drifted 6 DIP a row). A grid: the bound cards at the fitted grid's own cells (it used to draw
    /// rows over a grid). <paramref name="fullBase"/> is the full grid's size-S floor of the tier (the record's <c>CardCellMin</c>, the host's mount floor) and
    /// <paramref name="gaps"/> its gaps (the record's <c>GridGaps</c>, the host's mount gaps).</summary>
    internal static Element NavShimmer(int view, int size, EntityKind entity, float fullBase = LibraryNavMount.CardCellMinFull, LibraryGridGaps? gaps = null)
    {
        bool compact = User.IsCompactView(view);
        if (User.IsGridView(view)) return NavGridShimmer(compact, LibraryNavMount.CellMinFor(view, size, fullBase), entity == EntityKind.Artist, gaps ?? LibraryGridGaps.Core);
        var rows = new Element[ClassicLibraryRules.NavSkelRows];
        for (int i = 0; i < rows.Length; i++) rows[i] = NavShimmerRow(compact) with { Key = "navskel:" + i };
        return new BoxEl { Key = "navskel", Direction = 1, Children = rows };
    }

    static Element NavShimmerRow(bool compact)
    {
        Element title = new TextEl("Loading title")   // loc-allow: skeleton shimmer template — the leaf only sizes the bar
        {
            Size = ClassicLibraryRules.RowTitleSize, LineHeight = ClassicLibraryRules.RowTitleLine, Width = ClassicLibraryRules.NavSkelTitleW,
        };
        Element text = new BoxEl
        {
            Direction = 1, Grow = 1f, Basis = 0f, Gap = 1f, MinWidth = 0f,
            Children = compact
                ? [title]
                : [title, new TextEl("Loading subtitle")   // loc-allow: skeleton shimmer template
                {
                    Size = ClassicLibraryRules.RowSubtitleSize, LineHeight = ClassicLibraryRules.RowSubtitleLine, Width = ClassicLibraryRules.NavSkelSubtitleW,
                }],
        };
        return new BoxEl
        {
            Height = compact ? User.NavRowCompactPlate : User.NavRowPlate,
            Margin = new Edges4(ClassicLibraryRules.NavSkelPlateMarginX, User.NavRowMarginY, ClassicLibraryRules.NavSkelPlateMarginX, User.NavRowMarginY),
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, Padding = ClassicLibraryRules.NavSkelContentPad,
            Children = compact
                ? [text]
                : [new BoxEl
                {
                    Width = ClassicLibraryRules.RowArt, Height = ClassicLibraryRules.RowArt, Shrink = 0f,
                    Corners = CornerRadius4.All(ClassicLibraryRules.RowArtCorner), Fill = Tok.FillSubtleSecondary,
                }, text],
        };
    }

    /// <summary>The grid view's shimmer: <see cref="NavCard"/>'s shape at the fitted grid's cells — an auto-fill grid at the mount's cell minimum and gap
    /// (the column count and the cell width are <see cref="RepeatLayout.GridFit"/>'s formula, <c>GridEl.AutoFillColumnCount</c>), inside the list's
    /// <see cref="NavGridPad"/>, each card the fill-width square cover in its padding (16 round, 8 square) and, outside the compact grid, the title line.</summary>
    static Element NavGridShimmer(bool compact, float cellMin, bool circular, LibraryGridGaps gaps)
    {
        var cards = new Element[ClassicLibraryRules.NavSkelCards];
        for (int i = 0; i < cards.Length; i++) cards[i] = NavShimmerCard(compact, circular) with { Key = "navskel:" + i };
        return new BoxEl
        {
            Key = "navskel", Direction = 1, Padding = NavGridPad,
            Children = [new GridEl { MinColWidth = cellMin, ColGap = gaps.Column, RowGap = gaps.Row, Children = cards }],
        };
    }

    static Element NavShimmerCard(bool compact, bool circular)
    {
        // The cover is the media leaf itself (the deriver keeps an ImageEl's fluid square and paints only its placeholder).
        Element art = new ImageEl
        {
            Source = "", Fit = ImageFit.Cover, AspectRatio = 1f,
            Corners = CornerRadius4.All(circular ? Radii.Full : ClassicLibraryRules.CardArtCorner),
        };
        Element content = new BoxEl
        {
            Direction = 1, Gap = Spacing.S, ClipToBounds = true, Padding = Edges4.All(circular ? ClassicLibraryRules.CardPadRound : Spacing.S),
            Children = compact
                ? [art]
                // The title's LINE, not its bar: a text leaf derives to a bar shorter than its line, which would land every card short.
                : [art, new BoxEl
                {
                    Height = ClassicLibraryRules.CardTitleLine, Direction = 0, AlignItems = FlexAlign.Center,
                    AlignSelf = circular ? FlexAlign.Center : FlexAlign.Start,
                    Children =
                    [
                        new TextEl("Loading title")   // loc-allow: skeleton shimmer template — the leaf only sizes the bar
                        {
                            Size = ClassicLibraryRules.CardTitleSize, LineHeight = ClassicLibraryRules.CardTitleLine, Width = ClassicLibraryRules.NavSkelCardTitleW,
                        },
                    ],
                }],
        };
        return new BoxEl { Direction = 1, Children = [content] };
    }

    /// <summary>A grid's wrapper padding around its cells (a list has none): the bound grid's and its shimmer's.</summary>
    static Edges4 NavGridPad => new(Spacing.S, Spacing.S, Spacing.S, 0f);

    /// <summary>The real navigator (library-stabilization-plan §3.3, A1). The ROOT returned here is the region's single-child slot — UNKEYED and the SAME shape (a plain row `BoxEl`) every
    /// single render, forever, because a `Key` in that slot is INERT (RC1: <c>ReconcileSkeletonRegion</c> pairs a region's content root by <c>ElementTypeId</c> alone, never by
    /// <c>Key</c>). Everything that must actually remount is a KEYED CHILD, one level down, where <c>ReconcileChildren</c> honors keys: the nav list itself (<c>mount.Key</c>), the sticky
    /// letter ("nav:sticky", present only while <c>Mount.Lettered</c>) and the jump strip ("nav:strip", present only while <c>Mount.Strip</c> — a–z in ANY view, not just the lettered
    /// list). None of the three ever changes ELEMENT TYPE across a render, so the reconciler never has a chance to reuse one node as another.</summary>
    static Element NavList(PageCache c)
    {
        var nav = c.Props.Navigator;
        var plan = nav.Mount();                          // the ONLY reactive read: the mount rides the shape memo
        var mount = plan.Mount;
        Element list = new BoxEl
        {
            Key = mount.Key, OnRealized = nav.OnMounted,
            Grow = 1f, Direction = 1, MinHeight = 0f,
            Padding = mount.GridPadding ? NavGridPad : default,
            Children = [ItemsView.CreateBound(nav.Items, c.TemplateOf(mount.Template), plan.Layout, plan.Options)],
        };
        Element body = new BoxEl
        {
            Key = "nav:body", Grow = 1f, Basis = 0f, MinWidth = 0f, MinHeight = 0f, ZStack = true, ClipToBounds = true,
            Children = mount.Lettered
                ? [list, c.Sticky ??= ClassicLetterKit.StickyLetter(nav.StickyLetter, nav.StickyPush) with { Key = "nav:sticky" }]
                : [list],
        };
        return new BoxEl                                  // the region's single child: unkeyed, shape-stable
        {
            Direction = 0, Grow = 1f, MinHeight = 0f, AlignItems = FlexAlign.Stretch,
            Children = mount.Strip
                ? [body, c.Strip ??= ClassicLetterKit.JumpStrip(nav.Present, nav.StickyLetter, nav.Jump) with { Key = "nav:strip" }]
                : [body],
        };
    }

    /// <summary>The list slot: the template runs ONCE per recycled slot and branches at BUILD time on the pooled content type (Recents' `ClassicRecentsRowSlot` idiom) — a header plate and a row
    /// never share a pool.</summary>
    static Element NavSlot(BoundItemScope<LibraryNavItem> scope, EntityKind kind, bool compact)
        => scope.Item.Peek().Slot < Table.None ? LetterHeader(scope) : NavRow(scope, kind, compact);

    /// <summary>The inline header item (the flat projection's <c>ContentType 1</c>, extent <see cref="LibraryLetters.HeaderExtent"/>): the letter on a small card plate. A header row
    /// carries <c>Slot = -(letter + 1)</c>, so the plate reads its letter out of the slot and nothing else.</summary>
    static Element LetterHeader(BoundItemScope<LibraryNavItem> scope) => new BoxEl
    {
        Height = LibraryLetters.HeaderExtent, Direction = 0, AlignItems = FlexAlign.End, HitTestVisible = false,
        Padding = new Edges4(Spacing.S, 0f, Spacing.S, Spacing.XS),
        Children = [ClassicLetterKit.LetterPlate(scope.Text(static it => ClassicLetterKit.LetterText(-it.Slot - 1)))],
    };

    // ══ THE BOUND ROWS AND CARDS (W1-W5, W19, W22) ═══════════════════════════════════════════════════════════════════

    /// <summary>A navigator LIST row (plate 40 compact / 56, outer extent 44 / 60): 40×40 art (r 20 artist / 5 album, show) + title 14/20/600 + subtitle 12/16 — compact drops both the art
    /// and the subtitle. Wears the bound AccentPill chrome.</summary>
    static Element NavRow(BoundItemScope<LibraryNavItem> scope, EntityKind kind, bool compact)
    {
        bool circular = kind == EntityKind.Artist;
        Element text = new BoxEl
        {
            Direction = 1, Grow = 1f, Basis = 0f, Gap = 1f, MinWidth = 0f,
            Children = compact
                ? [TitleText(scope, ClassicLibraryRules.RowTitleSize, ClassicLibraryRules.RowTitleLine), new BoxEl()]
                : [TitleText(scope, ClassicLibraryRules.RowTitleSize, ClassicLibraryRules.RowTitleLine), NavSubtitle(scope, kind)],
        };
        Element content = new BoxEl
        {
            Direction = 0, Grow = 1f, AlignItems = FlexAlign.Center, Gap = Spacing.M, Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f),
            Draggable = User.DragOf(scope),
            Children = compact
                ? [text]
                : [ArtBox(scope, ClassicLibraryRules.RowArt, circular ? ClassicLibraryRules.RowArt / 2f : ClassicLibraryRules.RowArtCorner), text],
        };
        // The PLATE height, not the row extent: the chrome's own 2+2 margin is what makes the outer extent the seed (`User.NavRowExtent`). Setting the extent here would measure 60 + 4 and
        // drift the letters by 4 DIP a row.
        return SelectorVisualsBound.AccentPill(scope.Row, content) with
        {
            Cursor = CursorId.Hand, Height = compact ? User.NavRowCompactPlate : User.NavRowPlate,
        };
    }

    /// <summary>A navigator GRID card: the fill-width square cover (r Full artist / 6) + title 12/16/600 (compact drops it), pad 16 artist / 8. Wears the bound Border chrome.</summary>
    static Element NavCard(BoundItemScope<LibraryNavItem> scope, EntityKind kind, bool compact)
    {
        bool circular = kind == EntityKind.Artist;
        float pad = circular ? ClassicLibraryRules.CardPadRound : Spacing.S;
        Element art = FillArt(scope, circular ? Radii.Full : ClassicLibraryRules.CardArtCorner);
        Element content = new BoxEl
        {
            Direction = 1, Gap = Spacing.S, ClipToBounds = true, Padding = Edges4.All(pad), Draggable = User.DragOf(scope),
            Children = compact
                ? [art]
                : [art, TitleText(scope, ClassicLibraryRules.CardTitleSize, ClassicLibraryRules.CardTitleLine) with { AlignSelf = circular ? FlexAlign.Center : FlexAlign.Start }],
        };
        return BorderChrome(scope.Row, content);
    }

    static TextEl TitleText(in BoundItemScope<LibraryNavItem> scope, float size, float line)
        => new(scope.Text(static it => LibraryRows.TitleOf(it.Kind, it.Slot)))
        {
            Size = size, LineHeight = line, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
        };

    /// <summary>The navigator row's second line. The TEXT is the core's (<see cref="User.NavSubtitleText"/>: an artist row says what the library HOLDS of them, an album row "artist · year", a
    /// show row its publisher — every one a bind through a HOISTED cache, so a recycle looks a string up and never formats); the line's type is the row's.</summary>
    static TextEl NavSubtitle(in BoundItemScope<LibraryNavItem> scope, EntityKind kind) => new(User.NavSubtitleText(scope, kind))
    {
        Size = ClassicLibraryRules.RowSubtitleSize, LineHeight = ClassicLibraryRules.RowSubtitleLine, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
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
        Source = scope.Image(static it => User.ArtOf(it)), Fit = ImageFit.Cover, AspectRatio = 1f, DecodePx = ClassicLibraryRules.CardDecodePx,
        Corners = CornerRadius4.All(corners),
        Placeholder = scope.Color(static it => Design.PlaceholderFor(User.ArtOf(it))),
    };

    /// <summary>The ItemContainer BORDER selection chrome, bound (W19 right): a 3-DIP accent ring + a 1-DIP inner stroke inset 2 revealed by the slot's IsSelected predicate (167 ms brush
    /// fade), the subtle hover/press plate above the content, and the container's tap/keys/focus wiring — shape-stable, so selection never re-renders a card.</summary>
    static BoxEl BorderChrome(in RowScope row, Element content)
    {
        Func<bool> isSel = row.IsSelected;
        var interact = row.OnInteraction;
        return new BoxEl
        {
            ZStack = true, Corners = Radii.ControlAll, Fill = Tok.FillSubtleTransparent,
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
                    Key = "ic-ring", HitTestVisible = false, Corners = Radii.ControlAll, BorderWidth = ItemContainer.SelectionVisualThickness,
                    BorderColor = Prop.Of(() => isSel() ? Tok.AccentDefault : ColorF.Transparent), BrushTransitionMs = Design.Motion.Fast,
                },
                new BoxEl
                {
                    Key = "ic-common", HitTestVisible = false, Corners = Radii.ControlAll,
                    HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary, HoverDurationMs = 0f, PressDurationMs = 0f,
                    Margin = Edges4.All(ItemContainer.SelectedInnerMargin), BorderWidth = ItemContainer.SelectedInnerThickness,
                    BorderColor = Prop.Of(() => isSel() ? Tok.FillControlSolid : ColorF.Transparent),
                },
            ],
        };
    }
}
