using System;
using System.Collections.Generic;
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

// Wavee's rich search content hosted by the reusable FluentGpu AutoSuggestBox. The field remains a real control (focus,
// editing, accessibility and popup lifetime); this component supplies only artwork-aware suggestion rows.
sealed class FluentRichOmnibar : Component
{
    readonly Signal<string> _text;
    readonly Action<string, string?> _go;
    readonly Signal<SearchSuggestions> _suggestions = new(SearchSuggestions.Empty);
    readonly Signal<bool> _loading = new(false);
    readonly Signal<int> _highlight = new(-1);
    // The merged row's centre island passes a parts map so it can capture AutoSuggestBox.PartRoot and put the caret in
    // the field on the click-expand edge. Null = the field owns its own root (every other host).
    readonly TemplateParts? _parts;
    readonly float _maxWidth;
    readonly AutoSuggestBoxSuggestionPresentation _suggestionPresentation;
    readonly bool _allowNarrowSuggestions;

    public FluentRichOmnibar(Signal<string> text, Action<string, string?> go, TemplateParts? parts = null,
        float maxWidth = 480f,
        AutoSuggestBoxSuggestionPresentation suggestionPresentation = AutoSuggestBoxSuggestionPresentation.Popup,
        bool allowNarrowSuggestions = false)
    {
        _text = text; _go = go; _parts = parts; _maxWidth = maxWidth;
        _suggestionPresentation = suggestionPresentation;
        _allowNarrowSuggestions = allowNarrowSuggestions;
    }

    public override Element Render()
    {
        var svc = UseContext(Services.Slot);
        var post = UsePost();
        var goOrigin = UseContext(HistoryStore.GoWithOrigin);
        string text = UseDebouncedValue(() => _text.Value.Trim(), AutoSuggestBox.TextChangedDebounceMs).Value;
        UseEffect(() => StartFetch(svc, post, text), text);
        var completion = UseComputed(() =>
        {
            if (_highlight.Value >= 0) return "";
            return SearchSuggestions.GhostFor(_text.Value.Trim(), _suggestions.Value.Queries) ?? "";
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
            var suggestions = _suggestions.Peek();
            int queryCount = Math.Min(6, suggestions.Queries.Count);
            int itemCount = Math.Min(10, suggestions.Items.Count);
            if (selection < 0 || selection >= queryCount + itemCount) return false;

            if (selection < queryCount)
            {
                string query = suggestions.Queries[selection];
                _text.Value = query;
                _go("search", query);
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
            var suggestions = _suggestions.Peek();
            int count = Math.Min(6, suggestions.Queries.Count) + Math.Min(10, suggestions.Items.Count);
            if (count == 0) { _highlight.Value = -1; return; }
            int current = _highlight.Peek();
            _highlight.Value = delta > 0
                ? (current + 1 >= count ? -1 : current + 1)
                : (current < 0 ? count - 1 : current - 1);
        }

        var presenter = new AutoSuggestBoxPresenter(
            Build: context => Embed.Comp(() => new OmnibarSuggestionsPopup(
                _text, _suggestions, _loading, context.Width, _highlight,
                selection => { if (InvokeSelection(selection)) context.Close(); },
                close: context.Close, allowNarrow: _allowNarrowSuggestions)),
            MoveSelection: MoveSelection,
            SubmitSelection: () => InvokeSelection(_highlight.Peek()),
            ResetSelection: () => _highlight.Value = -1);

        // Stock AutoSuggestBox metrics: a 32-DIP field at ControlCornerRadius (cornerRadius 0 resolves to Radii.Control
        // inside the box) with the control-default chrome — no pill, no elevation ring. 480 is the stock search cap.
        return AutoSuggestBox.Create(Array.Empty<string>(), Loc.Get(Strings.Shell.SearchPlaceholder),
            grow: 1f, maxFillWidth: _maxWidth, text: _text, onQuerySubmitted: Submit,
            minHeight: 32f, cornerRadius: 0f, presenter: presenter, parts: _parts,
            chrome: AutoSuggestBoxChrome.Standard, suggestionPresentation: _suggestionPresentation,
            completion: completion);
    }

    void StartFetch(Services? svc, Action<Action> post, string q)
    {
        if (q.Length == 0 || svc is null) { _suggestions.Value = SearchSuggestions.Empty; _loading.Value = false; return; }
        _loading.Value = true;
        _ = Run();

        async System.Threading.Tasks.Task Run()
        {
            try
            {
                var s = await svc.Library.SuggestRichAsync(q).ConfigureAwait(false);
                post(() => { if (_text.Peek().Trim() == q) { _suggestions.Value = s; _loading.Value = false; } });
            }
            catch { post(() => { if (_text.Peek().Trim() == q) _loading.Value = false; }); }
        }
    }
}

sealed class OmnibarSuggestionsPopup : Component
{
    readonly Signal<string> _text;
    readonly IReadSignal<SearchSuggestions> _suggestions;
    readonly IReadSignal<bool> _loading;
    readonly IReadSignal<float> _width;
    readonly Services? _svc;
    readonly Action<string, string?>? _go;
    readonly Action? _close;
    readonly IReadSignal<int>? _highlight;
    readonly Action<int>? _choose;
    readonly bool _allowNarrow;
    Action<string, string?, NavOrigin?>? _goOrigin;

    public OmnibarSuggestionsPopup(Signal<string> text, IReadSignal<SearchSuggestions> suggestions, IReadSignal<bool> loading,
        IReadSignal<float> width, Services? svc, Action<string, string?> go, Action close)
    {
        _text = text; _suggestions = suggestions; _loading = loading; _width = width; _svc = svc; _go = go; _close = close;
    }

    public OmnibarSuggestionsPopup(Signal<string> text, IReadSignal<SearchSuggestions> suggestions, IReadSignal<bool> loading,
        IReadSignal<float> width, IReadSignal<int> highlight, Action<int> choose, Action? close = null)
    {
        _text = text; _suggestions = suggestions; _loading = loading; _width = width;
        _highlight = highlight; _choose = choose; _close = close;
    }

    public OmnibarSuggestionsPopup(Signal<string> text, IReadSignal<SearchSuggestions> suggestions, IReadSignal<bool> loading,
        IReadSignal<float> width, IReadSignal<int> highlight, Action<int> choose, Action? close, bool allowNarrow)
    {
        _text = text; _suggestions = suggestions; _loading = loading; _width = width;
        _highlight = highlight; _choose = choose; _close = close; _allowNarrow = allowNarrow;
    }

    public override Element Render()
    {
        string q = _text.Value.Trim();
        var s = _suggestions.Value;
        bool loading = _loading.Value;
        int highlighted = _highlight?.Value ?? -1;
        // FLOOR, not just fallback: the popup width tracks the anchor field, and the merged chrome's icon-mode search
        // can be crushed to ChromeSearchIconW when the centre column has no room — anchoring a 40-DIP dropdown that
        // renders every row as a vertical sliver. A popup is an overlay; it may be wider than its anchor. 400 keeps a
        // cover + title + trailing actions legible (the overlay layer clamps to the window edge like any flyout).
        float measuredWidth = _width.Value > 0f ? _width.Value : 720f;
        float width = _allowNarrow ? measuredWidth : MathF.Max(measuredWidth, 400f);
        // Live path (FluentRichOmnibar) does not pass Services/go — resolve them from ambient context so row actions
        // (Play / Like / context menu) work the same as the retained RichOmnibar constructor.
        var svc = _svc ?? UseContext(Services.Slot);
        var acts = UseContext(ActionServices.Slot);
        var overlay = UseContext(Overlay.Service);
        var lib = UseContext(LibraryBridge.Slot);
        var goOrigin = UseContext(HistoryStore.GoWithOrigin);
        _goOrigin = goOrigin;

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
            body = loading
                ? new BoxEl { Width = width, MinWidth = width, MinHeight = AutoSuggestBox.ItemMinHeight }
                : new BoxEl
                {
                    Width = width, MinWidth = width, MinHeight = AutoSuggestBox.ItemMinHeight,
                    AlignItems = FlexAlign.Center,
                    Padding = new Edges4(24, 0, 24, 0),
                    Children = [new TextEl(Loc.Get(Strings.Search.NoResults)) { Size = 14f, Color = Tok.TextPrimary, Grow = 1f }],
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
            Children = loading ? [ProgressBar.Indeterminate(width), body] : [body],
        };
    }

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
        OnClick = () =>
        {
            if (_choose is not null) { _choose(selectionIndex); return; }
            _text.Value = query;
            _go?.Invoke("search", query);
            _close?.Invoke();
        },
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
        Action open = () =>
        {
            if (_choose is not null) { _choose(selectionIndex); return; }
            Invoke(item, svc);
        };
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

    void Invoke(SearchSuggestionItem item, Services? svc = null)
    {
        svc ??= _svc;
        switch (item.Kind)
        {
            case SearchSuggestionKind.Track:
                if (svc is not null) _ = svc.Player.PlayTrackAsync(item.Uri);
                break;
            case SearchSuggestionKind.Artist:
                _go?.Invoke("artist:" + item.Uri, item.Title);
                break;
            case SearchSuggestionKind.Album:
                _go?.Invoke("album:" + item.Uri, item.Title);
                break;
            case SearchSuggestionKind.Playlist:
                _go?.Invoke("pl:" + item.Uri, item.Title);
                break;
            case SearchSuggestionKind.Podcast:
            case SearchSuggestionKind.Audiobook:
                _go?.Invoke("show:" + item.Uri, item.Title);
                break;
            case SearchSuggestionKind.Episode:
                if (svc is not null) _ = svc.Player.PlayAsync(item.Uri, 0);
                break;
            case SearchSuggestionKind.Genre:
                if (_go is { } go)
                {
                    string q = _text.Peek().Trim();
                    var origin = q.Length == 0 ? (NavOrigin?)null : new NavOrigin(q, "search", q);
                    SearchRoutes.OpenGenre(item.Uri, item.Title, go, origin, _goOrigin);
                }
                break;
        }
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
