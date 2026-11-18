// ── Themes/WinUi/Structure/Views/Podcast/WinUiPodcastPlayLeaf.cs — the play/pause leaf of the podcast pages (show, episode, audiobook) ───────────────
//
// Play and Pause flip a button's word and glyph, nothing else on the page. A page that read the playing signal in its own render re-rendered whole on
// every press (the episode page: 37 components, 18 ms on a long episode). This leaf is the one reader: its builder runs with the current answer, and
// only the leaf re-renders when it changes.

using FluentGpu.Dsl;
using FluentGpu.Hooks;

namespace Wavee.Themes.WinUi;

/// <summary>A leaf that builds its element from the playing answer, and is the only part that re-renders when that answer changes.</summary>
internal static class WinUiPodcastPlayLeaf
{
    /// <summary><paramref name="pausing"/> reads the page's playing signal (it subscribes the leaf, not the page); <paramref name="build"/> draws the button
    /// for that answer. Keyed <paramref name="key"/> (the button's own key goes on what <paramref name="build"/> returns).</summary>
    public static Element Of(string key, Func<bool> pausing, Func<bool, Element> build)
        => Embed.Comp(new Args(pausing, build), static () => new Host()) with { Key = key };

    sealed record Args(Func<bool> Pausing, Func<bool, Element> Build);

    sealed class Host : Component
    {
        public override Element Render()
        {
            var a = UseProps<Args>();
            return a.Build(a.Pausing());
        }
    }
}
