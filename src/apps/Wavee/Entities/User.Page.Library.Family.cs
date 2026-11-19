// ── Entities/User.Page.Library.Family.cs ────────────────────────────────────────────────────────────────────────────
// the library FAMILY's CORE HOST (`ShellCapabilities.LibraryFamily`, WinUI): the four master-detail kinds (albums, artists, podcasts,
// audiobooks) as ONE page under ONE head, the kinds' bodies switching under it.
//
// Role: CORE host
// Owner: O
// Spec: C:/WAVEE/scratch/raster/winui-library-spec.md §4-§5 (2026-11-15). Owner: "it does entire page navigation instead of navigating the
//       inner content here and doing left/right anims".
//
// ── THE SHAPE ─────────────────────────────────────────────────────────────────────────────────────────────────────────
//
//  content host KeepAlive           slot "<tab> library" (Shell.SlotNameOf), content key "page:library"
//  └─ LibraryFamily (keyed "library")           props: the route's kind, re-pushed on every switch inside the slot
//     ├─ head    LibraryControlsStyle.Head(kinds, title)    drawn ONCE: the pivot's pill moves between its words
//     └─ body    "lib:family:body" (Grow, MinHeight 0, clipped)
//        └─ Flow.KeepAlive(the kind signal, 4 entries, the structure's pivot recipe by the pivot's order)
//           ├─ LibraryKindEntry "library:albums"  ─ its own LoadingScope and MeasureMemory scope ─ LibraryPage(albums, head hosted)
//           ├─ LibraryKindEntry "library:artists" ─ parked: scroll, selection, filter text and pane all live, not restored
//           └─ ...
//
// Each kind stays a PLACE (Shell.SameSlot is untouched): history, Back, tab restore, deep links, the tab's title and the nav pane's lit row follow the
// route. Only the keep-alive slot and the content key are shared, and the content host plays no page transition between two kinds of one family
// (Shell.UI.cs PageTransition): the family's body plays the switch, by the pivot's order whichever verb moved (the pivot, the nav pane, Back).
//
// THE KIND REACHES THE INNER KEEP-ALIVE THROUGH A SIGNAL. A keep-alive boundary's Active, KeyOf, View and options freeze at mount, so the host owns a
// signal the boundary reads and writes it from an effect keyed on the pushed kind (never from a render).
//
// PER KIND, around the entry: its own MeasureMemory scope (the slot's is shared by four pages whose regions use the same role keys: what each kind's own
// slot answered before) and its own LoadingScope. A bridge forwards only the ACTIVE kind's pending state to the slot's scope, which draws the page bar: a
// parked kind that is still loading never holds it. A kind's first mount arrives whole (RenderContext.ArriveWhole): the body's slide presents it, so no
// part inside fades in on its own under that.

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;

namespace Wavee;

public readonly partial struct User
{
    // ══ THE KINDS (the head's pivot words) ═══════════════════════════════════════════════════════════════════════════

    /// <summary>The library's kinds as the head's pivot lists them (Albums, Artists, Podcasts, Audiobooks, Liked Songs), one cached list per selected kind
    /// with one cached verb per kind, so an unchanged head compares equal and a render allocates nothing for it. Rebuilt when the language changes (the
    /// titles are the destinations' words).</summary>
    internal static class LibraryKinds
    {
        static readonly Shell.RouteKind[] s_routes =
            [Shell.RouteKind.LibraryAlbums, Shell.RouteKind.LibraryArtists, Shell.RouteKind.LibraryPodcasts, Shell.RouteKind.LibraryAudiobooks, Shell.RouteKind.Liked];

        static readonly Action[] s_open = Array.ConvertAll(s_routes, static kind => (Action)(() => Shell.GoTo(new Shell.Route(kind))));

        /// <summary>One list per selected kind, the last slot for "none selected".</summary>
        static readonly IReadOnlyList<LibraryKindChip>?[] s_lists = new IReadOnlyList<LibraryKindChip>?[s_routes.Length + 1];
        static string s_culture = "";

        /// <summary>The kinds, <paramref name="selected"/> lit.</summary>
        public static IReadOnlyList<LibraryKindChip> For(Shell.RouteKind selected)
        {
            string culture = Loc.CurrentCulture;
            if (!string.Equals(culture, s_culture, StringComparison.Ordinal))
            {
                Array.Clear(s_lists);
                s_culture = culture;
            }
            int at = Array.IndexOf(s_routes, selected);
            int slot = at < 0 ? s_routes.Length : at;
            if (s_lists[slot] is { } cached) return cached;
            var chips = new LibraryKindChip[s_routes.Length];
            for (int i = 0; i < chips.Length; i++)
                chips[i] = new LibraryKindChip(Shell.Dest(new Shell.Route(s_routes[i])).Title, i == at, s_open[i]);
            s_lists[slot] = chips;
            return chips;
        }
    }

    // ══ THE FAMILY'S HOST ════════════════════════════════════════════════════════════════════════════════════════════

    sealed record LibraryFamilyProps(Shell.RouteKind Kind, int Tab);

    /// <summary>The family's host for a route of <paramref name="kind"/> in <paramref name="tab"/>: ONE component for the slot, keyed by the family's name, so
    /// a kind switch re-pushes its props to the mounted instance.</summary>
    static Element LibraryFamilyFor(Shell.RouteKind kind, int tab)
        => Embed.Comp(new LibraryFamilyProps(kind, tab), static () => new LibraryFamily()) with { Key = Shell.LibraryFamilyName };

    /// <summary>The library family (see the file header): the head once, the kinds' bodies in an inner keep-alive that slides by the pivot's order.</summary>
    [CoreHost]
    sealed class LibraryFamily : Component
    {
        /// <summary>Every kind stays mounted once visited: the family is ONE of the shell's slots, so cycling the kinds never evicts another page.</summary>
        const int Kinds = 4;

        readonly KeepAliveOptions _options;
        readonly Func<Shell.RouteKind> _active;
        readonly Func<Shell.RouteKind, Element> _entry;
        Signal<Shell.RouteKind>? _kind;
        IReadSignal<Theming.ThemeFacts>? _facts;
        LoadingScope? _slotLoading;
        int _tab;
        Element? _body;

        public LibraryFamily()
        {
            _active = () => _kind!.Value;
            _entry = kind => Embed.Comp(new LibraryKindEntryProps(kind, _tab, _slotLoading), static () => new LibraryKindEntry()) with { Key = EntryKey(kind) };
            _options = new KeepAliveOptions(
                MaxEntries: Kinds,
                TransitionFor: PivotTransition,
                // A kind coming back lands as it was parked: its layouts are placed, not animated in from where they stood.
                SuppressLayoutTransitionsOnActivation: true,
                // The body's slide and fade are composite parameters of the entry's layer: nothing below re-records.
                CompositeTransitions: true,
                // Reduced motion is the structure's recipe's call (its quick fade), not a cut.
                RecipeOwnsReducedMotion: true);
        }

        public override Element Render()
        {
            var p = UseProps<LibraryFamilyProps>();
            _tab = p.Tab;
            // The inner keep-alive's Active froze at mount: it reads this signal, which an effect keyed on the pushed kind writes (never this render).
            var kind = UseSignal(p.Kind);
            _kind = kind;
            var pushed = p.Kind;
            UseEffect(() => kind.Value = pushed, DepKey.From((int)pushed));
            _facts = Theming.TierContext.DrawnIn(UseContext(Theming.ThemeFactsContext.Signal));
            _slotLoading = UseContext(LoadingScope.Current);
            var registry = UseContext(ViewRegistry.Context);
            var head = registry is not null && registry.TryGetStyle(StyleKeys.LibraryControls, out var controls) ? controls.Head : null;

            _body ??= LibraryFamilyBody(Flow.KeepAlive(_active, static k => EntryKey(k), _entry, _options));
            return LibraryFamilyFrame(head?.Invoke(new LibraryHeadArgs(LibraryKinds.For(pushed), Shell.Dest(new Shell.Route(pushed)).Title)), _body);
        }

        /// <summary>The body's switch: none for the first kind (the page's own entrance presents it), else the structure's pivot recipe by the pivot's order
        /// (the new kind after the old one comes in from the right), else the core's quick same-kind fade.</summary>
        LayoutTransition? PivotTransition(object oldToken, object newToken)
        {
            if (oldToken is not Shell.RouteKind from || newToken is not Shell.RouteKind to || from == to) return null;
            bool fromRight = Shell.SiblingOrder(to) > Shell.SiblingOrder(from);
            var facts = (_facts ?? Theming.ThemeHost.Facts).Peek();
            if (facts.Policies.PageMotion.LibraryPivot is { } pivot) return pivot(fromRight, facts.Tiers);
            return LibraryFamilySwap;
        }

        static string EntryKey(Shell.RouteKind kind) => kind switch
        {
            Shell.RouteKind.LibraryArtists => "library:artists",
            Shell.RouteKind.LibraryPodcasts => "library:podcasts",
            Shell.RouteKind.LibraryAudiobooks => "library:audiobooks",
            _ => "library:albums",
        };
    }

    // ── the family's frame: positioning only (no look, no token), the content host's shape for a page under a head ──

    /// <summary>The head over the body, the body taking the rest of the page's height.</summary>
    static Element LibraryFamilyFrame(Element? head, Element body) => new BoxEl
    {
        Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, AlignItems = FlexAlign.Stretch,
        Children = head is { } h ? [h, body] : [body],
    };

    /// <summary>The body: the kinds' keep-alive, clipping the body that leaves and the one that slides in, as the content host clips its pages.</summary>
    static Element LibraryFamilyBody(Element kinds) => new BoxEl
    {
        Key = "lib:family:body", Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, MinHeight = 0f, ClipToBounds = true,
        Children = [kinds],
    };

    /// <summary>The body's switch where the structure declares no pivot recipe: the core's quick same-kind fade.</summary>
    static LayoutTransition LibraryFamilySwap => Design.Nav.SameKindSwap;

    sealed record LibraryKindEntryProps(Shell.RouteKind Kind, int Tab, LoadingScope? Slot);

    /// <summary>One kind inside the family: its page (the head left to the family) under its own loading and measurement scopes, and the bridge that hands the
    /// slot's page bar the pending state of the kind on screen only.</summary>
    sealed class LibraryKindEntry : Component
    {
        readonly LoadingScope _loading = new();   // one per kind
        Element? _page;
        bool _arrived;

        public override Element Render()
        {
            var p = UseProps<LibraryKindEntryProps>();
            var active = UseIsActive();
            // The bridge: the slot's scope draws the page bar; a parked kind that is still loading never holds it.
            UsePendingClaim(active.Value && _loading.IsPending.Value, p.Slot);
            // A kind's first mount arrives whole: the body's slide (or the page's own entrance, for the first kind) presents it.
            if (!_arrived) { _arrived = true; Context.ArriveWhole?.Invoke(); }
            var kind = p.Kind;
            return _page ??= Ctx.Provide(LoadingScope.Current, _loading,
                Ctx.Provide(MeasureMemory.Scope, Shell.KindMeasureScopeOf(new Shell.Route(kind, Tab: p.Tab)),
                    Embed.Comp(() => new LibraryPage(kind, headHosted: true))));
        }
    }
}
