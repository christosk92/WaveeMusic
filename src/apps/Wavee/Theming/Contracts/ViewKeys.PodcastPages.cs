// ── Theming/Contracts/ViewKeys.PodcastPages.cs — the redesigned podcast show, episode and audiobook pages (podcast pages v2) ──────────────────
//
// Three whole-page keys. The core hosts (`Podcasts/PodcastPages.*.cs`, each a `[CoreHost]`) own the demand, the one snapshot (the smart action, the
// row states, the filters and their counts, the groups, the credits, the book's progress), the page's own cells (filter, search, sort, selection,
// expansion, tabs, transcript follow) and every verb; a theme draws the snapshot it is handed. A structure that draws these keys says so with
// `ShellCapabilities.PodcastPages`; under one that does not, the show and episode routes keep the older reader pages (`Show.Page`, `Episode.Page`).
//
// The concept and its rules: C:/WAVEE/scratch/sphome/winui-podcast-v2.html (design notes 1-9, "The smart action, state by state", "Three widths",
// "Audiobooks are books"). The value is the same in every theme; the layout and the visual language are each theme's own.

using FluentGpu.Theming;

namespace Wavee.Theming.Contracts;

public static partial class ViewKeys
{
    /// <summary>A podcast's page: the smart action, the filtered and grouped episode list, the details pane / About. Props: <see cref="PodcastShowPageProps"/>.</summary>
    public static readonly ViewKey<PodcastShowPageProps> PodcastShowPage = new("Pages.PodcastShow");

    /// <summary>One episode's page: the chapter map, the chapters / about / comments, the transcript that follows playback. Props: <see cref="PodcastEpisodePageProps"/>.</summary>
    public static readonly ViewKey<PodcastEpisodePageProps> PodcastEpisodePage = new("Pages.PodcastEpisode");

    /// <summary>An audiobook's page: the credits, one Start / Continue / Listen again with the whole book's progress, the book line and the contents page.
    /// Props: <see cref="AudiobookPageProps"/>.</summary>
    public static readonly ViewKey<AudiobookPageProps> AudiobookPage = new("Pages.Audiobook");
}
