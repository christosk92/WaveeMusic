// ── Themes/Classic/Structure/Views/Pages/ClassicLibrarySavedEpisodes.cs — Classic's `Library.SavedEpisodes` (themes plan 6.1, T10c1) ─────────────
//
// From `Entities\User.SavedEpisodes.cs` (`SavedEpisodesReader.Render`): Your Episodes, over the ONE track table (B2 plan §3.2/§3.5). The listen-later list is a real Spotify playlist, so once the
// host has discovered it the episodes are just the table's rows — the same selection bar, menu and row template as any other episode-carrying table, no bespoke list here. The states this view
// draws are the ones ABOVE the table: has the listen-later playlist even been discovered yet (Pending, with a cold seed of the structure's episode rows), is it empty (the quiet line the host
// hands in as content), or did discovery fail (the quiet failure line with its Retry, the host's). Ready hands the body to the table, which then owns its OWN membership loading.
//
// The cold skeleton's rows are the structure's episode row seed (`StyleKeys.EpisodeRow`, T8c): the same rows the live table draws, at neutral facts, so the page does not jump when the table lands.
// The reveal is a plain fade (`SkelReveal.FadeOnly`): the table brings its own row entrance.

using FluentGpu.Dsl;
using FluentGpu.Hooks;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's Your Episodes page.</summary>
internal static class ClassicLibrarySavedEpisodes
{
    /// <summary>The registered template of <see cref="ViewKeys.LibrarySavedEpisodes"/>.</summary>
    public static Element Template(LibrarySavedEpisodesProps props, in ViewContext ctx)
    {
        // The structure's episode row (T8c): the cold skeleton's rows are its seed.
        var episodeStyle = ListStyleLookup.Of(ctx.Hooks.UseContext(ViewRegistry.Context), StyleKeys.EpisodeRow);
        return new SkelRegionEl(
            Pending: () => props.Pending,
            Failed: () => props.Failed,
            Content: props.Content,
            ShimmerSource: () => new BoxEl { Direction = 1, Children = SeedRows(episodeStyle) },
            OnFailed: props.OnFailed,
            Reveal: SkelReveal.FadeOnly, Style: SkeletonStyle.Default, Group: null);
    }

    static Element[] SeedRows(EpisodeRowStyle? style)
    {
        var rows = new Element[ClassicLibraryRules.SavedSeedRows];
        for (int i = 0; i < rows.Length; i++) rows[i] = Episode.SeedReaderRow(style, false);
        return rows;
    }
}
