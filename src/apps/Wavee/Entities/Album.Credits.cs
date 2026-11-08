// ── Entities/Album.Credits.cs — the album's credits: folded from its tracks' CreditsV2 roles (album credits, contract 1.84) ──────────────────
//
// Role: CORE (the fold, the demand's filter, the "all credits" sheet host)
//
// THE SOURCE. The client models no album-level credits trait; what it has is the CreditsV2 trait (extension kind 186) per TRACK, the answer the
// drawer's credits sheet and the stage's "this song" already read (`Edges.TrackCredits`, decoded by `Spotify.Decode.Credits`). Its route is a
// Metadata route (`FetchRoutes.ForEdge(TrackCredits)`), and the provider sends a whole edge batch as ONE `BatchedEntityRequest` (`AnswerEdge` ->
// `MetadataFor`), so asking every member of an album is one POST. The album's credits are therefore the fold of its members' credits, asked once per
// album by the trailing band's demand, and only under a structure that states them (`ShellCapabilities.AlbumCredits`: Zune's Z-Album "credits").
//
// THE FOLD (`PageRules.Credits`). One person per artist page (else per name, case-insensitive), counted per TRACK in three roles read off the role's
// words, the group heading only where the role says nothing (`RoleOf`): writer, producer, performer. The paragraph names
//   · the album's principal writers and producers: whoever wrote or produced at least half of the tracks that carry credits, "written and produced
//     by" a person in both, else "written by" / "produced by", at most three a clause, most tracks first;
//   · "with": the performers the album does not bill (the featured voices and players), not named already, at most four, most tracks first;
//   · "all credits" (the theme's link): the sheet of every credit of every track, grouped as the track's sheet groups them, each person once a group
//     with their roles and how many songs ("all songs", "3 songs", or the one song's title).
// Pending while the tracklist or any member's credits are unanswered; null when there is nothing to state (a theme draws no section at all).

using System.Globalization;
using FluentGpu.Dsl;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using Wavee.Theming.Contracts;
using ViewSlots = FluentGpu.Theming.Slots;

namespace Wavee;

public readonly partial struct Album
{
    public static partial class PageRules
    {
        /// <summary>The most names a "written / produced by" clause states, and the most "with" names (the sheet holds the rest).</summary>
        public const int CreditLeadCap = 3, CreditWithCap = 4;

        /// <summary>What a credit row says its person did on the track.</summary>
        [Flags]
        public enum CreditRole : byte { None = 0, Writer = 1, Producer = 2, Performer = 4 }

        /// <summary>The credits verdict of an album: still asking (<see cref="Pending"/>), or the paragraph (null: nothing to state).</summary>
        public readonly record struct CreditsVerdict(bool Pending, AlbumCredits? Credits);

        /// <summary>A row's role from its words: the role's own ("Composer", "Main Artist", "Producer"); a role that names none of the three
        /// (an instrument, "Mixing Engineer") counts as a performer only under a performers heading; an empty role reads the heading.</summary>
        public static CreditRole RoleOf(string? role, string? group)
        {
            if (!string.IsNullOrWhiteSpace(role))
            {
                var said = Words(role);
                return said != CreditRole.None ? said : Has(group, "perform") ? CreditRole.Performer : CreditRole.None;
            }
            return Words(group);

            static CreditRole Words(string? s)
            {
                var k = CreditRole.None;
                if (Has(s, "writ") || Has(s, "compos") || Has(s, "lyric") || Has(s, "author")) k |= CreditRole.Writer;
                if (Has(s, "produc")) k |= CreditRole.Producer;
                if (Has(s, "artist") || Has(s, "perform") || Has(s, "vocal") || Has(s, "featur")) k |= CreditRole.Performer;
                return k;
            }

            static bool Has(string? s, string word) => s is not null && s.Contains(word, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Is a member's credits answer still out (the tracklist unanswered counts too)? Reads the tables; allocates nothing.</summary>
        public static bool CreditsPending(Album a)
        {
            if (!a.IsValid) return false;
            var e = Entities.Current.Edges;
            if (e.AlbumTracks.Readiness(a.Slot) == EdgeState.Unknown) return true;
            var members = a.TrackSlots;
            for (int i = 0; i < members.Length; i++)
                if (e.TrackCredits.Readiness(members[i]) == EdgeState.Unknown) return true;
            return false;
        }

        /// <summary>The members whose credits nobody has answered (into <paramref name="into"/>, the count returned): what the demand asks.</summary>
        public static int UnaskedCredits(ReadOnlySpan<int> members, Span<int> into)
        {
            var credits = Entities.Current.Edges.TrackCredits;
            int n = 0;
            for (int i = 0; i < members.Length && n < into.Length; i++)
                if (members[i] > Table.None && credits.State(members[i]) == EdgeState.Unknown) into[n++] = members[i];
            return n;
        }

        /// <summary>The album's credits paragraph (see the file header), or pending, or null. COLD: allocates the people and the runs, so a host calls
        /// it only when its stamp moved.</summary>
        public static CreditsVerdict Credits(Album a, Action? allCredits)
        {
            if (!a.IsValid) return default;
            if (CreditsPending(a)) return new CreditsVerdict(true, null);
            var e = Entities.Current.Edges;
            var members = a.TrackSlots;
            var people = new List<Person>();
            int credited = 0;
            for (int t = 0; t < members.Length; t++)
            {
                var rows = e.TrackCredits.Payload(members[t]);
                if (rows.Length == 0) continue;
                var targets = e.TrackCredits.Targets(members[t]);
                credited++;
                for (int i = 0; i < rows.Length; i++)
                {
                    var role = RoleOf(Entities.Strings.Resolve(rows[i].Role), Entities.Strings.Resolve(rows[i].Group));
                    string name = Entities.Strings.Resolve(rows[i].Name);
                    if (role == CreditRole.None || name.Length == 0) continue;
                    var p = Person.Of(people, name, i < targets.Length ? targets[i] : Table.None);
                    if ((role & CreditRole.Writer) != 0 && p.WroteAt != t) { p.WroteAt = t; p.Wrote++; }
                    if ((role & CreditRole.Producer) != 0 && p.ProducedAt != t) { p.ProducedAt = t; p.Produced++; }
                    if ((role & CreditRole.Performer) != 0 && p.PerformedAt != t) { p.PerformedAt = t; p.Performed++; }
                }
            }
            if (people.Count == 0) return default;

            var both = new List<Person>();
            var wrote = new List<Person>();
            var produced = new List<Person>();
            foreach (var p in people)
            {
                bool w = Principal(p.Wrote, credited), pr = Principal(p.Produced, credited);
                if (w && pr) both.Add(p);
                else if (w) wrote.Add(p);
                else if (pr) produced.Add(p);
            }
            both.Sort(static (x, y) => Order(x.Wrote + x.Produced, y.Wrote + y.Produced, x, y));
            wrote.Sort(static (x, y) => Order(x.Wrote, y.Wrote, x, y));
            produced.Sort(static (x, y) => Order(x.Produced, y.Produced, x, y));

            // "with": who performs on the record and is neither billed on it nor named above.
            var billed = a.ArtistSlots;
            var with = new List<Person>();
            foreach (var p in people)
            {
                if (p.Performed == 0 || Named(p, both, CreditLeadCap) || Named(p, wrote, CreditLeadCap) || Named(p, produced, CreditLeadCap)) continue;
                if (IsBilled(p, billed)) continue;
                with.Add(p);
            }
            with.Sort(static (x, y) => Order(x.Performed, y.Performed, x, y));

            var runs = new List<AlbumCreditRun>(16);
            Clause(runs, Loc.Get(Strings.Detail.CreditsLine.WrittenAndProducedBy), both, CreditLeadCap, and: true);
            Clause(runs, Loc.Get(Strings.Detail.CreditsLine.WrittenBy), wrote, CreditLeadCap, and: true);
            Clause(runs, Loc.Get(Strings.Detail.CreditsLine.ProducedBy), produced, CreditLeadCap, and: true);
            Clause(runs, Loc.Get(Strings.Detail.CreditsLine.With), with, CreditWithCap, and: false);
            return new CreditsVerdict(false, runs.Count > 0 ? new AlbumCredits(runs, allCredits) : null);
        }

        /// <summary>A principal: credited on at least half of the tracks that carry credits.</summary>
        static bool Principal(int tracks, int credited) => tracks > 0 && 2 * tracks >= credited;

        /// <summary>Most tracks first, then first appearance.</summary>
        static int Order(int x, int y, Person a, Person b) => x != y ? y.CompareTo(x) : a.First.CompareTo(b.First);

        static bool Named(Person p, List<Person> clause, int cap)
        {
            for (int i = 0; i < clause.Count && i < cap; i++) if (ReferenceEquals(clause[i], p)) return true;
            return false;
        }

        static bool IsBilled(Person p, ReadOnlySpan<int> billed)
        {
            for (int i = 0; i < billed.Length; i++)
            {
                if (p.Artist > Table.None && billed[i] == p.Artist) return true;
                var artist = new Artist(billed[i]);
                if (artist.IsValid && artist.Knows(ArtistFields.Name) && string.Equals(artist.Name, p.Name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>"written by A, B and C" ("with A, B, C": <paramref name="and"/> false), after a " · " when a clause stands before it.</summary>
        static void Clause(List<AlbumCreditRun> runs, string words, List<Person> people, int cap, bool and)
        {
            if (people.Count == 0) return;
            if (runs.Count > 0) runs.Add(new AlbumCreditRun(" · "));
            runs.Add(new AlbumCreditRun(words + " "));
            int n = Math.Min(cap, people.Count);
            for (int i = 0; i < n; i++)
            {
                if (i > 0) runs.Add(new AlbumCreditRun(and && i == n - 1 ? " " + Loc.Get(Strings.Detail.CreditsLine.And) + " " : ", "));
                var p = people[i];
                runs.Add(new AlbumCreditRun(p.Name, Name: true, Open: p.Artist > Table.None ? OpenArtist(p.Artist) : null));
            }
        }

        static Action OpenArtist(int slot) => () => Track.GoToArtist(new Artist(slot));

        /// <summary>One person of the fold: matched by artist page, else by name (case-insensitive); counts are TRACKS, each counted once.</summary>
        sealed class Person
        {
            public string Name = "";
            public int Artist, First, Wrote, Produced, Performed, WroteAt = -1, ProducedAt = -1, PerformedAt = -1;

            public static Person Of(List<Person> people, string name, int artist)
            {
                foreach (var p in people)
                {
                    if ((artist > Table.None && p.Artist == artist) || string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        if (p.Artist <= Table.None && artist > Table.None) p.Artist = artist;   // a linked row names the page an unlinked one lacked
                        return p;
                    }
                }
                var added = new Person { Name = name, Artist = artist, First = people.Count };
                people.Add(added);
                return added;
            }
        }

        /// <summary>The "all credits" sheet's rows: every credit of every member, grouped as a track's sheet groups them (the server's heading, else the
        /// role; groups in first-appearance order), each person once a group with their roles and their songs ("all songs", "3 songs", the song's title).</summary>
        public static (Track.DrawerRules.CreditsView State, CreditsEntry[] Entries) CreditEntries(Album a)
        {
            if (!a.IsValid) return (Track.DrawerRules.CreditsView.Empty, []);
            if (CreditsPending(a)) return (Track.DrawerRules.CreditsView.Loading, []);
            var e = Entities.Current.Edges;
            var members = a.TrackSlots;
            var groups = new List<(string Key, List<SheetPerson> People)>();
            for (int t = 0; t < members.Length; t++)
            {
                var rows = e.TrackCredits.Payload(members[t]);
                var targets = e.TrackCredits.Targets(members[t]);
                for (int i = 0; i < rows.Length; i++)
                {
                    string name = Entities.Strings.Resolve(rows[i].Name), role = Entities.Strings.Resolve(rows[i].Role);
                    if (name.Length == 0) continue;
                    string key = Track.DrawerRules.CreditGroupKey(Entities.Strings.Resolve(rows[i].Group), role);
                    int g = groups.FindIndex(x => string.Equals(x.Key, key, StringComparison.Ordinal));
                    if (g < 0) { groups.Add((key, new List<SheetPerson>())); g = groups.Count - 1; }
                    var p = SheetPerson.Of(groups[g].People, name, i < targets.Length ? targets[i] : Table.None);
                    if (role.Length > 0 && !p.Roles.Exists(r => string.Equals(r, role, StringComparison.OrdinalIgnoreCase))) p.Roles.Add(role);
                    if (p.LastTrack != t) { p.LastTrack = t; p.Songs++; }
                }
            }
            var entries = new List<CreditsEntry>();
            foreach (var (key, people) in groups)
                for (int k = 0; k < people.Count; k++)
                {
                    var p = people[k];
                    string songs = p.Songs >= members.Length && members.Length > 1 ? Loc.Get(Strings.Detail.CreditsLine.AllSongs)
                        : p.Songs == 1 && new Track(members[p.LastTrack]) is { IsValid: true } one && one.Knows(TrackFields.Title) ? one.Title
                        : Strings.Detail.SongCount(p.Songs);
                    string roles = string.Join(", ", p.Roles);
                    entries.Add(new CreditsEntry(k == 0 && key.Length > 0 ? key : null, p.Name, roles.Length > 0 ? roles + " · " + songs : songs, p.Artist));
                }
            return (entries.Count > 0 ? Track.DrawerRules.CreditsView.List : Track.DrawerRules.CreditsView.Empty, entries.ToArray());
        }

        sealed class SheetPerson
        {
            public string Name = "";
            public int Artist, Songs, LastTrack = -1;
            public readonly List<string> Roles = new(2);

            public static SheetPerson Of(List<SheetPerson> people, string name, int artist)
            {
                foreach (var p in people)
                    if ((artist > Table.None && p.Artist == artist) || string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        if (p.Artist <= Table.None && artist > Table.None) p.Artist = artist;
                        return p;
                    }
                var added = new SheetPerson { Name = name, Artist = artist };
                people.Add(added);
                return added;
            }
        }
    }

    // ── "all credits": the album's sheet (the track's `Dialog.Sheet`, titled by the album) ────────────────────────────────────────────────────────

    /// <summary>Open every credit of the album in the credits sheet (the track's view, the album's rows); nothing without an overlay.</summary>
    public static void OpenCredits(Album a)
    {
        var overlay = Track.DrawerOverlay;
        if (Controls.IsNullOverlay(overlay) || !a.IsValid) return;
        Controls.Sheet(overlay, a.Knows(AlbumFields.Title) ? a.Title : "",
            Embed.Comp(new CreditsSheetArgs(a.Slot), static () => new CreditsSheetHost()));
    }

    sealed record CreditsSheetArgs(int AlbumSlot);

    [CoreHost]
    sealed class CreditsSheetHost : Component
    {
        public override Element Render()
        {
            var p = UseProps<CreditsSheetArgs>();
            _ = Entities.ScopeEpoch.Value;
            var scope = Entities.Current;
            _ = scope.Edges.TrackCredits.Changed.Value;
            _ = scope.Edges.AlbumTracks.Changed.Value;
            _ = scope.Tracks.Changed.Value;                    // a one-song credit names the song
            var (state, entries) = PageRules.CreditEntries(new Album(p.AlbumSlot));
            return ViewSlots.View(ViewKeys.DialogSheet, new CreditsSheetProps
            {
                State = state,
                Entries = entries,
                OpenArtist = static slot => Track.GoToArtist(new Artist(slot)),
            });
        }
    }
}
