// ── Screens/ReleaseNotes.Fake.cs — the `--fake` release notes (themes: the What's new page renders offline) ──────────────────────────────────
//
// Under `--fake` the store never reaches GitHub: `GetAsync` answers any safe version with this notes document (the named release's own words for the six
// versions of the rail, a generic one for any other), and `RefreshIndexAsync` publishes the rail's index. The words are the demo's, written like a real
// changelog: three highlight cards, then added / changed / fixed items citing closed issues, two with a first-time contributor.
//
// A demo that has real notes of its own (a packaged build's `Assets/whatsnew`, or `--whatsnew <dir>` pointing at a release folder the release tool
// emitted) plays THAT release instead: its notes, its images, and the index that came with them (or a one-entry index), so a release can be read in
// the app before it ships.

namespace Wavee;

public static partial class ReleaseNotes
{
    static readonly (string Version, string Name, string Date, string Channel)[] s_fakeReleases =
    [
        ("1.7.0-beta.1", "borealis", "2026-10-09", "beta"),
        ("1.6.0", "aurora", "2026-09-22", "stable"),
        ("1.5.2", "", "2026-09-02", "stable"),
        ("1.5.0", "", "2026-08-12", "stable"),
        ("1.4.1", "", "2026-07-28", "stable"),
        ("1.4.0", "", "2026-07-10", "stable"),
    ];

    /// <summary>The rail's index, newest first.</summary>
    internal static ReleaseNotesIndex FakeIndex()
    {
        var entries = new ReleaseNotesIndexEntry[s_fakeReleases.Length];
        for (int i = 0; i < entries.Length; i++)
        {
            var r = s_fakeReleases[i];
            entries[i] = new ReleaseNotesIndexEntry { Version = r.Version, PackageVersion = r.Version + ".0", Name = r.Name, Date = r.Date, Channel = r.Channel, Issues = [190, 198, 201, 203, 205, 210, 214] };
        }
        return new ReleaseNotesIndex { Releases = entries };
    }

    /// <summary>The rail's index when the demo plays its own notes <paramref name="own"/>: the index emitted beside them, else that one release.</summary>
    internal static ReleaseNotesIndex FakeIndex(ReleaseNotesDocument own, ReleaseNotesIndex? emitted)
    {
        if (emitted?.Find(own.Version) is not null) return emitted;
        return new ReleaseNotesIndex
        {
            Releases = [new ReleaseNotesIndexEntry { Version = own.Version, PackageVersion = own.PackageVersion, Name = own.Name, Date = own.Date, Channel = own.Channel }],
        };
    }

    /// <summary>The release the demo runs: its own notes' version when it has them, else 1.6.0 "aurora" (the canned notes' newest stable).</summary>
    internal static RunningBuild FakeRunning() =>
        Notes.FakeOwnNotes() is { } own ? new RunningBuild(own.Version, own.Channel.Length > 0 ? own.Channel : "stable", false) : new RunningBuild("1.6.0", "stable", false);

    /// <summary>The release the demo last read: the one before its own notes in their index ("" when none), else 1.4.0.</summary>
    internal static string FakeLastSeen()
    {
        if (Notes.FakeOwnNotes() is not { } own) return "1.4.0";
        var releases = Notes.FakeOwnIndex()?.Releases ?? [];
        for (int i = 0; i + 1 < releases.Length; i++)
            if (releases[i]?.Version == own.Version) return releases[i + 1]?.Version ?? "";
        return "";
    }

    /// <summary>The notes of <paramref name="version"/>.</summary>
    internal static ReleaseNotesDocument FakeDocument(string version)
    {
        string name = "", date = "2026-09-22", channel = "stable";
        foreach (var r in s_fakeReleases)
            if (r.Version == version) { name = r.Name; date = r.Date; channel = r.Channel; }
        var doc = new ReleaseNotesDocument
        {
            Version = version, PackageVersion = version + ".0", Name = name, Tagline = "themes, the zune band and grouped liked songs", Date = date, Channel = channel,
            GeneratedAt = date + "T08:00:00Z",
            Links = new ReleaseLinks { Release = "https://github.com/christosk92/WaveeMusic/releases/tag/wavee-v" + version },
            Highlights =
            [
                new ReleaseHighlight { Id = "themes", Title = "Themes", Body = "Classic, WinUI and Zune: one app, three looks, each with its own options.", Kind = "new", DeepLink = "wavee://open?route=settings" },
                new ReleaseHighlight { Id = "band", Title = "The Zune band", Body = "Pivots across the top, your pins beside them, no sidebar.", Kind = "new", DeepLink = "wavee://open?route=home" },
                new ReleaseHighlight { Id = "liked", Title = "Liked songs, grouped", Body = "This week, last week, then the months.", Kind = "improved", DeepLink = "wavee://open?route=liked" },
            ],
            Sections =
            [
                new ReleaseSection { Kind = "added", Items =
                [
                    Item("added-0", "Themes: Classic, WinUI and Zune, each with its own options", 210, "christosk92", false),
                    Item("added-1", "Liked Songs groups by week and month when sorted by date added", 203, "christosk92", false),
                    Item("added-2", "The Zune band shows up to six pins beside the pivots", 205, "fable", true),
                ] },
                new ReleaseSection { Kind = "changed", Items =
                [
                    Item("changed-0", "The right panel sits inline from 820 wide and floats below that", 198, "christosk92", false),
                    Item("changed-1", "Track rows: the playing song is drawn in the accent, with no plate", 201, "christosk92", false),
                ] },
                new ReleaseSection { Kind = "fixed", Items =
                [
                    Item("fixed-0", "A theme switch never paints a frame in the old theme", 214, "christosk92", false),
                    Item("fixed-1", "The daylist clock no longer drifts after sleep", 190, "sonnet", true),
                ] },
            ],
        };
        doc.Normalize();
        return doc;

        static ReleaseItem Item(string id, string text, int issue, string who, bool first) => new()
        {
            Id = id, Text = text,
            Issues = [new ReleaseIssue { Repo = "christosk92/WaveeMusic", Number = issue, Title = text, State = "closed", StateReason = "completed" }],
            Contributors = [new ReleaseContributor { Login = who, FirstTime = first }],
        };
    }
}
