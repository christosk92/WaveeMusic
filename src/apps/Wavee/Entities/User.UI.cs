// ── Entities/User.UI.cs ────────────────────────────────────────────────────────────────────────────────────────────
// the library's row shapes and small controls: the WORD RAILS' library adapters (the navigator's sort rail, the reader's
// scope and sort rails, over `Controls.Words.Rail`) + the view toggle and its trimmed panel, the letter header / sticky
// letter overlay / A-Z jump strip, the crumb
// bar, the column grip, the BOUND navigator rows and cards (and their grid selection chrome), the search-row shapes, and
// the library mutation seam (`LibrarySeam`) the shared save/follow affordances read
//
// Role: UI
// Owner: O
// Wave: 5 (the rails/letters: the 2026-09-17 library rework, waves L1-L2)
// Budget: 600 lines (+30 % = 780)
// Spec: ch 15 §1-§6, §9 (0.2.9 LibrarySortView.cs, LibraryPage.cs row/card/grip/search-row builders); contract §2.6;
//       library-rework-implementation.md §5.2 (the rails, the letters, the strip, the two subtitle caches)
//
// DELETED IN WAVE L2 (no legacy paths — their consumers went with them): `SortViewPill` / `SortPanel` and their two
// hosts (the word rail replaced the sort pill outright), `DiscoRow` / `DiscoCard` / `DiscoSubtitle` / `AlbumCode` (the
// discography grid pane is gone; `Artist.Reader` stacks art + tracks blocks instead) and `GoToArtistLink` (the reader's
// doorway is a `Controls.Named` icon button). `SortLabelKey` / `SortLabel` / `ViewGlyph` / `IsGridView` /
// `IsCompactView` STAY: the sidebar's Library V3 shares the codes and `ViewPanel` still draws the view glyphs.
//
// MOVED IN PODCAST WAVE P1 (podcast-show-rework-implementation.md §5.5): the rail itself — `RailBar`, `RailWord`,
// `RailInk`, `WordRailHost` and the RailWord* metrics — is `Controls.Words` now, shared with the podcast reader. Only
// `RailHeight` stays here, as the alias `Artist.Reader`'s sub-rail arithmetic reads.
//
// ZERO ALLOCATION ON A SCROLL FRAME. Every navigator row is an `ItemsView.CreateBound` slot: the template runs ONCE per
// slot and every per-item value is a bind over `BoundItemScope<LibraryNavItem>` (a slot + its row version), so a
// recycle rewrites the slot's item signal and re-fires only those binds — no `List<Element>` per row, no remount.
// Handlers resolve the CURRENT item at invocation (`Peek`), never a mount-time capture.
//
// PROPS FREEZE AT MOUNT: every rail, the view toggle and its panel receive Signal INSTANCES plus per-kind constants,
// which is the only shape that is safe to freeze (ch 15 §1.2 "four places it bites", item 1).

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>One navigator / discography slot: the row's kind and slot plus its row VERSION, so a fact landing on the row
/// changes the item (and re-fires the slot's binds) even though the slot did not move.
/// <para><b>The version is the row's whole re-render trigger.</b> The bound item signal is equality-gated (engine
/// <c>BoundItemsSource.BindItem</c>), so a bind only re-fires when this record CHANGES: whatever a row's text or art
/// reads has to be inside <see cref="Version"/>. <see cref="Of"/> is the plain case — the entity's own row version —
/// and a caller whose row displays a fact off ANOTHER table folds that in itself (the navigator's
/// <c>LibraryPage.FillRowVersions</c>: the album row's billed-artist name, the artist row's release/song counts). The
/// list must NOT be remounted to refresh a row; that was the 0.2.10 remount storm.</para></summary>
public readonly record struct LibraryNavItem(EntityKind Kind, int Slot, uint Version)
{
    public static LibraryNavItem Of(EntityKind kind, int slot)
    {
        Table? table = Entities.TableFor(kind);
        return new(kind, slot, table is null || slot <= Table.None || slot >= table.Count ? 0u : table.Version[slot]);
    }
}

public readonly partial struct User
{
    // ══ 1. THE PERSISTED CODES (shared with the sidebar's Library V3 — never renumber) ═══════════════════════════════

    /// <summary>Sort code → its Title-Case loc KEY. Codes 0..4 are persisted (<c>library.&lt;kind&gt;.sort</c>); unknown →
    /// Recents. The library's own rail reads the lowercase <c>library.rail.*</c> keys through
    /// <see cref="LibraryWordRail.WordKey"/> — these labels are the SIDEBAR's (Library V3's pills share the codes).</summary>
    public static string SortLabelKey(int code) => code switch
    {
        1 => Strings.Library.Sort.RecentlyAdded,
        2 => Strings.Library.Sort.Alphabetical,
        3 => Strings.Library.Sort.Creator,
        4 => Strings.Library.Sort.ReleaseDate,
        _ => Strings.Library.Sort.Recents,
    };

    public static string SortLabel(int code) => Loc.Get(SortLabelKey(code));

    /// <summary>View code (0 CompactList · 1 List · 2 CompactGrid · 3 Grid) → its glyph (the view toggle's and the view
    /// panel's four cells; the sidebar's own switch reads the same codes).</summary>
    public static string ViewGlyph(int view) => view >= 2 ? Icons.ViewGrid : Icons.ViewList;

    public static bool IsGridView(int view) => view >= 2;
    public static bool IsCompactView(int view) => view is 0 or 2;

    // ══ 2. THE WORD RAILS (W8 — Zune's text pivot; it REPLACED the sort pill, which is deleted) ═══════════════════════
    //
    // The rail itself is `Controls.Words.Rail` (promoted for the podcast reader, podcast plan §5.5 — the metrics, the two
    // stacked runs and the underline moved with it). These three are the library's ADAPTERS: which words, which codes,
    // what a re-tap means. Their signatures are unchanged.

    /// <summary>The rail's height, kept under its old name for <c>Artist.Reader</c>'s sticky sub-rail arithmetic.</summary>
    internal const float RailHeight = Controls.Words.Height;

    /// <summary>The ink/underline fade the A–Z strip shares with the rail words: the 83-ms WinUI BrushTransition.</summary>
    static readonly FluentGpu.Animation.MotionTokenDef RailInkFade = FluentGpu.Animation.MotionTok.ControlFaster;

    /// <summary>The navigator's sort rail: the kind's words (<see cref="LibraryWordRail.WordsFor"/>) in rail order, each
    /// carrying its PERSISTED code (codes are not positions). Tapping the ACTIVE word flips the direction and a 10-px
    /// chevron after it says which way; picking another word resets to ascending. The rail holds the two Signal
    /// INSTANCES, so nothing here is frozen at mount — the words themselves are a per-kind constant and are built once.</summary>
    public static Element WordRail(EntityKind kind, Signal<int> sort, Signal<bool> desc)
    {
        var codes = LibraryWordRail.WordsFor(kind);
        var words = new Controls.Words.Word[codes.Length];
        for (int i = 0; i < codes.Length; i++)
        {
            int c = (int)codes[i];
            words[i] = new Controls.Words.Word(Loc.Bind(LibraryWordRail.WordKey(codes[i])), Code: c,
                                               Chevron: () => sort.Value == c && desc.Value);
        }
        return Controls.Words.Rail(words, sort,
            onReselect: _ => desc.Value = !desc.Peek(),
            onSelect: _ => desc.Value = false);
    }

    /// <summary>The reader's scope rail (W3/W4): "in your library" · "all releases · N" — the same words with no
    /// direction flip. The total rides a <see cref="Prop{T}"/> so the facets answering re-fires ONE text bind instead of
    /// re-rendering the rail, and the count is formatted through a hoisted cache (never inside the thunk).
    /// <para><paramref name="compact"/> is the narrow reader's word: "all · N" instead of "all releases · N" while it
    /// answers true. It is a PREDICATE read INSIDE the bound text, so a column drag across the breakpoint re-fires one
    /// text bind and never re-renders the rail; null — the default, and what the two-argument call still is — is the
    /// long word forever.</para></summary>
    public static Element ScopeRail(Signal<int> scope, Prop<int> total, Func<bool>? compact = null)
    {
        // TWO caches, one per word: ONE cache keyed on the count alone would rewrite its entry on every crossing of the
        // breakpoint (the same N, the other word), which is exactly the formatting the cache exists to avoid.
        Prop<string> all = compact is { } isCompact
            ? Prop.Of(() => isCompact()
                ? s_allShort.Get(total.Current(), static n => AllShortText(n))
                : s_allReleases.Get(total.Current(), static n => AllReleasesText(n)))
            : Prop.Of(() => s_allReleases.Get(total.Current(), static n => AllReleasesText(n)));
        // Positions ARE the scope codes: 0 = in your library, 1 = all releases.
        return Controls.Words.Rail(
        [
            new Controls.Words.Word(Loc.Bind(Strings.Library.Scope.InLibrary)),
            new Controls.Words.Word(all),
        ], scope);
    }

    static readonly FormatCache<int> s_allReleases = new(), s_allShort = new();

    static string AllReleasesText(int total) => ScopeWordText(Strings.Library.Scope.AllReleases, total);

    static string AllShortText(int total) => ScopeWordText(Strings.Library.Scope.AllShort, total);

    /// <summary>"&lt;word&gt; · N", or the bare word while the total is 0 — a facet that has not answered says nothing
    /// rather than "· 0".</summary>
    static string ScopeWordText(string key, int total)
    {
        string word = Loc.Get(key);
        return total > 0 ? word + " · " + FormatCache.Int(total) : word;
    }

    /// <summary>The reader's own sort rail: newest · oldest · a–z (the reader-local <c>Artist.ReaderSort</c> codes, NOT
    /// <see cref="LibraryNavSort"/>, and they ARE positions) — no direction flip, so a word is a plain set.</summary>
    public static Element ReaderSortRail(Signal<int> sort)
    {
        var words = new Controls.Words.Word[3];
        for (int i = 0; i < words.Length; i++) words[i] = new Controls.Words.Word(Loc.Bind(ReaderSortKey(i)));
        return Controls.Words.Rail(words, sort);
    }

    static string ReaderSortKey(int code) => code switch
    {
        1 => Strings.Library.ReaderSort.Oldest,
        2 => Strings.Library.ReaderSort.Alphabetical,
        _ => Strings.Library.ReaderSort.Newest,
    };

    // ══ 3. THE VIEW TOGGLE AND THE TRIMMED VIEW PANEL ════════════════════════════════════════════════════════════════

    /// <summary>The toggle beside the rail: list (1) / grid (3) glyphs, then "…" for the trimmed <see cref="ViewPanel"/>
    /// (the compact variants + S/M/L). View codes stay 0..3 and stay persisted; the list/grid glyphs keep whichever
    /// compactness the persisted code already carried.</summary>
    public static Element ViewToggle(Signal<int> view, Signal<int> size) => Embed.Comp(() => new ViewToggleHost(view, size));

    sealed class ViewToggleHost(Signal<int> view, Signal<int> size) : Component
    {
        NodeHandle _anchor;
        OverlayHandle? _handle;

        public override Element Render()
        {
            var overlay = UseContext(Overlay.Service);
            int v = view.Value;   // subscribe → the pressed glyph follows the persisted code

            void Flyout()
            {
                if (Controls.IsNullOverlay(overlay)) return;
                if (_handle is { IsOpen: true } open) { open.Close(); return; }
                _handle = overlay.Open(
                    () => _anchor,
                    () => ViewPanel(view, size),
                    FlyoutPlacement.BottomEdgeAlignedRight,
                    new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Popup)
                    { ConstrainToRootBounds = false });
                _handle.ClosedAction = () => _handle = null;
            }

            return new BoxEl
            {
                Direction = 0, Gap = Spacing.XXS, AlignItems = FlexAlign.Center, Shrink = 0f, OnRealized = h => _anchor = h,
                Children =
                [
                    ViewIcon(Icons.ViewList, !IsGridView(v), () => view.Value = IsCompactView(view.Peek()) ? 0 : 1),
                    ViewIcon(Icons.ViewGrid, IsGridView(v), () => view.Value = IsCompactView(view.Peek()) ? 2 : 3),
                    ViewIcon(Icons.More, false, Flyout),
                ],
            };
        }

        static Element ViewIcon(string glyph, bool on, Action tap) => new BoxEl
        {
            Width = 28f, Height = 28f, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = CornerRadius4.All(4f), Fill = on ? Tok.FillSubtleTertiary : Tok.FillSubtleTransparent,
            Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = tap,
            Children = [Icon(glyph, 15f, on ? Tok.TextPrimary : Tok.TextSecondary)],
        }.Interactive(Interaction.Subtle);
    }

    /// <summary>The old sort flyout minus its sort rows — the rail owns sorting now: "View as" + (grid only) "Size". The
    /// same rows, the same code; the <c>hasCreator</c>/<c>hasRelease</c> flags went with the deleted sort rows.</summary>
    public static Element ViewPanel(Signal<int> view, Signal<int> size) => Embed.Comp(() => new ViewPanelHost(view, size));

    sealed class ViewPanelHost(Signal<int> view, Signal<int> size) : Component
    {
        static readonly string[] SizeLabels = ["S", "M", "L"];
        static readonly float[] ViewGlyphSizes = [14f, 16f, 12f, 15f];

        public override Element Render()
        {
            int v = view.Value; _ = size.Value;   // subscribe
            // M1 (RC10 / D12): the Size header + bar used to be ADDED/REMOVED as `v` crossed list<->grid, so the OPEN
            // flyout changed height under the cursor mid-click. It is now always in the tree — same row count, same
            // Gap, every time — and for a list view (v < 2, where S/M/L does nothing, D13) the wrapper is dimmed and
            // taken out of hit-testing instead of removed, so the popup's size never moves while it is open.
            BoxEl sizeBank = new()
            {
                Direction = 1, Gap = 1f,
                Children = [PanelHeader(Loc.Get(Strings.Library.Size)), SelectorBar.Create(SizeLabels, size)],
            };
            if (v < 2) sizeBank = sizeBank with { Opacity = 0.4f, HitTestVisible = false };   // Opacity/HitTestVisible are BoxEl-only
            Element[] rows = [PanelHeader(Loc.Get(Strings.Library.ViewAs)), ViewToggles(v), sizeBank];
            // PopupChrome.Popup supplies the one WinUI FlyoutPresenter acrylic, stroke, corners and shadow.
            return new BoxEl { Direction = 1, Gap = 1f, MinWidth = 200f, Padding = Edges4.All(Spacing.XS), Children = rows };
        }

        // Glyph-only cells (ch 15 §3: the four view labels are resolved by 0.2.9 and never drawn).
        Element ViewToggles(int v)
        {
            var cells = new Element[4];
            for (int i = 0; i < 4; i++)
            {
                int idx = i; bool on = v == i;
                cells[i] = new BoxEl
                {
                    Width = 40f, Height = 30f, Grow = 1f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Corners = CornerRadius4.All(5f),
                    Fill = on ? Tok.AccentDefault : Tok.FillSubtleSecondary, HoverFill = on ? Tok.AccentSecondary : Tok.FillSubtleTertiary,
                    Role = AutomationRole.Button, Cursor = CursorId.Hand,
                    OnClick = () => view.Value = idx,
                    Children = [Icon(ViewGlyph(i), ViewGlyphSizes[i], on ? Tok.TextOnAccentPrimary : Tok.TextSecondary)],
                };
            }
            return new BoxEl { Direction = 0, Gap = 4f, Padding = new Edges4(2f, 2f, 2f, 4f), Children = cells };
        }

        static Element PanelHeader(string t) => new BoxEl
        {
            Padding = new Edges4(Spacing.S, Spacing.XS, Spacing.S, Spacing.XXS),
            Children = [Design.Type.Eyebrow(t) with { Color = Tok.TextTertiary }],
        };
    }

    // ══ 4. THE LETTERS: THE HEADER ITEM, THE STICKY OVERLAY, THE A–Z STRIP (W2) ═══════════════════════════════════════

    static readonly string[] s_letters = ["A", "B", "C", "D", "E", "F", "G", "H", "I", "J", "K", "L", "M", "N", "O", "P", "Q", "R", "S", "T", "U", "V", "W", "X", "Y", "Z"];

    /// <summary>Letter index (0 = "#", 1..26 = A..Z — <see cref="LibraryLetters.Of"/>'s space) → its glyph.</summary>
    public static string LetterText(int letter) => letter <= 0 || letter > s_letters.Length ? "#" : s_letters[letter - 1];

    /// <summary>The inline header item (the flat projection's <c>ContentType 1</c>, extent
    /// <see cref="LibraryLetters.HeaderExtent"/>): the letter on a small card plate. A header row carries
    /// <c>Slot = -(letter + 1)</c>, so the plate reads its letter out of the slot and nothing else.</summary>
    public static Element LetterHeader(BoundItemScope<LibraryNavItem> scope) => new BoxEl
    {
        Height = LibraryLetters.HeaderExtent, Direction = 0, AlignItems = FlexAlign.End, HitTestVisible = false,
        Padding = new Edges4(Spacing.S, 0f, Spacing.S, Spacing.XS),
        Children = [LetterPlate(scope.Text(static it => LetterText(-it.Slot - 1)))],
    };

    /// <summary>The pinned twin of <see cref="LetterHeader"/>: a ZStack overlay over the list, pushed by the distance the
    /// page derives from the scroll geometry (Recents' <c>StickyDayHeader</c> idiom — a bound transform, so a scroll frame
    /// never re-renders it). Always mounted; before any letter is under the top it is simply transparent, which is cheaper
    /// than collapsing it (a presence flip would relayout the overlay).</summary>
    public static Element StickyLetter(IReadSignal<int> letter, IReadSignal<float> push) => new BoxEl
    {
        // "nav:sticky" (§3.3 / A1): a stable key on the ROOT so the navigator host can place this overlay as a KEYED
        // sibling of the keyed list — a key on a component's single-child slot is inert (RC1), but this element is
        // returned straight into a `Children` array, where `ReconcileChildren` honors it.
        Key = "nav:sticky",
        Height = LibraryLetters.HeaderExtent, Direction = 0, AlignItems = FlexAlign.End, HitTestVisible = false,
        AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start,
        Padding = new Edges4(Spacing.S, 0f, Spacing.S, Spacing.XS),
        Opacity = Prop.Of(() => letter.Value >= 0 ? 1f : 0f),
        Transform = Prop.Of(() => Affine2D.Translation(0f, push.Value)),
        Children = [LetterPlate(Prop.Of(() => LetterText(letter.Value)))],
    };

    static Element LetterPlate(Prop<string> text) => new BoxEl
    {
        Padding = new Edges4(6f, 1f, 6f, 1f), Corners = CornerRadius4.All(4f), Fill = Tok.FillCardDefault,
        BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
        Children = [new TextEl(text) { Size = 12f, LineHeight = 16f, Weight = 600, CharSpacing = 60f, Color = Tok.TextTertiary }],
    };

    /// <summary>The A–Z strip on the navigator's right edge: 27 rows of 18 × 13. A present letter is full ink and the one
    /// under the viewport top is accent/700 (two stacked runs again — <c>Weight</c> is not bindable); an absent letter sits
    /// at 30 % and its tap NO-OPS, because <paramref name="jump"/> resolves through <c>LibraryLetters.HeaderFlat</c> and
    /// finds nothing. <paramref name="present"/> is the letters' bitmask, <paramref name="current"/> the sticky letter.</summary>
    public static Element JumpStrip(IReadSignal<uint> present, IReadSignal<int> current, Action<int> jump)
    {
        var rows = new Element[LibraryLetters.Count];
        for (int l = 0; l < rows.Length; l++)
        {
            int letter = l;
            Func<bool> here = () => (present.Value & (1u << letter)) != 0;
            Func<bool> now = () => current.Value == letter;
            string text = LetterText(letter);
            rows[l] = new BoxEl
            {
                Width = 18f, Height = 13f, ZStack = true, Corners = CornerRadius4.All(3f),
                // 27 letters must not become 27 tab stops: the strip is a pointer affordance beside a typeahead list.
                Role = AutomationRole.Button, TabStop = false, Cursor = CursorId.Hand, HoverFill = Tok.FillSubtleSecondary,
                Opacity = Prop.Of(() => here() ? 1f : 0.3f), Transition = RailInkFade, OnClick = () => jump(letter),
                Children =
                [
                    StripInk(text, 400, Prop.Of(() => now() ? ColorF.Transparent : Tok.TextTertiary)),
                    StripInk(text, 700, Prop.Of(() => now() ? Tok.AccentTextPrimary : ColorF.Transparent)),
                ],
            };
        }
        return new BoxEl
        {
            // "nav:strip" (§3.3 / A1): same reasoning as StickyLetter's "nav:sticky" — a stable key on the ROOT so
            // the navigator host can place this strip as a KEYED sibling, not lose it to an inert single-child key.
            Key = "nav:strip",
            Direction = 1, Width = 18f, Shrink = 0f, Justify = FlexJustify.Center,
            Padding = new Edges4(0f, Spacing.XS, 0f, Spacing.XS), Children = rows,
        };
    }

    static TextEl StripInk(string text, ushort weight, Prop<ColorF> ink) => Design.Type.MicroMeta(text) with
    {
        Weight = weight, Color = ink, BrushTransitionMs = Design.Motion.Fast,
        AlignSelf = FlexAlign.Center, JustifySelf = FlexAlign.Center,
    };

    // ══ 5. CRUMBS, GRIP, PANES ═══════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The collapsed layout's breadcrumb (W14-W17): on the navigator's layer rung, a 1-DIP divider under it.
    /// <para>KEYED. It is the first child of the collapsed library's root and the nav column is the first child of the
    /// wide one, so with both unkeyed the reconciler paired the two by ordinal and reused ONE node for both — which
    /// handed the nav column this box's static <c>Width</c> in place of its own bound one (bind wiring is mount-only).
    /// The key is what makes the crossing a remount.</para></summary>
    public static Element CrumbBar(IReadOnlyList<string> crumbs, Action<int> onPick) => new BoxEl
    {
        Key = "lib:crumbs",
        Direction = 1, Shrink = 0f, Fill = Tok.FillLayerDefault,
        Children =
        [
            new BoxEl { Padding = new Edges4(Spacing.M, Spacing.S, Spacing.M, Spacing.S), Children = [BreadcrumbBar.Create(crumbs, onPick)] },
            new BoxEl { Height = 1f, Fill = Tok.StrokeDividerDefault },
        ],
    };

    /// <summary>THE column seam (W18): a 16-DIP grip strip with a 1-DIP <c>StrokeCardDefault</c> hairline centred behind
    /// the splitter's hover thumb. The seam brush is a LIVE thunk, so a theme swap re-resolves it without a render.</summary>
    public static Element ColumnGrip(Signal<float> width, float min, float max, Action onCommit) => new BoxEl
    {
        Width = Splitter.StripW, Shrink = 0f, ZStack = true,
        Children =
        [
            new BoxEl { Width = 1f, AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Center, HitTestVisible = false,
                        Fill = Prop.Of(static () => Tok.StrokeCardDefault) },
            new BoxEl { Direction = 1, AlignItems = FlexAlign.Stretch, AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch,
                        Children = [Splitter.Create(width, onCommit, new Splitter.SplitterOptions { Min = min, Max = max })] },
        ],
    };

    /// <summary>The navigator's layer rung and the reading panes' card rung (ch 15 §0 #2) — the seam does the separating.</summary>
    internal static BoxEl NavPanel => new() { Direction = 1, ClipToBounds = true, Fill = Tok.FillLayerDefault };
    internal static BoxEl ReadingPane => new() { Direction = 1, ClipToBounds = true, Fill = Tok.FillCardDefault };

    // ══ 6. THE BOUND ROWS AND CARDS (W1-W5, W19, W22) ═══════════════════════════════════════════════════════════════

    /// <summary>The navigator LIST row's PLATE height (W1): the selected backplate itself — 56, 40 compact — which is the
    /// prototype's row rhythm and the number <see cref="NavRow"/> sets as its explicit height.</summary>
    public const float NavRowPlate = 56f, NavRowCompactPlate = 40f;

    /// <summary>The margin the engine's bound list chrome puts around every plate (<c>SelectorVisualsBound</c>'s
    /// <c>s_backplateMargin {4,2,4,2}</c>) — the vertical half, because the main axis is the only one the list's extents
    /// add up. It is part of the row and NOT of the plate: the 2-DIP gap between plates is the ListView language.</summary>
    public const float NavRowMarginY = 2f;

    /// <summary>The navigator LIST row's OUTER extents — the analytic seed the page hands <c>RepeatLayout.Extents</c>,
    /// which MUST agree with what the row MEASURES: the measured list corrects a row to its measurement, so a seed that
    /// counted the plate and forgot its 2+2 margin was 4 DIP short PER ROW, and the pinned letter, the jump strip, the
    /// scrollbar thumb and every bring-into-view drifted by a whole row every fourteen. The plate + its margin is the
    /// one number both halves read, and <see cref="LibraryLetters"/>' offsets are summed at it.</summary>
    public const float NavRowExtent = NavRowPlate + 2f * NavRowMarginY,
                       NavRowCompactExtent = NavRowCompactPlate + 2f * NavRowMarginY;

    /// <summary>A navigator LIST row (plate 40 compact / 56, outer extent 44 / 60): 40×40 art (r 20 artist / 5 album,
    /// show) + title 14/20/600 + subtitle 12/16 — compact drops both the art and the subtitle. Wears the bound
    /// AccentPill chrome.</summary>
    public static Element NavRow(BoundItemScope<LibraryNavItem> scope, EntityKind kind, bool compact)
    {
        bool circular = kind == EntityKind.Artist;
        Element text = new BoxEl
        {
            Direction = 1, Grow = 1f, Basis = 0f, Gap = 1f, MinWidth = 0f,
            Children = compact
                ? [TitleText(scope, 14f, 20f), new BoxEl()]
                : [TitleText(scope, 14f, 20f), NavSubtitle(scope, kind)],
        };
        Element content = new BoxEl
        {
            Direction = 0, Grow = 1f, AlignItems = FlexAlign.Center, Gap = Spacing.M, Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f),
            Draggable = DragOf(scope),
            Children = compact ? [text] : [ArtBox(scope, 40f, circular ? 20f : 5f), text],
        };
        // The PLATE height, not the row extent: the chrome's own 2+2 margin is what makes the outer extent the seed
        // (`NavRowExtent`). Setting the extent here would measure 60 + 4 and drift the letters by 4 DIP a row.
        return SelectorVisualsBound.AccentPill(scope.Row, content) with
        {
            Cursor = CursorId.Hand, Height = compact ? NavRowCompactPlate : NavRowPlate,
        };
    }

    /// <summary>A navigator GRID card: the fill-width square cover (r Full artist / 6) + title 12/16/600 (compact drops it),
    /// pad 16 artist / 8. Wears the bound Border chrome.</summary>
    public static Element NavCard(BoundItemScope<LibraryNavItem> scope, EntityKind kind, bool compact)
    {
        bool circular = kind == EntityKind.Artist;
        float pad = circular ? 16f : Spacing.S;
        Element art = FillArt(scope, circular ? Radii.Full : 6f);
        Element content = new BoxEl
        {
            Direction = 1, Gap = Spacing.S, ClipToBounds = true, Padding = Edges4.All(pad), Draggable = DragOf(scope),
            Children = compact
                ? [art]
                : [art, TitleText(scope, 12f, 16f) with { AlignSelf = circular ? FlexAlign.Center : FlexAlign.Start }],
        };
        return BorderChrome(scope.Row, content);
    }

    static TextEl TitleText(in BoundItemScope<LibraryNavItem> scope, float size, float line)
        => new(scope.Text(static it => LibraryRows.TitleOf(it.Kind, it.Slot)))
        {
            Size = size, LineHeight = line, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
        };

    static readonly FormatCache<int> s_artistCounts = new();
    static readonly FormatCache<long> s_albumSubtitles = new();

    /// <summary>The navigator row's second line. An ARTIST row says what the library HOLDS of them — "3 albums · 34 songs"
    /// (ch 15 §7 gap 10: it used to say the literal word "Artist") — an ALBUM row "artist · year", and a SHOW row keeps its
    /// publisher. Every one of them is a bind through a HOISTED cache, so a recycle looks a string up and never formats.</summary>
    static TextEl NavSubtitle(in BoundItemScope<LibraryNavItem> scope, EntityKind kind) => kind switch
    {
        EntityKind.Artist => SubtitleText(scope.Text(static it => ArtistCountCode(it.Slot), s_artistCounts, static code => ArtistCountText(code))),
        EntityKind.Album => SubtitleText(scope.Text(static it => AlbumSubtitleKey(it.Slot), s_albumSubtitles, static key => AlbumSubtitleText(key))),
        _ => SubtitleText(scope.Text(static it => LibraryRows.SubtitleOf(it.Kind, it.Slot))),
    };

    static TextEl SubtitleText(Prop<string> text) => new(text)
    {
        Size = 12f, LineHeight = 16f, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
    };

    // The counts packed into one int key (albums << 12 | songs, each clamped to 12 bits): two numbers, so the cache holds
    // one string per distinct pair rather than one per artist.
    static int ArtistCountCode(int slot)
        => (Math.Min(LibraryAlbumCountOf(slot), 4095) << 12) | Math.Min(LibrarySongCountOf(slot), 4095);

    static string ArtistCountText(int code) => ArtistCountLine(code >> 12, code & 4095);

    /// <summary>"3 albums · 34 songs" — and, for an artist whose whole presence in your library is liked TRACKS (§11's
    /// group 2: nothing of theirs saved, one liked feature), "12 songs" on its own. The bare word "Artist" survives for
    /// 0/0 ONLY, which is a followed artist nothing of whose library has answered yet — before the rework it was what an
    /// artist with fifty liked songs and no saved album read as. (<c>nSongs</c> carries its own " · " because it is the
    /// continuation of the albums clause; <c>nSongsOnly</c> is the standalone line and does not.)
    /// <para>THREE branches, no counting. <c>nAlbums</c> / <c>nSongs</c> / <c>nSongsOnly</c> are ICU plurals and pick
    /// their own singular, so the <c>== 1</c> arms that used to reach for <c>oneAlbum</c> / <c>oneSong</c> are gone. They
    /// were also wrong the moment only ONE half was singular: an artist with one album and one song composed the
    /// hand-picked "1 album" with a <c>nSongs</c> that had no singular branch yet and read "1 album · 1 songs". A
    /// plural rule belongs to the message, never to the caller — and a language whose "one" category is not the number
    /// 1 (Russian's 21, Welsh's 2) can only be spelled inside the ICU entry.</para>
    /// <para>Public, not private: this assembly has no <c>InternalsVisibleTo</c> (see <c>Playlist.UI.cs</c>), and the
    /// rule is pinned by a fact rather than by reading this source.</para></summary>
    public static string ArtistCountLine(int albums, int songs)
        => albums <= 0
            ? (songs <= 0 ? Loc.Get(Strings.Search.TypeArtist) : Strings.Library.NSongsOnly(songs))
            : Strings.Library.NAlbums(albums) + (songs > 0 ? Strings.Library.NSongs(songs) : "");

    // "artist · year" keyed by (the billed artist's NAME id, the year): the album slot would have done for the CACHE, but
    // keying on the name's id means a name that lands after the year re-keys instead of serving the stale line forever.
    static long AlbumSubtitleKey(int slot)
    {
        var scope = Entities.Current;
        var billed = scope.Edges.AlbumArtists.Targets(slot);
        int name = billed.Length > 0 && billed[0] > Table.None ? scope.Artists.Name[billed[0]].Value : 0;
        int year = LibraryRows.YearOf(EntityKind.Album, slot);
        return ((long)(uint)name << 16) | (uint)Math.Clamp(year, 0, 0xFFFF);
    }

    static string AlbumSubtitleText(long key)
    {
        string artist = Entities.Strings.Resolve(new StringId((int)(uint)(key >> 16)));
        int year = (int)(key & 0xFFFF);
        if (year <= 0) return artist;
        return artist.Length > 0 ? artist + " · " + FormatCache.Int(year) : FormatCache.Int(year);
    }

    static string? ArtOf(in LibraryNavItem it) => Controls.ArtUrl(LibraryRows.ImageOf(it.Kind, it.Slot));

    static Element ArtBox(in BoundItemScope<LibraryNavItem> scope, float edge, float corners) => new BoxEl
    {
        Width = edge, Height = edge, Shrink = 0f, Corners = CornerRadius4.All(corners), ClipToBounds = true,
        Children =
        [
            new ImageEl
            {
                Source = scope.Image(static it => ArtOf(it)), Width = edge, Height = edge, Fit = ImageFit.Cover,
                Corners = CornerRadius4.All(corners), DecodePx = edge,
                Placeholder = scope.Color(static it => Design.PlaceholderFor(ArtOf(it))),
            },
        ],
    };

    static Element FillArt(in BoundItemScope<LibraryNavItem> scope, float corners) => new ImageEl
    {
        Source = scope.Image(static it => ArtOf(it)), Fit = ImageFit.Cover, AspectRatio = 1f, DecodePx = 256f,
        Corners = CornerRadius4.All(corners),
        Placeholder = scope.Color(static it => Design.PlaceholderFor(ArtOf(it))),
    };

    /// <summary>A library row is a drag SOURCE only, click-primary (×2 drag box); the payload is built at promotion from
    /// the slot's CURRENT item. Tracks resolve lazily through the sidebar's library seam.</summary>
    static DragSource DragOf(in BoundItemScope<LibraryNavItem> scope)
    {
        var item = scope.Item;
        return Drag.Source(() => PayloadOf(item.Peek()), clickPrimary: true);
    }

    internal static DragPayload PayloadOf(in LibraryNavItem it)
    {
        var id = LibraryRows.IdOf(it.Kind, it.Slot);
        string uri = id.Text;
        var kind = Drag.KindOf(it.Kind);
        string prefix = it.Kind switch
        {
            EntityKind.Artist => SidebarPinId.ArtistPrefix,
            EntityKind.Show => SidebarPinId.ShowPrefix,
            _ => SidebarPinId.AlbumPrefix,
        };
        Func<CancellationToken, Task<Track[]>>? resolver = null;
        if (uri.Length > 0 && kind is DragKind.Album or DragKind.Show && Sidebar.LibraryWrites?.ResolveTracks is { } resolve)
            resolver = ct => resolve(uri, ct);
        return new DragPayload(kind, prefix + uri, uri, LibraryRows.TitleOf(it.Kind, it.Slot), new EntityRef(it.Kind, it.Slot),
                               TrackResolver: resolver, ArtUrl: ArtOf(it));
    }

    /// <summary>The ItemContainer BORDER selection chrome, bound (W19 right): a 3-DIP accent ring + a 1-DIP inner stroke
    /// inset 2 revealed by the slot's IsSelected predicate (167 ms brush fade), the subtle hover/press plate above the
    /// content, and the container's tap/keys/focus wiring — shape-stable, so selection never re-renders a card.</summary>
    static BoxEl BorderChrome(in RowScope row, Element content)
    {
        Func<bool> isSel = row.IsSelected;
        var interact = row.OnInteraction;
        return new BoxEl
        {
            ZStack = true, Corners = Radii.ControlAll, Fill = Tok.FillSubtleTransparent,
            Focusable = false, FocusVisualMargin = Edges4.All(0f), Role = AutomationRole.Button, Cursor = CursorId.Hand,
            OnPointerReleased = args => interact(args.ClickCount >= 2 ? ItemContainerTrigger.DoubleTap : ItemContainerTrigger.Tap, args.Mods),
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

    // ══ 7. THE SEARCH-ROW SHAPES (W11-W13, W23, W26) ════════════════════════════════════════════════════════════════

    /// <summary>Retained hits stay put; only a real insert/remove/reorder animates (90 ms in, 70 ms out).</summary>
    internal static readonly LayoutTransition SearchRowChange = new(
        TransitionChannels.Position | TransitionChannels.Opacity,
        TransitionDynamics.Tween(90f, Easing.SmoothOut),
        Enter: new EnterExit(Dy: 3f, Opacity: 0f, Active: true),
        Exit: new EnterExit(Opacity: 0f, Active: true),
        ExitDynamics: TransitionDynamics.Tween(70f, Easing.SmoothOut));

    /// <summary>An artist / album hit (h 56): cover 44 (r Full / 4) · optional "why" eyebrow · highlighted title · subtitle.
    /// The browse-list selection language (subtle fills), never an accent tint.</summary>
    public static Element SearchRow(string key, string? cover, bool circular, string title, int matchStart, int matchLen,
                                    string subtitle, string? eyebrow, bool selected, Action onClick)
    {
        float r = circular ? Radii.Full : Radii.Control;
        var text = new List<Element>(3);
        if (!string.IsNullOrEmpty(eyebrow))
            text.Add(new TextEl(eyebrow) { Size = 12f, LineHeight = 16f, Weight = 600, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis });
        text.Add(Controls.SearchHighlight(title, matchStart, matchLen, 14f, 600, Tok.TextPrimary));
        if (subtitle.Length > 0)
            text.Add(new TextEl(subtitle) { Size = 12f, LineHeight = 16f, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis });
        return new BoxEl
        {
            Key = key, Animate = SearchRowChange,
            Direction = 0, Height = 56f, AlignItems = FlexAlign.Center, Gap = Spacing.M, ClipToBounds = true,
            Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f), Corners = Radii.ControlAll,
            Fill = selected ? Tok.FillSubtleSecondary : ColorF.Transparent,
            HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
            Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = onClick,
            Children =
            [
                new BoxEl { Width = 44f, Height = 44f, Shrink = 0f, Corners = CornerRadius4.All(r), ClipToBounds = true,
                    SkeletonOverride = CoverSkeleton(44f, r), Children = [Controls.Artwork(cover, 44f, 44f, r)] },
                new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, Gap = 1f, ClipToBounds = true, MinWidth = 0f, Children = text.ToArray() },
            ],
        };
    }

    /// <summary>A track hit (h 44): 36 cover (dropped when track artwork is hidden — part of the key) + a 13/600
    /// highlighted title. A click plays in place.</summary>
    public static Element TrackHitRow(string key, string? cover, string title, int matchStart, int matchLen, bool showArt, Action onClick)
    {
        Element titleBox = new BoxEl
        {
            Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, ClipToBounds = true,
            Children = [Controls.SearchHighlight(title, matchStart, matchLen, 13f, 600, Tok.TextPrimary)],
        };
        return new BoxEl
        {
            Key = key + (showArt ? ":art=True" : ":art=False"), Animate = SearchRowChange,
            Direction = 0, Height = 44f, AlignItems = FlexAlign.Center, Gap = Spacing.M, ClipToBounds = true,
            Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f), Corners = Radii.ControlAll,
            Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = onClick,
            Children = showArt
                ? [new BoxEl { Width = 36f, Height = 36f, Shrink = 0f, Corners = Radii.ControlAll, ClipToBounds = true,
                       SkeletonOverride = CoverSkeleton(36f, Radii.Control), Children = [Controls.Artwork(cover, 36f, 36f, Radii.Control)] },
                   titleBox]
                : [titleBox],
        }.Interactive(Interaction.Subtle);
    }

    /// <summary>The cover slot's honest skeleton: a same-sized tile in the shimmer's bar colour.</summary>
    static Element CoverSkeleton(float size, float corners) => new BoxEl
    {
        Width = size, Height = size, Shrink = 0f, Corners = CornerRadius4.All(corners),
        Fill = SkeletonStyle.Default.BarColor, IsEnabled = false, HitTestVisible = false,
    };

    /// <summary>A facet header: eyebrow + count (hidden when &lt; 0; faded to 40 % while a newer answer is coming).</summary>
    public static Element FacetHeader(string label, int count, bool refining) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.XS, Shrink = 0f,
        Padding = new Edges4(Spacing.M, Spacing.M, Spacing.M, Spacing.S),
        Children =
        [
            Design.Type.Eyebrow(label) with { Color = Tok.TextTertiary },
            count >= 0
                ? new TextEl(FormatCache.Int(count))
                  {
                      Size = 12f, LineHeight = 16f, Weight = 600, BrushTransitionMs = Design.Motion.Faster,
                      Color = refining ? Tok.TextTertiary with { A = Tok.TextTertiary.A * 0.4f } : Tok.TextTertiary,
                  }
                : new BoxEl(),
        ],
    };

    /// <summary>The left search column's zero-hit copy (14/20 tertiary, left-aligned) — NOT the browse EmptyState.</summary>
    public static Element SearchMessage(string text) => new BoxEl
    {
        Padding = new Edges4(Spacing.M, Spacing.XL, Spacing.M, Spacing.XL),
        Children = [new TextEl(text) { Size = 14f, LineHeight = 20f, Color = Tok.TextTertiary }],
    };

    // ══ 8. THE LIBRARY SEAM (contract §2.6 → Controls.Library) ═══════════════════════════════════════════════════════

    /// <summary>The save/follow affordances' one seam. <c>IsSaved</c> SUBSCRIBES (the scope epoch + the relation's
    /// <c>Changed</c>) so a heart re-skins the frame the optimistic edge lands; a pending remove reads unsaved. Tracks,
    /// albums, artists and shows go through <see cref="Add"/>/<see cref="Remove"/>; a playlist follows through the
    /// rootlist (<c>Spotify.Library.FollowPlaylist</c>); the liked collection reads saved and is not toggled.</summary>
    public static Controls.LibrarySeam LibrarySeam { get; } = new(IsSavedUri, ToggleSavedUri);

    static bool IsSavedUri(string uri)
    {
        _ = Entities.ScopeEpoch.Value;
        var scope = Entities.Current;
        if (scope is null || string.IsNullOrEmpty(uri) || scope.MeSlot <= Table.None) return false;
        if (EntityUri.IsLikedCollection(uri)) return true;
        var id = EntityUri.Parse(uri).Id;
        var table = RelationOf(id.Kind, scope);
        if (id.Kind == EntityKind.Playlist)
        {
            _ = scope.Edges.Rootlist.Changed.Value;
            return scope.Playlists.TryGetSlot(id, out int p) && scope.Edges.Rootlist.Contains(scope.MeSlot, p);
        }
        if (table is null || Entities.TableFor(id.Kind) is not { } rows) return false;
        _ = table.Changed.Value;
        return rows.TryGetSlot(id, out int slot) && table.Contains(scope.MeSlot, slot)
               && table.PendingOf(scope.MeSlot, slot) != EdgePending.Remove;
    }

    static void ToggleSavedUri(string uri, string? name)
    {
        var scope = Entities.Current;
        if (scope is null || string.IsNullOrEmpty(uri) || EntityUri.IsLikedCollection(uri)) return;
        var me = Me;
        if (!me.IsValid) return;
        var id = EntityUri.Parse(uri).Id;
        if (id.Kind == EntityKind.Playlist)
        {
            bool following = scope.Playlists.TryGetSlot(id, out int p) && scope.Edges.Rootlist.Contains(me.Slot, p);
            Spotify.Library.FollowPlaylist(uri, !following);
            return;
        }
        if (Entities.TableFor(id.Kind) is not { } rows) return;
        LibraryEdgeKind kind = id.Kind switch
        {
            EntityKind.Album => LibraryEdgeKind.SavedAlbums,
            EntityKind.Artist => LibraryEdgeKind.FollowedArtists,
            EntityKind.Show => LibraryEdgeKind.SavedShows,
            EntityKind.Track => LibraryEdgeKind.Liked,
            _ => LibraryEdgeKind.Pins,
        };
        if (kind == LibraryEdgeKind.Pins) return;   // an episode / user / concert has no library relation
        int slot = rows.Slot(id);
        if (me.Has(kind, slot) && me.PendingOf(kind, slot) != EdgePending.Remove) me.Remove(kind, slot, id);
        else me.Add(kind, slot, id);
        Entities.Publish();
    }

    static EdgeTable<LibraryEdge>? RelationOf(EntityKind kind, Scope scope) => kind switch
    {
        EntityKind.Track => scope.Edges.Liked,
        EntityKind.Album => scope.Edges.SavedAlbums,
        EntityKind.Artist => scope.Edges.FollowedArtists,
        EntityKind.Show => scope.Edges.SavedShows,
        _ => null,
    };
}
