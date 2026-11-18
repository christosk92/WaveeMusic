namespace Wavee;

/// <summary>Offline reader content for the existing --fake smoke-test mode.</summary>
internal static class PodcastReaderFixtures
{
    internal static readonly Spotify.Podcasts.Page<Spotify.Podcasts.Chapter> Chapters = new(
        [new("fake:chapter:opening", "Opening", 0, 60000), new("fake:chapter:conversation", "The conversation", 60000, 0)], "", 2, "", 200);
    internal static readonly Spotify.Podcasts.Transcript Transcript = new("en",
        [new(0, "Opening", true), new(0, "Welcome to this episode.", false),
         new(15000, "Today we explore the ideas behind the story.", false),
         new(60000, "The conversation", true), new(60000, "Let us start with the first question.", false)], 200);
    internal static readonly Spotify.Podcasts.Page<Spotify.Podcasts.Comment> Comments = new(
        [new("fake:comment:reader", "A thoughtful conversation. Thank you for sharing it.", "Listener", "", "2026-09-19T10:00:00Z",
            false, false, true, 0, 0, "", true)], "", 1, "", 200);

    // ── the podcast pages v2 fixtures: twelve chapters drawn to the episode's own length, and a transcript that runs the whole episode with the chapter
    // titles as its headings (the concept's "The long answer" episode, scaled). Every fake episode the catalogue holds gets them, so the episode page's map,
    // chapter list and transcript render in each state (in progress sh0e5, played sh0e6, not started sh1e0). ──

    /// <summary>The concept's twelve chapters: where each starts as a share of a 15 000-second episode, and its title.</summary>
    static readonly (int Start, string Title)[] s_chapters =
    [
        (0, "Cold open: the name you just forgot"), (370, "What a memory physically is"), (1900, "Encoding: why attention is the gate"),
        (3485, "Sleep and consolidation"), (5070, "The forgetting curve, revisited"), (6720, "False memories and confident mistakes"),
        (9500, "Why you forget names"), (10950, "Memory palaces: do they work?"), (12045, "Ageing, and what actually declines"),
        (13090, "What to do on Monday"), (13920, "Listener objections"), (14490, "Credits and sources"),
    ];
    const int ConceptLength = 15000;

    /// <summary>The spoken lines the transcript cycles through (the concept's two excerpts).</summary>
    static readonly string[] s_lines =
    [
        "Quick test before we start. A minute ago our producer told you her name. What was it?",
        "I have no idea. And I study this for a living.",
        "That's the episode. Today's question is how memory actually works, and why it fails in such predictable ways.",
        "My guest has spent twenty years running memory experiments on people who were sure they remembered.",
        "Mostly on people who were sure. That's the interesting group.",
        "We'll go in order: what a memory physically is, how it gets in, how it gets lost, and what you can do about it by Monday.",
        "It's a long one. The chapters are there if you want to skip to your own problem.",
        "Let's do the one everybody asked about. Names.",
        "The most common memory complaint there is, and the least mysterious.",
        "So when I meet someone and their name is gone ten seconds later, that isn't a storage problem?",
        "Almost never. The name didn't get in. You were busy planning what to say next, so there was nothing to store.",
        "Names are also arbitrary. Baker the job comes with ovens, flour and early mornings. Baker the surname comes with nothing.",
        "That's the Baker-baker paradox.",
        "Right. Same sound, very different number of hooks.",
        "So the fix isn't a better memory. It's two seconds of attention at the moment you hear the name.",
        "Say it back, attach it to something.",
        "Say it back, and make the hook yourself. It feels awkward, and it works.",
        "What about people who say they're just bad with names?",
        "They're usually fine with faces, jobs and stories. That tells you the system works. The input is the part that failed.",
        "Let's test that. I'm going to give you five names, and we'll come back to them at the end of the chapter.",
        "Now I'm nervous.",
        "Good. Nervous people encode better. Is that true?",
        "Up to a point. A little arousal helps. A lot of it narrows what you take in.",
    ];
    /// <summary>One spoken line every this many ms.</summary>
    const int LineEveryMs = 12_000;

    /// <summary>The episode's length from the fake catalogue (0 when it holds no such row).</summary>
    static int DurationOf(string episodeUri)
        => EntityId.TryParse(episodeUri, out var id) && Entities.Current.Episodes.TryGetSlot(id, out int slot) ? new Episode(slot).DurationMs : 0;

    /// <summary>A LONG episode (3 h and up, fake sh0e0) carries a realistic maximum: 100 chapters and a line every <see cref="LongLineEveryMs"/>
    /// (about 2,500 lines over 3 h): the episode page's long lists are measured on it.</summary>
    const int LongFromMs = 2 * 3_600_000, LongChapters = 100, LongLineEveryMs = 4_300;

    /// <summary>The chapters: twelve scaled to the episode's length (the concept's), a hundred on a long one; the two-chapter fixture when the length is
    /// unknown.</summary>
    internal static Spotify.Podcasts.Page<Spotify.Podcasts.Chapter> ChaptersFor(string episodeUri)
    {
        int duration = DurationOf(episodeUri);
        if (duration <= 0) return Chapters;
        var items = ChapterList(duration);
        return new(items, "", items.Length, "", 200);
    }

    static Spotify.Podcasts.Chapter[] ChapterList(int duration)
    {
        if (duration < LongFromMs)
        {
            var items = new Spotify.Podcasts.Chapter[s_chapters.Length];
            for (int i = 0; i < items.Length; i++)
            {
                int start = Scale(s_chapters[i].Start, duration);
                int end = i + 1 < items.Length ? Scale(s_chapters[i + 1].Start, duration) : duration;
                items[i] = new("fake:chapter:" + i, s_chapters[i].Title, start, end);
            }
            return items;
        }
        // A hundred chapters of uneven length (a pure function of the index): the concept's titles, then numbered parts of them.
        var starts = new long[LongChapters + 1];
        long sum = 0;
        for (int i = 0; i < LongChapters; i++) { starts[i] = sum; sum += 7 + (i * 37 % 11); }
        starts[LongChapters] = sum;
        var list = new Spotify.Podcasts.Chapter[LongChapters];
        for (int i = 0; i < LongChapters; i++)
        {
            int start = (int)(starts[i] * duration / sum), end = (int)(starts[i + 1] * duration / sum);
            string title = s_chapters[i % s_chapters.Length].Title;
            if (i >= s_chapters.Length) title += ", part " + (i / s_chapters.Length + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            list[i] = new("fake:chapter:" + i, title, start, end);
        }
        return list;
    }

    /// <summary>A transcript over the whole episode: a line every <see cref="LineEveryMs"/> (<see cref="LongLineEveryMs"/> on a long one), each
    /// chapter's title a heading where it starts.</summary>
    internal static Spotify.Podcasts.Transcript TranscriptFor(string episodeUri)
    {
        int duration = DurationOf(episodeUri);
        if (duration <= 0) return Transcript;
        var chapters = ChapterList(duration);
        int every = duration < LongFromMs ? LineEveryMs : LongLineEveryMs;
        var lines = new List<Spotify.Podcasts.TranscriptLine>(duration / every + 2 * chapters.Length);
        int chapter = 0, spoken = 0;
        for (int at = 0; at < duration; at += every)
        {
            while (chapter < chapters.Length && chapters[chapter].StartMs <= at)
            {
                int start = chapters[chapter].StartMs;
                lines.Add(new(start, chapters[chapter].Title, true));
                if (start < at) lines.Add(new(start, s_lines[spoken++ % s_lines.Length], false));
                chapter++;
            }
            lines.Add(new(at, s_lines[spoken++ % s_lines.Length], false));
        }
        return new("en", lines.ToArray(), 200);
    }

    static int Scale(int conceptSeconds, int duration) => (int)((long)conceptSeconds * duration / ConceptLength);

    /// <summary>"More like this" offline: the other seeded podcasts (sh0-sh6 but this one), as the recommendation wire shapes them.</summary>
    internal static Spotify.Podcasts.Page<Spotify.Podcasts.Recommendation> SimilarShows(string showUri)
    {
        var items = new List<Spotify.Podcasts.Recommendation>(6);
        for (int i = 0; i < 7 && items.Count < 6; i++)
        {
            string uri = "spotify:show:sh" + i;
            if (uri == showUri || !EntityId.TryParse(uri, out var id) || !Entities.Current.Shows.TryGetSlot(id, out int slot)) continue;
            var show = new Show(slot);
            items.Add(new(uri, show.Title, Controls.ArtUrl(show.ImageId) ?? "", Entities.Strings.Resolve(show.PublisherId)));
        }
        return new(items.ToArray(), "", items.Count, "", 200);
    }

    /// <summary>The fixture-answer rule below over a fixture built for the episode (only when the catalogue holds it).</summary>
    internal static Task<T> Answer<T>(string episodeUri, Func<string, T> fixture, CancellationToken ct)
        => Holds(episodeUri) ? Task.FromResult(fixture(episodeUri)) : Never<T>(ct);

    /// <summary>The reader fixture for an episode the fake catalogue HOLDS (its row knows its title); for one it does not (the pending probe
    /// `epzzqx`, whose own read never answers) the ask never answers either, so the pending page stays on its skeleton instead of drawing the
    /// fixture's chapters, transcript and comments under a loading head (audit B7, A2's finding).</summary>
    internal static Task<T> Answer<T>(string episodeUri, T fixture, CancellationToken ct)
        => Holds(episodeUri) ? Task.FromResult(fixture) : Never<T>(ct);

    static bool Holds(string episodeUri)
    {
        if (!EntityId.TryParse(episodeUri, out var id)) return false;
        return Entities.Current.Episodes.TryGetSlot(id, out int slot) && new Episode(slot).Knows(EpisodeFields.Title);
    }

    static async Task<T> Never<T>(CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
        throw new OperationCanceledException(ct);
    }
}
