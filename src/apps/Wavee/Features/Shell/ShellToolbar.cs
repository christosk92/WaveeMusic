using System;
using System.Collections.Generic;
using System.Threading;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// The merged chrome row's shared button STYLE. The 48-DIP navigation toolbar this file used to build is gone — Wavee's
// chrome is now ONE 48-DIP TitleBar (see MergedChromeRow), and the customizable shortcut band moved to the sidebar.
// What survives here is the row's reusable furniture: this style, the back/forward history button, the "..." overflow
// menu, and the omnibar (field + suggestions popup) the merged row's centre island hosts.
static class ShellToolbar
{
    /// <summary>The footprint for anything living INSIDE a merged-row island — 40x44, the bar's own nav metric (the
    /// same one TitleBar gives its built-in back/pane buttons). Taller on purpose: an island's whole 48-DIP rect is
    /// reported as Client and can never be window-drag, so a 32-DIP button would leave an 8-DIP strip above and below
    /// that is neither draggable nor clickable. 44 in a 48 row leaves 2.
    /// <para>(The old 36x32 <c>NavStyle</c> is gone. Every chrome button now lives in an island — the bell already used
    /// this style, and the two nav buttons only ever reached the 36x32 default through an optional ctor parameter that
    /// nothing passed, so it was a footprint no pixel on screen actually had.)</para></summary>
    internal static IconButton.Style BarNavStyle => IconButton.DefaultStyle with { Size = 40f, Height = 44f };

    /// <summary>The breathing room between two island affordances: 2 DIP on each horizontal side, which is exactly what
    /// the bar's own built-in pane toggle carries (TitleBar gives it <c>Margin 2</c>). Contiguous 40x44 plates with a
    /// 0-DIP gap read as one rough slab of chrome, and the hover fills of two neighbours touch. The cost is honest and
    /// bounded: 2 DIP per side of an island button is window-drag band the island can never give back (see
    /// <see cref="MergedChromeRow"/>'s island contract), and 2 is the smallest step that separates the plates.</summary>
    internal static readonly Edges4 BarNavMargin = new(2f, 0f, 2f, 0f);
}

// A toolbar nav button (Back or Forward) that fires its primary action on click and opens a history flyout on
// right-click or touch-hold (OnContextRequested). Shows the most recent HistoryMenuMax routes from the supplied
// list (most recent at top), plus a "View all history" item when the list exceeds the cap. Each item navigates
// via Go so back/forward state is rebuilt naturally (Go clears forward, then the user can go back to any item).
sealed class NavHistoryButton : Component
{
    readonly string _icon;
    readonly Action _primary;
    readonly Signal<bool> _canDo;
    readonly List<Route> _history;   // live reference — read at flyout-open time, not mount time
    readonly Action<string, string?> _go;
    readonly IconButton.Style _style;   // REQUIRED: the host island owns the footprint (there is no standalone form)

    const int HistoryMenuMax = 8;

    public NavHistoryButton(string icon, Action primary, Signal<bool> canDo,
                            List<Route> history, Action<string, string?> go, IconButton.Style style)
    { _icon = icon; _primary = primary; _canDo = canDo; _history = history; _go = go; _style = style; }

    public override Element Render()
    {
        bool canDo = _canDo.Value;   // subscribe → re-render when enabled state changes
        var anchor = UseRef<NodeHandle>(default);
        var handle = UseRef<OverlayHandle?>(null);
        var svc = UseContext(Overlay.Service);

        void OpenFlyout(ContextRequestEventArgs _)
        {
            if (handle.Value is { IsOpen: true } h) { h.Close(); return; }
            if (_history.Count == 0) return;

            int count = Math.Min(_history.Count, HistoryMenuMax);
            bool hasMore = _history.Count > HistoryMenuMax;
            var items = new MenuFlyoutItem[count + (hasMore ? 2 : 0)];
            int idx = 0;
            for (int i = _history.Count - 1; i >= _history.Count - count; i--)
            {
                var r = _history[i];
                var (title, glyph) = ShellNav.Dest(r);
                items[idx++] = new MenuFlyoutItem(title, glyph, Invoke: () => _go(r.Name, r.Arg));
            }
            if (hasMore)
            {
                items[idx++] = MenuFlyoutItem.Separator;
                items[idx]   = new MenuFlyoutItem(Loc.Get(Strings.Nav.ViewAllHistory), Icons.Clock, Invoke: () => _go("history", null));
            }

            handle.Value = svc.Open(
                () => anchor.Value,
                () => MenuFlyout.Create(items, () => handle.Value?.Close()),
                FlyoutPlacement.BottomEdgeAlignedLeft,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = false });
            handle.Value.ClosedAction = () => handle.Value = null;
        }

        return IconButton.Create(_icon, _primary, _style, isEnabled: canDo)
            with { Margin = ShellToolbar.BarNavMargin, OnRealized = h => anchor.Value = h, OnContextRequested = OpenFlyout };
    }
}

// A "⋯" toolbar icon that opens a plain MenuFlyout below it via the overlay service — the same path DropDownButton uses,
// so it gets the engine's clean MenuPopupThemeTransition clip-reveal (NOT CommandBarFlyout's extra overflow-expand clip).
sealed class OverflowMenu : Component
{
    readonly MergedChromeRow _owner;
    readonly IReadSignal<MergedChromeLayout> _layout;
    public OverflowMenu(MergedChromeRow owner, IReadSignal<MergedChromeLayout> layout)
    { _owner = owner; _layout = layout; }

    public override Element Render()
    {
        var anchor = UseRef<NodeHandle>(default);
        var handle = UseRef<OverlayHandle?>(null);
        var svc = UseContext(Overlay.Service);

        // Notifications used to be re-anchored HERE when the bell collapsed. They are not any more: the bell merged
        // into the profile chip's avatar badge and its panel opens from the profile flyout (ProfileMenu re-uses this
        // file's re-anchoring mechanism verbatim, against the CHIP). The "⋯" is back to being pure spillover.
        void Toggle()
        {
            if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
            handle.Value = svc.Open(
                () => anchor.Value,
                () => MenuFlyout.Create(_owner.OverflowItems(_layout.Peek()), () => handle.Value?.Close()),
                FlyoutPlacement.BottomEdgeAlignedRight,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = false });
            handle.Value.ClosedAction = () => handle.Value = null;
        }

        return IconButton.Create(Icons.More, Toggle, ShellToolbar.BarNavStyle)
            with { Margin = ShellToolbar.BarNavMargin, OnRealized = h => anchor.Value = h };
    }
}

/// <summary>The chrome row's ONE suggestion store, shared by the field-mode omnibar and the icon-mode flyout's. The
/// request lifecycle lives in the engine-free <see cref="OmnibarSuggestQuery"/>; this wrapper republishes its changes
/// to the render graph and carries the keyboard cursor. Owned by <see cref="MergedChromeRow"/> (constructed once per
/// shell), so a search-mode switch — which re-mounts <see cref="FluentRichOmnibar"/> — keeps the rows, the pending
/// generation and the cursor instead of restarting from nothing.</summary>
sealed class OmnibarSuggestStore
{
    public readonly OmnibarSuggestQuery Query = new();
    /// <summary>Bumped after every <see cref="Query"/> change: a component reads it to subscribe to the lifecycle.</summary>
    public readonly Signal<int> Version = new(0);
    /// <summary>The keyboard cursor over the visible rows (-1 = none). Reset by the box on every user edit.</summary>
    public readonly Signal<int> Highlight = new(-1);

    public OmnibarSuggestStore() => Query.Changed += () => Version.Value = Version.Peek() + 1;
}

// Wavee's rich search content hosted by the reusable FluentGpu AutoSuggestBox. The field remains a real control (focus,
// editing, accessibility and popup lifetime); this component supplies only artwork-aware suggestion rows.
sealed class FluentRichOmnibar : Component
{
    readonly Signal<string> _text;
    readonly Action<string, string?> _go;
    readonly OmnibarSuggestStore _store;
    // The merged row's centre island passes a parts map so it can capture AutoSuggestBox.PartRoot and put the caret in
    // the field on the click-expand edge. Null = the field owns its own root (every other host).
    readonly TemplateParts? _parts;
    readonly float _maxWidth;
    readonly AutoSuggestBoxSuggestionPresentation _suggestionPresentation;
    readonly bool _allowNarrowSuggestions;

    public FluentRichOmnibar(Signal<string> text, Action<string, string?> go, OmnibarSuggestStore store,
        TemplateParts? parts = null, float maxWidth = 480f,
        AutoSuggestBoxSuggestionPresentation suggestionPresentation = AutoSuggestBoxSuggestionPresentation.Popup,
        bool allowNarrowSuggestions = false)
    {
        _text = text; _go = go; _store = store; _parts = parts; _maxWidth = maxWidth;
        _suggestionPresentation = suggestionPresentation;
        _allowNarrowSuggestions = allowNarrowSuggestions;
    }

    public override Element Render()
    {
        var svc = UseContext(Services.Slot);
        var post = UsePost();
        var goOrigin = UseContext(HistoryStore.GoWithOrigin);
        var store = _store;
        var query = store.Query;
        var highlight = store.Highlight;
        // The request in flight for the current generation. A superseding keystroke, a clear and an unmount cancel it;
        // the store drops whatever a cancelled request would have said, so cancelling is only ever a saving.
        var inflight = UseRef<CancellationTokenSource?>(null);
        void CancelInflight() { inflight.Value?.Cancel(); inflight.Value = null; }

        _ = store.Version.Value;   // subscribe: every lifecycle step re-renders the field (ghost) and the popup
        string typed = _text.Value.Trim();

        // THE KEYSTROKE EDGE, undebounced. The engine box opens its popup synchronously on this same edge; entering
        // Pending here — not 150 ms later when the fetch starts — is what keeps "No results found" off the screen
        // between a keystroke and its request. The generation it hands out is what the debounced fetch below answers.
        UseEffect(() =>
        {
            int before = query.Generation;
            if (query.Begin(typed) != before) CancelInflight();
        }, typed);

        // The fetch edge: the GENERATION settles (not the text — a retype to the same text inside the window is a new
        // generation and must be answered), 150 ms of quiet after the last change, exactly the engine's TextChanged
        // cadence. A retry re-arms the generation and flows through here too; a re-mount finds a Pending generation
        // whose request died with the old instance and re-issues it; a settled Results/Empty generation is left alone.
        int settled = UseDebouncedValue(() => { _ = store.Version.Value; return query.Generation; },
            AutoSuggestBox.TextChangedDebounceMs).Value;
        UseEffect(() =>
        {
            if (settled != query.Generation || !query.IsPending) return;
            if (svc is null) { query.Complete(settled, SearchSuggestions.Empty); return; }   // no session: nothing to ask
            Fetch(svc, post, settled, query.Query);
        }, DepKey.From(settled));

        // Unmount: the request dies with the component; the store keeps Pending so the next mount re-issues it.
        UseEffect(() => (Action)CancelInflight, DepKey.Empty);

        void Fetch(Services services, Action<Action> postUi, int gen, string q)
        {
            CancelInflight();
            var cts = new CancellationTokenSource();
            inflight.Value = cts;
            _ = Run();

            async System.Threading.Tasks.Task Run()
            {
                try
                {
                    var s = await services.Library.SuggestRichAsync(q, cts.Token).ConfigureAwait(false);
                    postUi(() => query.Complete(gen, s));
                }
                catch (OperationCanceledException) { }   // superseded or torn down: the store has already moved on
                catch (Exception ex) { postUi(() => query.Fail(gen, ex)); }
            }
        }

        var completion = UseComputed(() =>
        {
            if (highlight.Value >= 0) return "";
            _ = store.Version.Value;
            return SearchSuggestions.GhostFor(_text.Value.Trim(), query.Suggestions.Queries) ?? "";
        });

        void Submit(string q)
        {
            var trimmed = q.Trim();
            _go("search", trimmed.Length == 0 ? null : trimmed);
        }

        NavOrigin? GenreOrigin()
        {
            string q = _text.Peek().Trim();
            return q.Length == 0 ? null : new NavOrigin(q, "search", q);
        }

        bool InvokeSelection(int selection)
        {
            var suggestions = query.Suggestions;
            int queryCount = Math.Min(6, suggestions.Queries.Count);
            int itemCount = Math.Min(10, suggestions.Items.Count);
            if (selection < 0 || selection >= queryCount + itemCount) return false;

            if (selection < queryCount)
            {
                string chosen = suggestions.Queries[selection];
                _text.Value = chosen;
                _go("search", chosen);
                return true;
            }

            var item = suggestions.Items[selection - queryCount];
            switch (item.Kind)
            {
                case SearchSuggestionKind.Track:
                    if (svc is not null) _ = svc.Player.PlayTrackAsync(item.Uri);
                    break;
                case SearchSuggestionKind.Artist: _go("artist:" + item.Uri, item.Title); break;
                case SearchSuggestionKind.Album: _go("album:" + item.Uri, item.Title); break;
                case SearchSuggestionKind.Playlist: _go("pl:" + item.Uri, item.Title); break;
                case SearchSuggestionKind.Podcast:
                case SearchSuggestionKind.Audiobook:
                    _go("show:" + item.Uri, item.Title);
                    break;
                case SearchSuggestionKind.Episode:
                    if (svc is not null) _ = svc.Player.PlayAsync(item.Uri, 0);
                    break;
                case SearchSuggestionKind.Genre:
                    SearchRoutes.OpenGenre(item.Uri, item.Title, _go, GenreOrigin(), goOrigin);
                    break;
            }
            return true;
        }

        void MoveSelection(int delta)
        {
            var suggestions = query.Suggestions;
            int count = Math.Min(6, suggestions.Queries.Count) + Math.Min(10, suggestions.Items.Count);
            if (count == 0) { highlight.Value = -1; return; }
            int current = highlight.Peek();
            highlight.Value = delta > 0
                ? (current + 1 >= count ? -1 : current + 1)
                : (current < 0 ? count - 1 : current - 1);
        }

        var presenter = new AutoSuggestBoxPresenter(
            Build: context => Embed.Comp(() => new OmnibarSuggestionsPopup(
                _text, store, context.Width,
                choose: selection => { if (InvokeSelection(selection)) context.Close(); },
                close: context.Close,
                // Re-arms the failed generation; the settled-generation effect above issues the request.
                retry: () => query.Retry(),
                allowNarrow: _allowNarrowSuggestions)),
            MoveSelection: MoveSelection,
            SubmitSelection: () => InvokeSelection(highlight.Peek()),
            ResetSelection: () => highlight.Value = -1);

        // Stock AutoSuggestBox metrics: a 32-DIP field at ControlCornerRadius (cornerRadius 0 resolves to Radii.Control
        // inside the box) with the control-default chrome — no pill, no elevation ring. 480 is the stock search cap.
        return AutoSuggestBox.Create(Array.Empty<string>(), Loc.Get(Strings.Shell.SearchPlaceholder),
            grow: 1f, maxFillWidth: _maxWidth, text: _text, onQuerySubmitted: Submit,
            minHeight: 32f, cornerRadius: 0f, presenter: presenter, parts: _parts,
            chrome: AutoSuggestBoxChrome.Standard, suggestionPresentation: _suggestionPresentation,
            completion: completion);
    }
}

// The suggestions popup body, rendered BY the store's state. The one sentence it can say, "No results found", is
// reserved for a confirmed empty answer; a pending generation is a progress row, a failed one is a retry offer, and the
// rows of the previous answer stay on screen under the progress bar until the new answer replaces them.
sealed class OmnibarSuggestionsPopup : Component
{
    readonly Signal<string> _text;
    readonly OmnibarSuggestStore _store;
    readonly IReadSignal<float> _width;
    readonly Action<int> _choose;   // row index over (queries, then items) — the omnibar owns what a choice does
    readonly Action? _close;
    readonly Action _retry;
    readonly bool _allowNarrow;

    public OmnibarSuggestionsPopup(Signal<string> text, OmnibarSuggestStore store, IReadSignal<float> width,
        Action<int> choose, Action? close, Action retry, bool allowNarrow)
    {
        _text = text; _store = store; _width = width;
        _choose = choose; _close = close; _retry = retry; _allowNarrow = allowNarrow;
    }

    public override Element Render()
    {
        string q = _text.Value.Trim();
        _ = _store.Version.Value;   // subscribe to the lifecycle
        var suggest = _store.Query;
        var s = suggest.Suggestions;
        var state = suggest.State;
        bool pending = state == SuggestState.Pending;
        int highlighted = _store.Highlight.Value;
        // FLOOR, not just fallback: the popup width tracks the anchor field, and the merged chrome's icon-mode search
        // can be crushed to ChromeSearchIconW when the centre column has no room — anchoring a 40-DIP dropdown that
        // renders every row as a vertical sliver. A popup is an overlay; it may be wider than its anchor. 400 keeps a
        // cover + title + trailing actions legible (the overlay layer clamps to the window edge like any flyout).
        float measuredWidth = _width.Value > 0f ? _width.Value : 720f;
        float width = _allowNarrow ? measuredWidth : MathF.Max(measuredWidth, 400f);
        // Row actions (Play / Like / context menu) resolve from ambient context; choosing a row goes through _choose.
        var svc = UseContext(Services.Slot);
        var acts = UseContext(ActionServices.Slot);
        var overlay = UseContext(Overlay.Service);
        var lib = UseContext(LibraryBridge.Slot);

        // No client-side re-filter: the server's fuzzy matching (apostrophes, word order) is authoritative;
        // a literal Contains() check would drop most of its hits. Staleness is handled at publish time.
        var rows = new List<Element>();
        int selectionIndex = 0;
        int queryCount = 0;
        foreach (var query in s.Queries)
        {
            rows.Add(QueryRow(query, q, selectionIndex, highlighted == selectionIndex));
            selectionIndex++;
            if (++queryCount >= 6) break;
        }

        int richCount = 0;
        foreach (var item in s.Items)
        {
            if (richCount == 0 && rows.Count > 0) rows.Add(Divider());
            rows.Add(RichRow(item, selectionIndex, highlighted == selectionIndex, svc, acts, overlay, lib));
            selectionIndex++;
            if (++richCount >= 10) break;
        }

        Element body;
        if (rows.Count == 0)
        {
            body = state switch
            {
                SuggestState.Empty => Notice(width, Loc.Get(Strings.Search.NoResults)),
                SuggestState.Failed => Notice(width, Loc.Get(Strings.Search.SuggestFailed),
                    Button.Subtle(Loc.Get(Strings.Common.Retry), _retry)),
                // Pending (the progress bar above is the whole answer) and Idle (the box is closing): no sentence.
                _ => new BoxEl { Width = width, MinWidth = width, MinHeight = AutoSuggestBox.ItemMinHeight },
            };
        }
        else
        {
            body = new ScrollEl
            {
                Width = width,
                MinWidth = width,
                MaxHeight = 560f,
                ContentSized = true,
                Content = new BoxEl
                {
                    Direction = 1,
                    Width = width,
                    MinWidth = width,
                    Margin = new Edges4(-1, 0, -1, 0),
                    Children = rows.ToArray(),
                },
            };
        }

        // PopupChrome.Static supplies the acrylic plate + border + rounded corners + shadow + clip, so return just the
        // content with the 2px vertical breathing room the rows had inside the old plate.
        return new BoxEl
        {
            Direction = 1, Width = width, MinWidth = width, Padding = new Edges4(0, 2, 0, 2),
            Children = pending ? [ProgressBar.Indeterminate(width), body] : [body],
        };
    }

    // One sentence in the row slot, optionally with a trailing affordance (the failure's Retry).
    static Element Notice(float width, string text, Element? trailing = null) => new BoxEl
    {
        Width = width, MinWidth = width, MinHeight = AutoSuggestBox.ItemMinHeight,
        Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M,
        Padding = new Edges4(24, 0, trailing is null ? 24 : 12, 0),
        Children = trailing is null
            ? [new TextEl(text) { Size = 14f, Color = Tok.TextPrimary, Grow = 1f }]
            : [new TextEl(text) { Size = 14f, Color = Tok.TextPrimary, Grow = 1f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis }, trailing],
    };

    Element QueryRow(string query, string typed, int selectionIndex, bool selected) => new BoxEl
    {
        MinHeight = AutoSuggestBox.ItemMinHeight,
        AlignItems = FlexAlign.Center,
        Padding = new Edges4(12, 0, 8, 0),
        Margin = new Edges4(4, 2, 4, 2),
        Corners = Radii.ControlAll,
        Role = AutomationRole.MenuItem,
        Fill = selected ? Tok.FillSubtleSecondary : ColorF.Transparent,
        HoverFill = Tok.FillSubtleSecondary,
        PressedFill = Tok.FillSubtleTertiary,
        OnClick = () => _choose(selectionIndex),
        Children = QueryContent(query, typed),
    };

    Element RichRow(SearchSuggestionItem item, int selectionIndex, bool selected,
                    Services? svc, ActionServices? acts, IOverlayService? overlay, LibraryBridge? lib)
    {
        bool circular = item.Kind is SearchSuggestionKind.Artist or SearchSuggestionKind.User;
        float radius = circular ? 22f : 5f;
        bool saved = lib?.IsSaved(item.Uri) ?? false;
        bool canPlay = item.Kind is not (SearchSuggestionKind.User or SearchSuggestionKind.Genre);
        Action play = () => PlayItem(item, svc);
        Action open = () => _choose(selectionIndex);
        var trailingKids = new List<Element>(4);
        if (canPlay) trailingKids.Add(IconButton(Icons.Play, play));
        if (item.Kind == SearchSuggestionKind.Track)
            trailingKids.Add(TrackRow.Heart(saved, () => lib?.ToggleSaved(item.Uri, item.Title)));
        if (acts is not null && overlay is not null && canPlay)
            trailingKids.Add(MoreButton(true));
        trailingKids.Add(TypePill(TypeLabel(item.Kind)));
        var trailing = new BoxEl
        {
            Direction = 0, Shrink = 0f, AlignItems = FlexAlign.Center, Gap = 2f,
            Children = trailingKids.ToArray(),
        };

        var row = new BoxEl
        {
            Direction = 0,
            Height = 58f,
            AlignItems = FlexAlign.Center,
            Gap = Spacing.M,
            Padding = new Edges4(12, 0, 10, 0),
            Margin = new Edges4(4, 2, 4, 2),
            Corners = Radii.ControlAll,
            Role = AutomationRole.MenuItem,
            Fill = selected ? Tok.FillSubtleSecondary : ColorF.Transparent,
            HoverFill = Tok.FillSubtleSecondary,
            PressedFill = Tok.FillSubtleTertiary,
            OnClick = open,
            Children =
            [
                new BoxEl
                {
                    Width = 44f, Height = 44f, Shrink = 0f,
                    Corners = CornerRadius4.All(radius), ClipToBounds = true,
                    Children = [Surfaces.Artwork(item.Image, item.Uri.GetHashCode() & 0x7fffffff, 44f, 44f, radius)],
                },
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Basis = 0f, Gap = 1f,
                    Children =
                    [
                        new TextEl(item.Title) { Size = 14f, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                        new TextEl(item.Subtitle ?? TypeLabel(item.Kind)) { Size = 12f, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                    ],
                },
                trailing,
            ],
        };
        return acts is not null && overlay is not null
            ? row.WithContextMenu(overlay, () => Menus.Card(acts, item.Uri, item.Title))
            : row;
    }

    void PlayItem(SearchSuggestionItem item, Services? svc)
    {
        if (svc is null) return;
        if (item.Kind is SearchSuggestionKind.User or SearchSuggestionKind.Genre) return;
        if (item.Kind == SearchSuggestionKind.Track) _ = svc.Player.PlayTrackAsync(item.Uri);
        else _ = svc.Player.PlayAsync(item.Uri, 0);
        _close?.Invoke();
    }

    static Element IconButton(string glyph, Action onClick) => new BoxEl
    {
        Width = 28f, Height = 28f, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Corners = CornerRadius4.All(14f),
        HoverScale = WaveeMotion.ScaleEmphatic.Hover, PressScale = WaveeMotion.ScaleEmphatic.Press,
        Cursor = CursorId.Hand, OnClick = onClick, Role = AutomationRole.Button,
        Children = [Icon(glyph, 14f, Tok.TextSecondary)],
    }.Interactive(Interaction.Subtle);

    // Always-visible "…" — same ClickRequestsContext contract as TrackRow.MoreButton, without the hover-only fade
    // (omnibar rows are transient; the affordance needs to read at rest).
    static Element MoreButton(bool enabled) => new BoxEl
    {
        Width = 28f, Height = 28f, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Corners = CornerRadius4.All(14f),
        HoverScale = WaveeMotion.ScaleEmphatic.Hover, PressScale = WaveeMotion.ScaleEmphatic.Press,
        Cursor = enabled ? CursorId.Hand : (CursorId?)null,
        ClickRequestsContext = enabled,
        Role = AutomationRole.Button,
        Children = [Icon(Icons.More, 16f, Tok.TextSecondary)],
    }.Interactive(Interaction.Subtle);

    static Element Divider() => new BoxEl
    {
        Height = 1f,
        Margin = new Edges4(16f, 4f, 16f, 4f),
        Fill = Tok.StrokeDividerDefault,
    };

    static Element TypePill(string type) => new BoxEl
    {
        Shrink = 0f,
        Padding = new Edges4(9f, 2f, 9f, 2f),
        Corners = CornerRadius4.All(10f),
        Fill = Tok.FillSubtleSecondary,
        Children = [WaveeType.Eyebrow(type) with { Color = Tok.TextTertiary }],
    };

    static string TypeLabel(SearchSuggestionKind kind) => kind switch
    {
        SearchSuggestionKind.Track => Loc.Get(Strings.Search.TypeSong),
        SearchSuggestionKind.Artist => Loc.Get(Strings.Search.TypeArtist),
        SearchSuggestionKind.Album => Loc.Get(Strings.Search.TypeAlbum),
        SearchSuggestionKind.Playlist => Loc.Get(Strings.Search.TypePlaylist),
        SearchSuggestionKind.Genre => Loc.Get(Strings.Search.TypeGenre),
        SearchSuggestionKind.Episode => Loc.Get(Strings.Search.TypeEpisode),
        SearchSuggestionKind.Podcast => Loc.Get(Strings.Search.TypePodcast),
        SearchSuggestionKind.Audiobook => Loc.Get(Strings.Search.TypeAudiobook),
        SearchSuggestionKind.User => Loc.Get(Strings.Search.TypeUser),
        _ => "",
    };

    static Element[] QueryContent(string text, string query)
    {
        var kids = new List<Element>(4)
        {
            new TextEl(Icons.Search) { Size = 16f, FontFamily = Theme.IconFont, Color = Tok.TextSecondary, Margin = new Edges4(0, 0, 12, 0) },
        };

        int mi = query.Length > 0 ? text.IndexOf(query, StringComparison.OrdinalIgnoreCase) : -1;
        if (mi < 0)
        {
            kids.Add(new TextEl(text) { Size = 14f, Color = Tok.TextPrimary, Grow = 1f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis });
            return kids.ToArray();
        }

        if (mi > 0) kids.Add(Seg(text.Substring(0, mi), false, false));
        kids.Add(Seg(text.Substring(mi, query.Length), true, false));
        int after = mi + query.Length;
        kids.Add(after < text.Length ? Seg(text.Substring(after), false, true) : new BoxEl { Grow = 1f });
        return kids.ToArray();

        static Element Seg(string s, bool match, bool grow) => new TextEl(s)
        {
            Size = 14f,
            Weight = (ushort)(match ? 700 : 400),
            Color = match ? Tok.TextPrimary : Tok.TextSecondary,
            Grow = grow ? 1f : 0f,
            MaxLines = 1,
            Trim = TextTrim.CharacterEllipsis,
        };
    }

}
