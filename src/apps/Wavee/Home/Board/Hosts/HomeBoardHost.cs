// ── Home/Board/Hosts/HomeBoardHost.cs — the pinned Home board's core host (pinned-home-tech.md 1.5) ────────────────────────────────
//
// Role: CORE. One per Home tab, held by `HomeScreen` for its life. It owns what the board needs that is not the document: the view state
// (`HomeBoardView`), the grid controller, the verbs (`HomeBoardVerbs`: dispatch, the theme's confirmation, announcements) and the props record
// both views read (`Home.Board` and its head-row tools, `Home.BoardTools`). The model is derived by the page's own component (`HomeScreen`
// calls `Project` inside a `UseComputed`), so the board and its tools share ONE memo and a board change never re-renders the page.
//
// It draws nothing (WVTH008): no element types, no tokens. Every tile is the core tile host of its source (`Tile.Hosts.cs`).

using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Hooks;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;
using Wavee.HomeUi;
using Wavee.Theming.Contracts;
using Slots = FluentGpu.Theming.Slots;

namespace Wavee;

/// <summary>The board's core host: view state, verbs, controller, props.</summary>
[CoreHost]
public sealed class HomeBoardHost
{
    readonly ScrollHandle _scroll;
    readonly HomeBoardView _view = new();
    readonly PackGridController _grid = new();
    readonly HomeBoardVerbs _verbs;
    IReadSignal<BoardModel>? _model;
    IOverlayService? _overlay;
    HomeBoardProps? _props;

    /// <summary>A host over the page's scroller.</summary>
    public HomeBoardHost(ScrollHandle scroll)
    {
        _scroll = scroll;
        _verbs = new HomeBoardVerbs(_view, _grid, () => _model?.Peek() ?? BoardModel.Empty);
        _view.Grid = _grid;
        Inspector = new HomeBoardInspector(this);
        Board = () => Embed.Comp(new HomeBoardMountProps(Props(), this), static () => new HomeBoardMount()) with { Key = "home:board-mount" };
        Tools = () => Slots.View(ViewKeys.HomeBoardTools, Props());
    }

    /// <summary>The board view (the page mounts it where its zone column stood).</summary>
    public Func<Element> Board { get; }

    /// <summary>The board's head-row tools.</summary>
    public Func<Element> Tools { get; }

    /// <summary>The view state.</summary>
    public HomeBoardView View => _view;

    /// <summary>The verbs.</summary>
    public IHomeBoardVerbs Verbs => _verbs;

    /// <summary>The grid's controller.</summary>
    public PackGridController Grid => _grid;

    /// <summary>The board as the inspector drives it (registered while the board is mounted).</summary>
    internal IHomeBoardInspect Inspector { get; }

    /// <summary>The board as projected right now.</summary>
    public BoardModel ModelNow => _model?.Peek() ?? BoardModel.Empty;

    /// <summary>Hand over the page's memo of <see cref="Project"/> and its overlay service (called every page render; idempotent).</summary>
    public void Bind(IReadSignal<BoardModel> model, IOverlayService? overlay)
    {
        if (ReferenceEquals(_model, model) && ReferenceEquals(_overlay, overlay)) return;
        _model = model;
        _overlay = overlay;
        _props = null;
        // Commands address entries of the board as shown (feed sections, fill cells).
        HomeBoardStore.Instance.Shown = () => model.Peek();
    }

    /// <summary>The board as projected right now: the document x the All answer (a subscribing read of both).</summary>
    public BoardModel Project()
    {
        var store = HomeBoardStore.Instance;
        var doc = store.Doc.Value;
        _ = Entities.ScopeEpoch.Value;
        _ = Home.Changed.Value;
        _ = Entities.Current.Edges.SectionCards.Changed.Value;
        _ = Entities.Current.Edges.SectionPreviewTracks.Changed.Value;   // a preview card's (and a fold's) clusters land with their preview tracks
        var h = Entities.HomeFeed("");
        _ = h.Version; _ = h.SectionVersion;
        IReadOnlyList<SectionInput> answer = h.Knows(HomeFields.Sections) ? SectionReader.Read(h) : [];
        IReadOnlyList<SectionInput>? following = null;
        var fh = Entities.HomeFeed("music-following-chip");
        _ = fh.Version; _ = fh.SectionVersion;
        if (fh.Knows(HomeFields.Sections)) following = SectionReader.Read(fh);
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return HomeProjection.Project(doc, answer, following, charts: null, HomeSourceRegistry.Default, HomeZoneTitles.Localized(), now,
            HomeProjection.FeedStateOf(h));
    }

    /// <summary>Once the All answer is known, hand it to the store so a v2 document's hidden zones resolve to section families.</summary>
    public static void ResolveLegacyOnAnswer()
    {
        var store = HomeBoardStore.Instance;
        if (store.Doc.Peek().LegacyHidden.IsDefaultOrEmpty) return;
        var h = Entities.HomeFeed("");
        if (!h.Knows(HomeFields.Sections)) return;
        store.ResolveLegacy(SectionReader.Read(h));
    }

    RecentGrid.Cache? _recents;

    /// <summary>Recently played for the shortcuts grid's other face: the Recents zone of the All answer, folded once per Recents version.</summary>
    HomeRecentsProps RecentsNow() => RecentGrid.PropsOf(HomeBoardZones.RecentsZone(), ref _recents);

    HomeBoardProps Props()
    {
        if (_props is { } p) return p;
        var model = _model ?? throw new InvalidOperationException("HomeBoardHost.Bind was not called");
        var overlay = Controls.IsNullOverlay(_overlay) ? null : _overlay;
        _verbs.Overlay = overlay;
        return _props = new HomeBoardProps
        {
            Model = model,
            View = _view,
            Verbs = _verbs,
            Tile = (entry, cell, form) => HomeTileHost.For(entry, cell, form, _verbs, overlay, _view),
            Options = entry => HomeTileHost.OptionsFor(entry, _verbs, _view),
            Catalog = HomeCatalogViews.Create(model, _verbs, overlay, _view),
            Undo = HomeBoardStore.Instance.UndoState,
            Grid = _grid,
            Scroll = _scroll,
            ReadOnly = HomeBoardStore.Instance.ReadOnly,
            Overlay = overlay,
            EditorFocused = Shell.FocusedIsTextEditor,
            Recents = RecentsNow,
        };
    }
}
