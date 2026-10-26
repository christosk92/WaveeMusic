// ── Themes/Common/CommonNothing.cs — the view that draws nothing, one copy for every theme (themes plan 6.5, P5 ZK) ────────────
//
// A structure that has no picture for a published key (Zune: the narrow drawer, the masthead, the nav pane's flyouts and heads, the
// Home facet row whose words moved to the band) still registers the key, so the registry is complete (`RegistryCompletenessTests`)
// and the slot never logs "renders nothing". It registers THIS block for it: a zero-size box, out of layout flow and hit-testing,
// with no state and no effect, whatever the props. One call per key, from the structure's `Register`:
//
//     CommonNothing.Register(views, ViewKeys.ShellNarrowDrawer);   // template id "common.nothing.Shell.NarrowDrawer"
//
// The template id names the key, so two keys registered as Nothing are two identities and a later real picture for one of them
// remounts only that slot. The core still runs its host (the hooks, effects and state machines behind the key); only the picture
// is absent, so a host that needs a picture to work (a core pane mounted through `Mount`) must be kept from mounting by the core's
// own rule (the capabilities), never by registering Nothing.

using FluentGpu.Dsl;
using FluentGpu.Theming;

namespace Wavee.Themes.Common;

/// <summary>The view that renders nothing.</summary>
public static class CommonNothing
{
    /// <summary>The template id prefix: a key registered as Nothing is <c>common.nothing.&lt;key id&gt;</c>.</summary>
    public const string IdPrefix = "common.nothing.";

    /// <summary>The one element every Nothing slot renders: a zero box, not hit-testable, that neither grows nor shrinks.</summary>
    private static readonly BoxEl s_nothing = new() { Width = 0f, Height = 0f, Grow = 0f, Shrink = 0f, HitTestVisible = false };

    /// <summary>Registers Nothing for <paramref name="key"/> under the id <c>common.nothing.&lt;key id&gt;</c> (the key's version suffix,
    /// if any, is dropped from the id).</summary>
    public static void Register<TProps>(ViewRegistryBuilder views, ViewKey<TProps> key) where TProps : class
    {
        ArgumentNullException.ThrowIfNull(views);
        views.Add(key, Of<TProps>.Template, IdFor(key.Id));
    }

    /// <summary>The template id Nothing is registered under for the key id <paramref name="keyId"/>.</summary>
    public static string IdFor(string keyId)
    {
        ArgumentNullException.ThrowIfNull(keyId);
        int at = keyId.IndexOf('@');
        return IdPrefix + (at >= 0 ? keyId[..at] : keyId);
    }

    /// <summary>The template, one cached delegate per props type (so an equal registration compares equal).</summary>
    private static class Of<TProps> where TProps : class
    {
        public static readonly ViewTemplate<TProps> Template = static (TProps _, in ViewContext _) => s_nothing;
    }
}
