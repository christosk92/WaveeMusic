// ── Screens/SettingsSearchModel.cs — the Settings page's search box: its query, its results, its keyboard and its focus (contract 1.116) ─
//
// Role: CORE (settings rework phase 4, handoff/settings-2026-11-18/DESIGN.md 4.8). Every theme draws its own box (WinUI's "Find a setting"
// AutoSuggestBox, Classic's masthead box, Zune's "find a setting" pill) and its own results (live rows in Classic and WinUI, two-line links in
// Zune); what the box DOES is here, so a theme only binds it:
//   · `Query`      the box's text cell (bind it two-way). The page host re-ranks on every change (`Refresh`, an effect: never per frame) into
//                  this model's own buffers and moves `Version`; an empty query is no results page (the category body or the home shows);
//   · results      `Count`, `Hit(i)` (the dial or pointer, its title, its breadcrumb "Category › Group" as `Place`, its `Under` parent),
//                  `Spans(i)` (the title's matched letters, Zune's accent), `Close` ("Showing close matches"), and the no-match state
//                  (`NoMatch`, `NoMatchLine()`, `DidYouMean(i)`, `SearchAppLine()` and `SearchApp`, the whole-app search with the query);
//   · keyboard     `OnKeyDown` / `PreviewKey` (wire either to the box): Down/Up move `Highlight` through the results, Enter opens the
//                  highlighted one (the first when none is) in its category, Escape clears the box, a second Escape leaves it. A box
//                  bound with `Box(frame)` gets Escape ahead of its field: a stock field (the AutoSuggestBox's editor) handles Escape
//                  itself (it restores the typed text and blurs) and an open suggestion list takes it in the overlay host's key
//                  preview, so neither ever bubbles to the frame. While the caret is in the box, `Box` chains the window's one key
//                  preview (`InputHooks.KeyPreview`, the slot the overlay host and the full screen chain too): the list closes and
//                  the box clears in one Escape, a second leaves the box;
//   · focus        Ctrl+F while Settings is the page focuses the box instead of the app's search, Ctrl+, opens Settings with the box focused
//                  (`Settings.FindInPage`, `Settings.OpenWithSearch`); `Box(frame)` binds a theme's box to all of it (it keys the frame
//                  `settings:search`, takes the focus tickets, routes the keys). A theme that binds without `Box` reads `FocusTicket` and calls
//                  `ConsumeFocus` the way the omnibar's forms do.
// COST (design 4.5): a keystroke allocates only the query string the box writes. The ranking runs on the built index into the caller-owned
// buffers here (hits, spans, did-you-mean words); the lines a theme prints are formatted only when it asks (`NoMatchLine()`).
// ROOT-LIFETIME: a remounted root starts with an empty box.

using FluentGpu;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;

namespace Wavee;

/// <summary>The Settings page's search box model (see the file header). One per root: <see cref="Current"/>.</summary>
public sealed class SettingsSearchModel
{
    /// <summary>Matched runs kept per result.</summary>
    public const int MaxSpans = 4;

    /// <summary>A focus request older than this is not acted on by a box that mounts later (a Ctrl+, that opened a page with no box).</summary>
    const long FocusRequestLifeMs = 3000;

    static readonly RootState<SettingsSearchModel> s_state = new("Settings.Search", static () => new SettingsSearchModel());

    /// <summary>The current root's model.</summary>
    public static SettingsSearchModel Current => s_state.Value;

    readonly SettingsHit[] _hits = new SettingsHit[SettingsSearch.PageResults];
    readonly SettingsTextSpan[] _spans = new SettingsTextSpan[SettingsSearch.PageResults * MaxSpans];
    readonly byte[] _spanCount = new byte[SettingsSearch.PageResults];
    readonly string?[] _words = new string?[3];
    readonly Signal<int> _version = new(0);
    readonly Signal<int> _highlight = new(-1);
    readonly Signal<int> _focusTicket = new(0);
    int _count, _suggested, _focusConsumed, _boxes;
    bool _close;
    long _focusAskedMs;
    string _ran = "", _input = "";

    SettingsSearchModel()
    {
        SearchApp = () =>
        {
            string q = _ran;
            if (q.Length == 0) return;
            Shell.GoTo(Shell.Parse("search", q));
        };
        PreviewKey = Key;
        OnKeyDown = e => { if (!e.Handled && Key(e)) e.Handled = true; };
    }

    /// <summary>The box's text (bind it two-way). A change re-ranks (the page host's effect).</summary>
    public Signal<string> Query { get; } = new("");

    /// <summary>Moves whenever the results change: a results view reads it to re-render.</summary>
    public IReadSignal<int> Version => _version;

    /// <summary>The highlighted result (Down/Up), or -1 for none.</summary>
    public IReadSignal<int> Highlight => _highlight;

    /// <summary>The query the results answer (empty: no results page).</summary>
    public string Ranked => _ran;

    /// <summary>A query is ranked: the body is the results page (or its no-match state).</summary>
    public bool Active => _ran.Length > 0;

    /// <summary>How many results (at most <see cref="SettingsSearch.PageResults"/>).</summary>
    public int Count => _count;

    /// <summary>No dial matched every word: these are close matches.</summary>
    public bool Close => _close;

    /// <summary>A ranked query with no result: the no-match state.</summary>
    public bool NoMatch => _ran.Length > 0 && _count == 0;

    /// <summary>Result <paramref name="i"/>: its dial or pointer, title, breadcrumb (<see cref="SettingsHit.Place"/>) and parent result
    /// (<see cref="SettingsHit.Under"/>). A copy of numbers and references: nothing allocates.</summary>
    public SettingsHit Hit(int i) => (uint)i < (uint)_count ? _hits[i] : default;

    /// <summary>The runs of result <paramref name="i"/>'s title the query matched (Zune's accent letters).</summary>
    public ReadOnlySpan<SettingsTextSpan> Spans(int i) => (uint)i < (uint)_count ? _spans.AsSpan(i * MaxSpans, _spanCount[i]) : default;

    /// <summary>How many "did you mean" words the no-match state offers (0-3).</summary>
    public int DidYouMeanCount => _suggested;

    /// <summary>"Did you mean" word <paramref name="i"/> (an indexed word two or three edits from the query's).</summary>
    public string DidYouMean(int i) => (uint)i < (uint)_suggested ? _words[i] ?? "" : "";

    /// <summary>"No settings match "xyz"." (formatted when asked).</summary>
    public string NoMatchLine() => Strings.Settings.Search.NoMatch(_ran);

    /// <summary>"Did you mean".</summary>
    public string DidYouMeanLabel => Loc.Get(Strings.Settings.Search.DidYouMean);

    /// <summary>"Search the whole app for "xyz"" (formatted when asked): the label of <see cref="SearchApp"/>.</summary>
    public string SearchAppLine() => Strings.Settings.Search.SearchApp(_ran);

    /// <summary>"Showing close matches".</summary>
    public string CloseLine => Loc.Get(Strings.Settings.Search.CloseMatches);

    /// <summary>Hands the query to the app's own search.</summary>
    public Action SearchApp { get; }

    /// <summary>The box's keys as a preview (an editor's <c>PreviewKeyDown</c>): true when the key was the search's.</summary>
    public Func<KeyEventArgs, bool> PreviewKey { get; }

    /// <summary>The box's keys as a bubbling handler (a container's <c>OnKeyDown</c>): marks the key handled when it was the search's.</summary>
    public Action<KeyEventArgs> OnKeyDown { get; }

    /// <summary>A focus request (Ctrl+F on Settings, Ctrl+,): a box acts on a ticket newer than the last one acted on (<see cref="ConsumeFocus"/>).</summary>
    public IReadSignal<int> FocusTicket => _focusTicket;

    /// <summary>A bound box is mounted (<see cref="Box"/>): Ctrl+F on Settings goes to it.</summary>
    public bool HasBox => _boxes > 0;

    /// <summary>Act on focus ticket <paramref name="ticket"/> at most once, and only while it is fresh: a box mounting later never takes the
    /// caret for a request from long ago.</summary>
    public bool ConsumeFocus(int ticket)
    {
        if (ticket <= _focusConsumed) return false;
        _focusConsumed = ticket;
        return Environment.TickCount64 - _focusAskedMs <= FocusRequestLifeMs;
    }

    /// <summary>Ask the box for the caret (Ctrl+F on Settings, Ctrl+,).</summary>
    public void RequestFocus()
    {
        _focusAskedMs = Environment.TickCount64;
        _focusTicket.Value = _focusTicket.Peek() + 1;
    }

    /// <summary>Open result <paramref name="i"/>: a dial in its category (the deep-link door: the parent's expander, the glide, the highlight;
    /// the box clears), a pointer's place.</summary>
    public void Open(int i)
    {
        if ((uint)i >= (uint)_count) return;
        SettingsHit hit = _hits[i];
        if (hit.Pointer is { } pointer)
        {
            Clear();
            pointer.Open();
        }
        else if (hit.Dial is { } dial) Settings.Open(dial.Id);
    }

    /// <summary>Empty the box (and the results).</summary>
    public void Clear()
    {
        if (Query.Peek().Length > 0) Query.Value = "";
        Refresh("");
    }

    /// <summary>Rank <paramref name="query"/> into the buffers and move <see cref="Version"/> (the page host calls it on every change of
    /// <see cref="Query"/>; the same text again ranks nothing unless <paramref name="force"/>). Allocation-free on a built index.</summary>
    internal void Refresh(string query, bool force = false)
    {
        if (!force && string.Equals(query, _input, StringComparison.Ordinal)) return;
        _input = query;
        ReadOnlySpan<char> q = query.AsSpan().Trim();
        if (q.IsEmpty)
        {
            if (_ran.Length == 0 && _count == 0 && !force) return;
            _ran = "";
            _count = _suggested = 0;
            _close = false;
        }
        else
        {
            SettingsSearchResult result = SettingsSearch.Run(q, _hits);
            _count = result.Count;
            _close = result.Close;
            for (int i = 0; i < _count; i++)
                _spanCount[i] = (byte)SettingsSearch.TitleSpans(q, _hits[i].Title, _spans.AsSpan(i * MaxSpans, MaxSpans));
            _suggested = _count == 0 ? SettingsSearch.Suggest(q, _words) : 0;
            _ran = query;
        }
        _highlight.Value = -1;
        _version.Value = _version.Peek() + 1;
    }

    /// <summary>The search's keys (see the file header). Returns whether the key was the search's.</summary>
    bool Key(KeyEventArgs e)
    {
        if (e.Ctrl || e.Alt) return false;
        switch (e.KeyCode)
        {
            case Keys.Down:
                if (_count == 0) return false;
                _highlight.Value = Math.Min(_highlight.Peek() + 1, _count - 1);
                return true;
            case Keys.Up:
                if (_count == 0 || _highlight.Peek() < 0) return false;
                _highlight.Value = _highlight.Peek() - 1;
                return true;
            case Keys.Enter:
                if (_count == 0) return false;
                int h = _highlight.Peek();
                Open(h >= 0 ? h : 0);
                return true;
            case Keys.Escape:
                return Escape(null);
        }
        return false;
    }

    /// <summary>The box holds text or shows results: an Escape clears it.</summary>
    bool HasText => Query.Peek().Length > 0 || _ran.Length > 0;

    /// <summary>Escape in the box: the first clears it (and its results), a second leaves it: the caret goes nowhere (the page keeps the
    /// keyboard, as after a click on it). False when there is nothing to clear and no focus to drop.</summary>
    bool Escape(InputHooks? hooks)
    {
        if (HasText)
        {
            Clear();
            return true;
        }
        if ((hooks ?? SettingsContext.Hooks)?.FocusNode is not { } focus) return false;
        focus(NodeHandle.Null, false);
        return true;
    }

    /// <summary>The box's Escape ahead of its field (see the file header): while the caret is anywhere in the bound box, the window's key
    /// preview runs this first. It holds the slot only while the box has the caret and puts back what it found when the caret leaves; the
    /// overlay host re-takes the slot whenever it re-renders (an overlay opened or closed), so the chain is re-asserted after each frame's
    /// animations while the box is focused (a reference compare on frames that render anyway, nothing allocated).</summary>
    sealed class EscapeChain
    {
        readonly SettingsSearchModel _model;
        readonly Func<int, bool> _preview;
        readonly Action _reassert;
        InputHooks? _hooks;
        Func<int, bool>? _saved;
        bool _within, _running;

        public EscapeChain(SettingsSearchModel model)
        {
            _model = model;
            _preview = Preview;
            _reassert = Reassert;
        }

        /// <summary>The caret entered (true) or left (false) the box: a routed focus change on the box's frame.</summary>
        public void Focus(InputHooks hooks, bool within)
        {
            if (within == _within) return;
            if (!within)
            {
                Release();
                return;
            }
            _within = true;
            _hooks = hooks;
            Install();
            hooks.SetAfterAnimations(this, _reassert);
        }

        /// <summary>Leave the slot (the caret left, or the box unmounted), putting back what it held when this chain still owns it.</summary>
        public void Release()
        {
            _within = false;
            if (_hooks is not { } hooks) return;
            hooks.SetAfterAnimations(this, null);
            if (ReferenceEquals(hooks.KeyPreview, _preview)) hooks.KeyPreview = _saved;
            _saved = null;
            _hooks = null;
        }

        void Install()
        {
            if (_hooks is not { } hooks || ReferenceEquals(hooks.KeyPreview, _preview)) return;
            _saved = hooks.KeyPreview;
            hooks.KeyPreview = _preview;
        }

        void Reassert()
        {
            if (_within) Install();
        }

        bool Preview(int key)
        {
            if (_running) return false;   // a chain that saved this one and was re-wrapped by it: never loop
            _running = true;
            try
            {
                if (key != Keys.Escape || !_within) return _saved?.Invoke(key) ?? false;
                bool text = _model.HasText;
                // An open suggestion list closes first (the overlay host's own Escape): with text that is part of the clear; on an empty box an
                // overlay that took the key is all this Escape does.
                bool closed = _saved?.Invoke(key) ?? false;
                if (text || !closed) _model.Escape(_hooks);
                return true;
            }
            finally
            {
                _running = false;
            }
        }
    }

    // ── the binding a theme wraps its box in ─────────────────────────────────────────────────────────────────────────

    /// <summary>Bind a theme's box: <paramref name="frame"/> (the theme's own box node, holding its field) comes back keyed
    /// <see cref="SettingsKeys.Search"/>, its keys routed through <see cref="OnKeyDown"/>, Escape taken ahead of its field while the caret is in
    /// it (the first clears, a second leaves: see the file header), and the caret moved into its first focusable node on a focus request
    /// (Ctrl+F on Settings, Ctrl+,). Its sizing is the frame's own; the anchor mirrors it.</summary>
    public Element Box(BoxEl frame) => Embed.Comp(new BoxProps(this, frame), static () => new BoxHost());

    sealed record BoxProps(SettingsSearchModel Model, BoxEl Frame);

    sealed class BoxHost : Component
    {
        public override Element Render()
        {
            var p = UseProps<BoxProps>();
            SettingsSearchModel model = p.Model;
            var hooks = UseContext(InputHooks.Current);
            var node = UseRef<NodeHandle>(default);
            var escape = UseRef<EscapeChain?>(null);
            EscapeChain chain = escape.Value ??= new EscapeChain(model);
            UseEffect(() =>
            {
                model._boxes++;
                return () =>
                {
                    model._boxes--;
                    chain.Release();
                };
            }, DepKey.Empty);
            int ticket = model._focusTicket.Value;
            UseLayoutEffect(() =>
            {
                if (node.Value.IsNull || !model.ConsumeFocus(ticket)) return;
                NodeHandle editor = hooks.FirstFocusableIn?.Invoke(node.Value) ?? NodeHandle.Null;
                hooks.FocusNode?.Invoke(editor.IsNull ? node.Value : editor, true);
            }, DepKey.From(ticket));
            BoxEl frame = p.Frame;
            Action<NodeHandle>? realized = frame.OnRealized;
            Action<KeyEventArgs>? keys = frame.OnKeyDown;
            Action<bool>? focusChanged = frame.OnFocusChanged;
            return frame with
            {
                Key = SettingsKeys.Search,
                OnRealized = h =>
                {
                    node.Value = h;
                    realized?.Invoke(h);
                },
                OnKeyDown = keys is null ? model.OnKeyDown : e =>
                {
                    keys(e);
                    model.OnKeyDown(e);
                },
                // Routed: the frame hears the caret entering and leaving its subtree (the field inside it holds the caret).
                OnFocusChanged = within =>
                {
                    chain.Focus(hooks, within);
                    focusChanged?.Invoke(within);
                },
            };
        }
    }
}
