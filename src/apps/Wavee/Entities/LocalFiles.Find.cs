// ── Entities/LocalFiles.Find.cs ─────────────────────────────────────────────────────────────────────────────────────
// "Find music on this PC" (#240, 0dm): the card the empty Local Files / On this PC state and Settings' empty Music folders group show in
// place of "Add my Music folder": the user's Music and Downloads folders with what the peek counted in them, a tick each, "Add selected"
//
// Role: SHELL (UI thread)
// Spec: C:\WAVEE\fgpu\handoff\240-0dm-suggestions.md §3 F2; owner defaults 2026-11-25 (Downloads unticked, the Music folder only)
//
// WHEN. While no music folder is set (`Onboarding`). The card's host starts the peek when it mounts (`LocalLibrary.StartPeek`: once per
// session, counts only, never a read) and re-words its rows as each folder's count lands. Nothing is added until "Add selected" (D3).
//
// THE ROWS. Music starts ticked, Downloads unticked (its count shows, so the user decides with the number in front of them). A folder with
// no song is greyed, says "No songs here" and cannot be ticked. While a folder is counted its line says "Counting…": one line either way,
// so the card does not move when the counts land. The ticks are the session's, shared by the page and Settings.

using System.Globalization;
using FluentGpu.Dsl;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;
using ViewSlots = FluentGpu.Theming.Slots;
using F = Wavee.Strings.LocalFiles.Find;

namespace Wavee;

public static partial class LocalFilesList
{
    static readonly Signal<bool> s_tickMusic = new(true), s_tickDownloads = new(false);

    /// <summary>The card (its host: it starts the peek on mount and follows it). <paramref name="choose"/>: offer "Choose another folder…" (the
    /// empty page); Settings' group passes false (its header has "Add a folder…": one home per verb).</summary>
    public static Element FindCard(bool choose = true)
        => Embed.Comp(new FindProps(choose), static () => new FindHost()) with { Key = choose ? "local-find" : "local-find:settings" };

    sealed record FindProps(bool Choose);   // false: Settings' group (unframed, no "Choose another folder…")

    sealed class FindHost : Component
    {
        public override Element Render()
        {
            bool choose = UseProps<FindProps>().Choose;
            UseEffect(static () => LocalLibrary.StartPeek(), DepKey.From(0));
            LocalPeekRow[] peek = LocalLibrary.Peek.Value;
            var rows = new LocalFindRow[peek.Length];
            bool canAdd = false;
            for (int i = 0; i < peek.Length; i++)
            {
                LocalPeekRow p = peek[i];
                Signal<bool> tick = p.Key == "music" ? s_tickMusic : s_tickDownloads;
                bool empty = p.Done && p.Songs == 0;
                bool ticked = tick.Value && !empty;
                canAdd |= ticked;
                rows[i] = new LocalFindRow
                {
                    Key = p.Key, Name = Loc.Get(p.Key == "music" ? F.Music : F.Downloads), Path = p.Path, Ticked = tick,
                    Count = CountOf(p), Empty = empty, Counting = !p.Done,
                };
            }
            return ViewSlots.View(ViewKeys.LocalFilesFind, new LocalFindProps
            {
                Title = Loc.Get(F.Title), Line = Loc.Get(F.Line), Rows = rows,
                AddLabel = Loc.Get(F.Add), Add = s_addSelected, CanAdd = canAdd,
                ChooseLabel = choose ? Loc.Get(F.Choose) : "", Choose = choose ? s_choose : null, Framed = choose,
            });
        }
    }

    /// <summary>A folder's line: "1,204 songs (238 in OneDrive)", "37 songs · 12 videos", "1,204+ songs" (the walk stopped at its cap),
    /// "No songs here", or "Counting…".</summary>
    static string CountOf(LocalPeekRow p)
    {
        if (!p.Done) return Loc.Get(F.Counting);
        if (p.Songs == 0) return Loc.Get(F.None);
        string songs = p.Capped ? F.SongsMore(Count(p.Songs)) : F.Songs(p.Songs, Count(p.Songs));
        if (p.Cloud > 0) songs = F.InCloud(songs, Count(p.Cloud));
        return p.Videos > 0 ? F.WithVideos(songs, F.Videos(p.Videos, Count(p.Videos))) : songs;

        static string Count(int n) => n.ToString("N0", CultureInfo.CurrentCulture);
    }

    /// <summary>"Add selected": each ticked folder that holds songs (or is still counted) becomes a music folder, as "Add a folder…" adds one.</summary>
    static readonly Action s_addSelected = static () =>
    {
        foreach (LocalPeekRow p in LocalLibrary.Peek.Peek())
        {
            bool ticked = (p.Key == "music" ? s_tickMusic : s_tickDownloads).Peek();
            if (ticked && !(p.Done && p.Songs == 0)) SettingsVerbs.AddMusicFolderAt(p.Path);
        }
    };
}
