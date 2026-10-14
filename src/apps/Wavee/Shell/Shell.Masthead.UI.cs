// ── Shell/Shell.Masthead.UI.cs ─────────────────────────────────────────────────────────────────────────────────────
// masthead band (core host), material layer (host note), omnibar + suggestion popup (a cross-owner contract with P's Search.cs)
//
// Role: UI
// Owner: I
// Wave: 4
// Budget: 950 lines
// Spec: ch 18 §4 ("each is a named surface, not optional chrome")
//
// THREE NAMED SURFACES the frame mounts once each:
//   · the MASTHEAD BAND ("Browse › Category") — one overlay above the keep-alive boundary, never a page's own copy, so a
//     page swap cannot double-expose it and crossing out of the family never changes the boundary's height;
//   · the MATERIAL LAYER — the one layer between live Mica and the chrome column: a flat page tint or Home's three
//     clipped radial washes, re-rendered at navigation rate only (T4c: the structure's `Shell.Material`);
//   · the OMNIBAR — the field-mode field and the icon-mode flyout are two mounts of ONE omnibar over ONE suggestion
//     store, so a search-mode flip keeps the rows, the pending generation and the cursor. The SUGGESTION SOURCE is owner
//     P's (`Shell.Omnibar.Source`, Wave 5); with none installed the popup answers from the navigation log.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;
using ViewSlots = FluentGpu.Theming.Slots;

namespace Wavee;

public static partial class Shell
{
    // ══ 1. THE MASTHEAD BAND (ch 18 W13) ═══════════════════════════════════════════════════════════════════════════
    //
    // T4c: the band's PICTURE is the structure's (`Shell.Masthead`, `Themes\Classic\Structure\Views\Masthead\ClassicMasthead.cs`; the
    // trail-as-title row is `Page.Head`, `ClassicPageHead.cs`). This host keeps the state machine and hands the theme a `PageChrome`.

    /// <summary>The one masthead, mounted by the content host as an overlay on its page-swap boundary.</summary>
    // MOUNT POINT (stage B contract)
    public static Element Masthead() => Embed.Comp(static () => new MastheadBand());

    /// <summary>The masthead's core host: which route's publication is live, the chrome it HELD while an unknown family fades (a trail
    /// never collapses to nothing mid-swap), and the "Show all" verb that resolves the LATEST delegate at click time. It builds no element.</summary>
    [CoreHost]
    sealed class MastheadBand : Component
    {
        // An unknown family KEEPS the last trail and fades opacity; it never collapses to height 0 mid-swap.
        PageChrome? _held;
        Route _heldRoute;
        Action? _runTools;

        public override Element Render()
        {
            var route = Current.Value;
            var published = Mastheads.For(route);
            var origin = Origins.For(route);
            bool live = TryMasthead(route, published?.Title, origin, out var title, out var trail);
            if (live)
            {
                _held = ChromeFor(title!, trail, published);
                _heldRoute = route;
            }

            // "Show all" resolves the LATEST delegate at click time: a re-publish that changes only the delegate never
            // re-renders the band.
            return ViewSlots.View(ViewKeys.ShellMasthead, new MastheadProps
            {
                Held = _held,
                Live = live,
                RunTools = _runTools ??= RunTools,
            });
        }

        void RunTools() => Mastheads.Peek(_heldRoute)?.ToolsAction?.Invoke();
    }

    /// <summary>The chrome a masthead-family route publishes (T4c): its title, its trail as navigable crumbs and the "Show all" tool's
    /// state. A crumb with a route navigates to it; the current page (no route) is not clickable. Pure but for the verbs it captures.</summary>
    public static PageChrome ChromeFor(string title, IReadOnlyList<Crumb> trail, MastheadPublication? published)
    {
        var crumbs = new PageCrumb[trail.Count];
        for (int i = 0; i < crumbs.Length; i++)
        {
            var crumb = trail[i];
            crumbs[i] = new PageCrumb(crumb.Label, crumb.Route is { } target ? () => GoTo(target) : null);
        }
        return new PageChrome(title, crumbs, published is { ToolsVisible: true }, published is { ToolsLoading: true });
    }

    // ══ 2. THE MATERIAL LAYER (ch 00 §material, ch 18 W12) ════════════════════════════════════════════════════════
    //
    // T4c: the layer is the structure's (`Shell.Material`, `Themes\Classic\Structure\Views\Material\ClassicMaterial.cs`): the frame mounts it with
    // `MaterialState`. The channel (`ShellMaterial`, `TintOwnership`) is core: `Platform\ShellMaterial.cs`.

    // ══ 3. THE OMNIBAR (ch 18 W15) ════════════════════════════════════════════════════════════════════════════════

    /// <summary>The chrome row's ONE omnibar: the suggestion store (<see cref="OmnibarModel.Query"/>, its version and the
    /// keyboard cursor), the fetch edges, the focus tickets and the verbs are <see cref="OmnibarModel"/>'s; the field and the
    /// popup below are views over it. <see cref="OmnibarModel.SearchFocused"/> / <see cref="OmnibarModel.FlyoutOpen"/> are
    /// what the chrome allocator reads to hand the caret to the new form across a mode flip.</summary>
    static readonly OmnibarModel s_omnibar = new(OmnibarPorts.Live());

    /// <summary>Ask the omnibar for keyboard focus (Ctrl+F — <see cref="IFrameVerbs.FocusSearch"/>).</summary>
    internal static void FocusSearch() => s_omnibar.Focus();

    // ── test-only seam (RootScopeTests) ─────────────────────────────────────────────────────────────────────────────
    //
    // Wavee.ShellTests has no InternalsVisibleTo, so this is public (the repo's `*ForTests` convention, see Shell.cs). It
    // reads and writes the facts only a MOUNTED omnibar form holds, which `RootScope` puts back when its root unmounts.
    // Never called from production code.
    /// <summary>The omnibar facts a mounted form holds (see <see cref="OmnibarModel.ResetMountedFacts"/>).</summary>
    public readonly record struct OmnibarFacts(bool SearchFocused, bool FlyoutOpen, int Highlight);

    /// <summary>The omnibar facts now.</summary>
    public static OmnibarFacts OmnibarFactsForTests()
        => new(s_omnibar.SearchFocused.Peek(), s_omnibar.FlyoutOpen.Peek(), s_omnibar.Highlight.Peek());

    /// <summary>Write the facts a mounted form would (the caret in the field, the flyout open, a row highlighted).</summary>
    public static void SetOmnibarFactsForTests(OmnibarFacts facts)
    {
        s_omnibar.SearchFocused.SetIfChanged(facts.SearchFocused);
        s_omnibar.FlyoutOpen.SetIfChanged(facts.FlyoutOpen);
        s_omnibar.Highlight.SetIfChanged(facts.Highlight);
    }

    // T4b (themes plan 6.1): the field, the icon-mode flyout button, the rich omnibar and the popup body are the structure's
    // (`Shell.Search` and `Shell.SearchSuggestions`: `Themes\Classic\Structure\Views\Search\ClassicSearch.cs`, mounted by the
    // title bar's template); the answer the popup draws, the rows' facts and the field width rule are `+Shell.TitleBar.cs`.
}
