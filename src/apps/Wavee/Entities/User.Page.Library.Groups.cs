// ── Entities/User.Page.Library.Groups.cs ─────────────────────────────────────────────────────────────────────────────
// The ALBUMS library's artist grouping: a structure that draws the albums page as "all artists | the picked artist's albums | the songs" asks for it with
// `LibraryTierPolicy.GroupAlbumsByArtist`. This host then (1) groups the saved albums by their FIRST billed artist (the artist the navigator's own subtitle names), (2) keeps the
// pick (`GroupKey`, persisted like the selection: `library.albums.group`) and (3) narrows the navigator's source to that artist's albums. The structure draws the column from
// `LibraryPageProps.Groups`. A structure that does not ask (Classic) sees no groups, no filter and no written key: byte-identical.
//
// A pick the library no longer holds (the artist's last album was removed) never empties the page: the filter falls back to every album and the set reads "" as the pick.

using FluentGpu.Signals;
using Wavee.Theming.Contracts;

namespace Wavee;

public readonly partial struct User
{
    sealed partial class LibraryPage
    {
        Memo<LibraryGroupSet>? _groupSet;
        readonly Func<LibraryGroupSet> _computeGroups;
        LibraryGroupsProps? _groupsProps;
        string _groupSaved = "";
        int[] _groupSrc = new int[64];

        /// <summary>The grouping is live: the albums page, the structure asked, and the page is split (the one-column drill-in has no groups column).</summary>
        bool GroupingOn => _entity == EntityKind.Album && ShellPolicy.Tracked.LibraryTiers.GroupAlbumsByArtist
                           && !(_layoutTier is { } tier && Theming.LibraryLadders.IsSingleColumn(tier.Value));

        /// <summary>The grouping is live but this content tier draws no songs pane (`LibraryTierPolicy.GroupPaneFrom`).</summary>
        bool GroupPaneHidden => GroupingOn && (_facts ?? Theming.ThemeHost.Facts).Peek().Tiers.Content < ShellPolicy.Now.LibraryTiers.GroupPaneFrom;

        LibraryGroupsProps GroupsProps() => _groupsProps ??= new LibraryGroupsProps(ReadGroups, PickGroup)
        {
            Play = () => { var a = GroupArtist(); if (a.IsValid) Playback.PlayOrToggleContext(a.Id); },
            Shuffle = () => { var a = GroupArtist(); if (!a.IsValid) return; Playback.SetShuffle(true); Playback.PlayContext(a.Id); },
            Open = () => { var a = GroupArtist(); if (a.IsValid) Shell.GoTo(Shell.For(a.Uri, a.Name)); },
        };

        Artist GroupArtist()
        {
            string key = EffectiveGroup();
            return key == AllGroups ? default : new Artist(SlotOfKey(EntityKind.Artist, key));
        }

        /// <summary>The pick that means every artist (an unset pick, "", means the first artist of the column, as the boards open on one).</summary>
        internal const string AllGroups = "*";

        void PickGroup(string key) => GroupKey.SetIfChanged(key.Length == 0 ? AllGroups : key);

        /// <summary>The pick the albums narrow to: the stored one, else the first artist of the column (a stored artist the library no longer holds reads the same).</summary>
        string EffectiveGroup()
        {
            string key = GroupKey.Value;
            if (key == AllGroups) return key;
            var rows = _groupSet?.Value.Rows;
            if (rows is null || rows.Count == 0) return AllGroups;
            for (int i = 0; i < rows.Count; i++) if (rows[i].Key == key) return key;
            return rows[0].Key;
        }

        /// <summary>Written only when a structure's pick moved it: a structure that never groups never writes the key.</summary>
        void SaveGroup()
        {
            string key = GroupKey.Peek();
            if (key == _groupSaved) return;
            _groupSaved = key;
            Platform.Settings.Set(Platform.Keys.LibraryGroup(_kind), key);
        }

        LibraryGroupSet ReadGroups()
        {
            var set = _groupSet!.Value;
            string key = EffectiveGroup();
            for (int i = 0; i < set.Rows.Count; i++)
                if (set.Rows[i].Key == key)
                {
                    int slot = SlotOfKey(EntityKind.Artist, key);
                    var artist = new Artist(slot);
                    return set with
                    {
                        Selected = key, SelectedName = set.Rows[i].Name, SelectedAlbums = set.Rows[i].Albums,
                        SelectedArt = artist.IsValid ? Controls.ArtUrl(artist.ImageId) ?? "" : "", SelectedSongs = LibrarySongCountOf(slot),
                    };
                }
            return set;
        }

        /// <summary>The artists that have a saved album, a-z, with their album counts (first billed artist). Pure of the pick: the memo moves with the library and the names only.</summary>
        LibraryGroupSet ComputeGroups()
        {
            var none = new LibraryGroupSet(Array.Empty<LibraryGroupRow>(), "", "", 0, 0) { Known = false };
            if (!GroupingOn) return none;
            _ = Entities.ScopeEpoch.Value;
            var scope = Entities.Current;
            var relation = Relation(_relation);
            _ = relation.Changed.Value;
            _ = Entities.TableFor(_entity)!.Changed.Value;
            _ = scope.Edges.AlbumArtists.Changed.Value;
            _ = scope.Artists.Changed.Value;
            if (scope.MeSlot <= Table.None) return none;
            var albums = relation.Targets(scope.MeSlot);
            var counts = new Dictionary<int, int>();
            for (int i = 0; i < albums.Length; i++)
            {
                var billed = scope.Edges.AlbumArtists.Targets(albums[i]);
                if (billed.Length == 0 || billed[0] <= Table.None) continue;
                counts[billed[0]] = counts.GetValueOrDefault(billed[0]) + 1;
            }
            var named = new List<(string Name, int Slot, int Count)>(counts.Count);
            foreach (var (slot, count) in counts)
            {
                string name = LibraryRows.TitleOf(EntityKind.Artist, slot);
                if (name.Length > 0) named.Add((name, slot, count));
            }
            named.Sort(static (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
            var rows = new LibraryGroupRow[named.Count];
            string last = "";
            for (int i = 0; i < rows.Length; i++)
            {
                string letter = char.IsLetter(named[i].Name[0]) ? char.ToLowerInvariant(named[i].Name[0]).ToString() : "#";
                rows[i] = new LibraryGroupRow(KeyOf(EntityKind.Artist, named[i].Slot), named[i].Name, named[i].Count, letter == last ? "" : letter);
                last = letter;
            }
            // Unanswered, the column's counts are not known: it draws none (audit B7).
            return new LibraryGroupSet(rows, "", "", albums.Length, albums.Length) { Known = relation.State(scope.MeSlot) != EdgeState.Unknown };
        }

        /// <summary>The albums whose first billed artist is the picked one; every album when the pick is unknown or holds none (never an empty page).</summary>
        ReadOnlySpan<int> NarrowToGroup(ReadOnlySpan<int> source, string groupKey)
        {
            int artist = SlotOfKey(EntityKind.Artist, groupKey);
            if (artist <= Table.None) return source;
            var edge = Entities.Current.Edges.AlbumArtists;
            if (_groupSrc.Length < source.Length) _groupSrc = new int[source.Length];
            int n = 0;
            for (int i = 0; i < source.Length; i++)
            {
                var billed = edge.Targets(source[i]);
                if (billed.Length > 0 && billed[0] == artist) _groupSrc[n++] = source[i];
            }
            return n == 0 ? source : _groupSrc.AsSpan(0, n);
        }
    }
}
