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
        Board = () => Slots.View(ViewKeys.HomeBoard, Props());
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

    /// <summary>Hand over the page's memo of <see cref="Project"/> and its overlay service (called every page render; idempotent).</summary>
    public void Bind(IReadSignal<BoardModel> model, IOverlayService? overlay)
    {
        if (ReferenceEquals(_model, model) && ReferenceEquals(_overlay, overlay)) return;
        _model = model;
        _overlay = overlay;
        _props = null;
    }

    /// <summary>The board as projected right now: the document x the All answer (a subscribing read of both).</summary>
    public BoardModel Project()
    {
        var store = HomeBoardStore.Instance;
        var doc = store.Doc.Value;
        _ = Entities.ScopeEpoch.Value;
        _ = Home.Changed.Value;
        _ = Entities.Current.Edges.SectionCards.Changed.Value;
        var h = Entities.HomeFeed("");
        _ = h.Version; _ = h.SectionVersion;
        IReadOnlyList<SectionInput> answer = h.Knows(HomeFields.Sections) ? SectionReader.Read(h) : [];
        IReadOnlyList<SectionInput>? following = null;
        var fh = Entities.HomeFeed("music-following-chip");
        _ = fh.Version; _ = fh.SectionVersion;
        if (fh.Knows(HomeFields.Sections)) following = SectionReader.Read(fh);
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return HomeProjection.Project(doc, answer, following, charts: null, HomeSourceRegistry.Default, HomeScreen.LocalizedTitles(), now);
    }

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
        };
    }
}
