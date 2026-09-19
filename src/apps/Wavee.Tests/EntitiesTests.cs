// ── Wavee.Tests/EntitiesTests.cs — identity, base62, the uri parse, the interner seam, the P15 sweep, staging ─────
//
// Wave 1's gate for Entities/Entities.cs (plan §5, §4.15). Pure functions over columns: no engine loop (D17), no
// mocks, no I/O, and — deliberately — no `Entities.Boot` and no `Scope`. Boot is a composition-root call that attaches
// the sqlite store through its partial hook, so a unit test that called it would open a database; the mechanism this
// file pins needs neither. Table/edge behaviour lives in TableTests / EdgesTests.
//
// The identity half of this file is the gate for the 2026-09-12 change (docs/plans/wavee/wavee-0.3-entity-identity-
// memory.md, option 2): a 24-byte `EntityId` that carries a 128-bit gid for the six catalog kinds and an interned
// `StringId` for everything else. Three facts here are not decoration:
//   · a 22-char base62 string whose value exceeds 2^128 is REFUSED, not truncated — 0.2.9's decoder truncated, which
//     would alias `spotify:track:7N42dgm5tFLK9N8MT7fHC8` onto the all-zeros gid;
//   · `spotify:user:<u>:playlist:<gid>` and `spotify:playlist:<gid>` are ONE identity (defect 4);
//   · a gid-form id interns NO text at all, which is what lets a trim ever reclaim anything (defect 1).

using System.Text;
using FluentGpu.Foundation;
using Wavee;
using Xunit;

namespace Wavee.Tests;

/// <summary>Everything here writes the process-wide <c>Entities</c> statics (the interner, the publication counter,
/// the dirty list), so the three Wave-1 core test classes share one collection and never run concurrently.</summary>
[CollectionDefinition(EntitiesCollection.Name, DisableParallelization = true)]
public sealed class EntitiesCollection
{
    public const string Name = "entities-core";
}

[Collection(EntitiesCollection.Name)]
public class EntitiesTests
{
    /// <summary>A real-shaped gid: 22 base62 characters, distinct per <paramref name="seed"/>.</summary>
    static string GidText(int seed)
    {
        Span<char> buf = stackalloc char[Base62.GidChars];
        Base62.Encode(new UInt128((ulong)seed * 0x9E37_79B9_7F4A_7C15UL, (ulong)seed * 0xC2B2_AE3D_27D4_EB4FUL + 7), buf);
        return new string(buf);
    }

    // ── the uri parse ───────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    // spotify: the six fetchable kinds plus the routing-only ones
    [InlineData("spotify:track:4uLU6hMCjMI75M1A2tKUQC", EntityKind.Track, EntityProvider.Spotify)]
    [InlineData("spotify:episode:512ojhOuo1ktJprKbVcKyQ", EntityKind.Episode, EntityProvider.Spotify)]
    [InlineData("spotify:album:2noRn2Aes5aoNVsU6iWThc", EntityKind.Album, EntityProvider.Spotify)]
    [InlineData("spotify:artist:0OdUWJ0sBjDrqHygGUXeCF", EntityKind.Artist, EntityProvider.Spotify)]
    [InlineData("spotify:playlist:37i9dQZF1DXcBWIGoYBM5M", EntityKind.Playlist, EntityProvider.Spotify)]
    [InlineData("spotify:show:5CfCWKI5pZ28U0uOzXkDHe", EntityKind.Show, EntityProvider.Spotify)]
    [InlineData("spotify:user:christos", EntityKind.User, EntityProvider.Spotify)]
    [InlineData("spotify:user", EntityKind.User, EntityProvider.Spotify)]
    [InlineData("spotify:collection:tracks", EntityKind.Collection, EntityProvider.Spotify)]
    [InlineData("spotify:collection:albums", EntityKind.Collection, EntityProvider.Spotify)]
    [InlineData("spotify:concert:3ab7ff", EntityKind.Concert, EntityProvider.Spotify)]
    // the user-namespaced forms 0.2.9 answered Unknown for until the one parser landed
    [InlineData("spotify:user:christos:playlist:37i9dQ", EntityKind.Playlist, EntityProvider.Spotify)]
    [InlineData("spotify:user:christos:collection", EntityKind.Collection, EntityProvider.Spotify)]
    [InlineData("spotify:user:christos:collection:tracks", EntityKind.Collection, EntityProvider.Spotify)]
    // local, module, synthetic podcast, session playlists
    [InlineData("wavee:local:file:QzpcbXVzaWNcYS5tcDM", EntityKind.Track, EntityProvider.Local)]
    [InlineData("local:track:abc", EntityKind.Track, EntityProvider.Local)]
    [InlineData("wavee:module:radio:aHR0cDovL3g", EntityKind.Track, EntityProvider.Module)]
    [InlineData("wavee:playlist:session-1", EntityKind.Playlist, EntityProvider.UserPlaylist)]
    [InlineData("wavee:show:demo", EntityKind.Show, EntityProvider.WaveePodcast)]
    [InlineData("wavee:episode:demo", EntityKind.Episode, EntityProvider.WaveePodcast)]
    // the demo catalog
    [InlineData("fake:album:7", EntityKind.Album, EntityProvider.Fake)]
    [InlineData("tr7", EntityKind.Track, EntityProvider.Fake)]
    [InlineData("al12", EntityKind.Album, EntityProvider.Fake)]
    // never a guess
    [InlineData("wavee:skeleton:home", EntityKind.Unknown, EntityProvider.None)]
    [InlineData("wavee:media:x", EntityKind.Unknown, EntityProvider.None)]
    [InlineData("spotify:sausage:1", EntityKind.Unknown, EntityProvider.Spotify)]
    [InlineData("https://open.spotify.com/track/x", EntityKind.Unknown, EntityProvider.None)]
    [InlineData("", EntityKind.Unknown, EntityProvider.None)]
    [InlineData("trx", EntityKind.Unknown, EntityProvider.None)]        // legacy fake ids are digits after the pair
    public void Parse_answers_the_kind_and_the_provider(string uri, EntityKind kind, EntityProvider provider)
    {
        var parsed = EntityUri.Parse(uri.AsSpan());
        Assert.Equal(kind, parsed.Kind);
        Assert.Equal(provider, parsed.Provider);
        // Every uri in this corpus is already spelled canonically, so the round trip is the identity — a gid-form id
        // MATERIALISES this text from 16 bytes (it interned none of it), a text-form id resolves the one string it kept.
        Assert.Equal(uri, parsed.Text);
    }

    /// <summary>WHICH FORM each uri takes — the decision the whole 24-byte layout rests on. The six catalog kinds with
    /// a 22-base62 id are gids and cost no string at all; every other line of the corpus keeps its text, including the
    /// short test-fixture ids and the non-ASCII ones (doc §6 "ids that are not 22 chars").</summary>
    [Theory]
    [InlineData("spotify:track:4uLU6hMCjMI75M1A2tKUQC", EntityForm.Gid)]
    [InlineData("spotify:episode:512ojhOuo1ktJprKbVcKyQ", EntityForm.Gid)]
    [InlineData("spotify:album:2noRn2Aes5aoNVsU6iWThc", EntityForm.Gid)]
    [InlineData("spotify:artist:0OdUWJ0sBjDrqHygGUXeCF", EntityForm.Gid)]
    [InlineData("spotify:playlist:37i9dQZF1DXcBWIGoYBM5M", EntityForm.Gid)]
    [InlineData("spotify:show:5CfCWKI5pZ28U0uOzXkDHe", EntityForm.Gid)]
    [InlineData("spotify:prerelease:2noRn2Aes5aoNVsU6iWThc", EntityForm.Gid)]
    [InlineData("spotify:user:christos:playlist:37i9dQZF1DXcBWIGoYBM5M", EntityForm.Gid)]   // folded, defect 4
    [InlineData("spotify:track:abc", EntityForm.Text)]                  // a fixture id, not a gid
    [InlineData("spotify:album:4Xy2", EntityForm.Text)]
    [InlineData("spotify:playlist:1a2b", EntityForm.Text)]
    [InlineData("spotify:user:christos", EntityForm.Text)]              // user is not a gid kind
    [InlineData("spotify:concert:3ab7ff", EntityForm.Text)]             // nor is concert
    [InlineData("spotify:collection:tracks", EntityForm.Text)]
    [InlineData("spotify:folder:1a2b3c", EntityForm.Text)]
    [InlineData("spotify:user:christos:playlist:37i9dQ", EntityForm.Text)]
    [InlineData("wavee:local:file:QzpcbXVzaWNcYS5tcDM", EntityForm.Text)]
    [InlineData("wavee:module:radio:aHR0cDovL3g", EntityForm.Text)]
    [InlineData("fake:album:7", EntityForm.Text)]
    [InlineData("tr7", EntityForm.Text)]
    [InlineData("spotify:playlist:Ωμέγα", EntityForm.Text)]         // non-ASCII: never a gid
    [InlineData("https://open.spotify.com/track/x", EntityForm.Text)]   // unowned, but its text is kept
    [InlineData("", EntityForm.None)]
    public void The_parse_decides_the_form_once(string uri, EntityForm form)
    {
        Assert.Equal(form, EntityId.Parse(uri.AsSpan()).Form);
        Assert.Equal(form, EntityId.Parse(Encoding.UTF8.GetBytes(uri)).Form);
    }

    /// <summary>The char overload narrows to ASCII and runs the SAME byte walk, so the two can never drift. Pinned
    /// over a corpus that includes non-ASCII ids, where the narrowing maps to 0xFF and must still decide identically.</summary>
    [Theory]
    [InlineData("spotify:track:4uLU6hMCjMI75M1A2tKUQC")]
    [InlineData("spotify:playlist:Ωμέγα")]
    [InlineData("wavee:local:file:8J-Ygg")]
    [InlineData("spotify:user:μπάμπης:playlist:x")]
    [InlineData("fake:artist:日本語")]
    [InlineData("völlig:unbekannt")]
    public void The_char_and_byte_parses_agree(string uri)
    {
        var byChars = EntityUri.ProviderOf(uri.AsSpan(), out var kindFromChars);
        var byBytes = EntityUri.ProviderOf(Encoding.UTF8.GetBytes(uri), out var kindFromBytes);
        Assert.Equal(byChars, byBytes);
        Assert.Equal(kindFromChars, kindFromBytes);
        Assert.Equal(EntityUri.KindOf(uri.AsSpan()), EntityUri.KindOf(Encoding.UTF8.GetBytes(uri)));
        // …and so do the two id parses, which is the stronger statement now that the id is what a row is keyed by.
        Assert.Equal(EntityId.Parse(uri.AsSpan()), EntityId.Parse(Encoding.UTF8.GetBytes(uri)));
    }

    /// <summary>A prerelease is an ALBUM row with a flag, not a kind of its own (ch 05, ch 31 §7.3) — so the countdown
    /// surface reads one table and a release does not migrate the row between kinds. The flag now rides in the id, and
    /// it is part of the identity: a prerelease gid is NOT the album gid it becomes (doc §6).</summary>
    [Fact]
    public void A_prerelease_uri_resolves_to_an_album_row_and_keeps_its_spelling()
    {
        Assert.Equal(EntityKind.Album, EntityUri.KindOf("spotify:prerelease:4Xy2".AsSpan()));
        Assert.True(EntityUri.IsPrerelease("spotify:prerelease:4Xy2".AsSpan()));
        Assert.True(EntityUri.IsPrerelease("spotify:prerelease:4Xy2"u8));
        Assert.False(EntityUri.IsPrerelease("spotify:album:4Xy2".AsSpan()));

        string gid = GidText(11);
        var pre = EntityId.Parse($"spotify:prerelease:{gid}".AsSpan());
        var album = EntityId.Parse($"spotify:album:{gid}".AsSpan());

        Assert.Equal(EntityKind.Album, pre.Kind);
        Assert.True(pre.IsPrerelease);
        Assert.False(album.IsPrerelease);
        Assert.NotEqual(album, pre);                                    // one flag bit apart, and therefore two rows
        Assert.Equal($"spotify:prerelease:{gid}", pre.Text);            // Format must NOT answer "album:"
        Assert.Equal($"spotify:album:{gid}", album.Text);
    }

    /// <summary>DEFECT 4. Two spellings of one playlist interned to two <c>StringId</c>s and allocated two rows
    /// (doc §4.4); the id is decoded from the TRAILING segment, so they are one identity by construction. It is
    /// UNVERIFIED whether the 0.3 wire path still emits the namespaced form — the fold is kept either way, and the
    /// canonical spelling is what a round trip answers.</summary>
    [Fact]
    public void The_two_playlist_spellings_are_one_identity()
    {
        string gid = GidText(3);
        var namespaced = EntityId.Parse($"spotify:user:christos:playlist:{gid}".AsSpan());
        var canonical = EntityId.Parse($"spotify:playlist:{gid}".AsSpan());

        Assert.Equal(canonical, namespaced);
        Assert.Equal(canonical.GetHashCode(), namespaced.GetHashCode());
        Assert.Equal(EntityKind.Playlist, namespaced.Kind);
        Assert.Equal($"spotify:playlist:{gid}", namespaced.Text);       // the round trip canonicalises, deliberately
        Assert.Equal(EntityForm.Gid, namespaced.Form);

        // A namespaced playlist whose id is NOT a gid still cannot fold — there is nothing to fold it onto.
        Assert.NotEqual(EntityId.Parse("spotify:playlist:1a2b".AsSpan()),
                        EntityId.Parse("spotify:user:christos:playlist:1a2b".AsSpan()));
    }

    /// <summary>The wire has two doors onto the same entity — a uri string and 16 raw protobuf bytes — and they must
    /// arrive at ONE id, or a decoder's rows and a page's rows are different rows (doc §2).</summary>
    [Fact]
    public void A_gid_off_the_wire_and_the_uri_that_spells_it_are_one_identity()
    {
        string gid = GidText(5);
        var fromUri = EntityId.Parse($"spotify:track:{gid}".AsSpan());

        Span<byte> raw = stackalloc byte[Base62.GidBytes];
        Assert.Equal(Base62.GidBytes, fromUri.WriteGid(raw));
        var fromBytes = EntityId.ForGid(EntityKind.Track, raw);

        Assert.Equal(fromUri, fromBytes);
        Assert.Equal(fromUri.GetHashCode(), fromBytes.GetHashCode());
        Assert.Equal($"spotify:track:{gid}", fromBytes.Text);
        Assert.Equal(EntityProvider.Spotify, fromBytes.Provider);

        // The same 16 bytes under a different kind is a DIFFERENT entity — an album and a track never share a row.
        Assert.NotEqual(fromBytes, EntityId.ForGid(EntityKind.Album, raw));
        // …and a kind that has no gid form refuses one rather than minting an unroutable id.
        Assert.True(EntityId.ForGid(EntityKind.User, raw).IsEmpty);
        Assert.True(EntityId.ForGid(EntityKind.Concert, raw).IsEmpty);
    }

    /// <summary>Kind and provider are FIELDS of the id, which is what ends `EntityUri.Of`'s 45-100 ns re-parse of the
    /// text on every <c>.Uri</c> read (doc §1.3 item 3) — and text-form ids answer them just as directly.</summary>
    [Fact]
    public void Kind_provider_and_the_predicates_are_field_reads_in_both_forms()
    {
        var track = EntityId.Parse("spotify:track:4uLU6hMCjMI75M1A2tKUQC".AsSpan());
        var local = EntityId.Parse("wavee:local:file:QzpcbXVzaWNcYS5tcDM".AsSpan());
        var album = EntityId.Parse("spotify:album:2noRn2Aes5aoNVsU6iWThc".AsSpan());

        Assert.True(track.IsPlayable);
        Assert.True(local.IsPlayable);                                   // a local file is a track, form notwithstanding
        Assert.False(album.IsPlayable);
        Assert.True(album.IsContainer);
        Assert.True(track.IsValid && local.IsValid);
        Assert.False(EntityId.Parse("https://open.spotify.com/track/x".AsSpan()).IsValid);
        Assert.True(default(EntityId).IsEmpty);
        Assert.False(track.IsEmpty);

        Assert.Equal(EntityProvider.Local, local.Provider);
        Assert.Equal(StringId.Empty, track.TextId);                      // a gid row has NO string anywhere (defect 1)
        Assert.NotEqual(StringId.Empty, local.TextId);
        Assert.Equal(UInt128.Zero, local.Gid);
    }

    /// <summary>Equality is three word compares, and a hash disagreement here would silently lose rows in the table's
    /// open-addressed index. Also the negative: same gid, different kind; same text, different provider.</summary>
    [Fact]
    public void Equal_ids_hash_equal_and_unequal_ids_do_not_collide_by_construction()
    {
        var a = EntityId.Parse("spotify:track:4uLU6hMCjMI75M1A2tKUQC".AsSpan());
        var b = EntityId.Parse("spotify:track:4uLU6hMCjMI75M1A2tKUQC"u8);
        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.False(a != b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.True(a.Equals((object)b));

        var text = EntityId.Parse("wavee:playlist:session-1".AsSpan());
        Assert.Equal(text, EntityId.Parse("wavee:playlist:session-1".AsSpan()));
        Assert.NotEqual(a, text);
        Assert.NotEqual(text, EntityId.Parse("wavee:playlist:session-2".AsSpan()));

        // 4,096 distinct gids must produce 4,096 distinct hashes' worth of spread: a fold that lost the high word
        // would show up here as a pile-up long before a 10k-row table did.
        var hashes = new HashSet<int>();
        for (int i = 0; i < 4_096; i++) hashes.Add(EntityId.ForGid(EntityKind.Track, (UInt128)i * 0x9E37_79B9UL + 1).GetHashCode());
        Assert.True(hashes.Count > 4_000, $"only {hashes.Count} distinct hashes for 4,096 ids");
    }

    /// <summary>The identity of an already-interned uri is decided the same way the wire's is — otherwise
    /// <c>Slot(StringId)</c> and <c>Slot(utf8)</c> would hand out two rows for one entity.</summary>
    [Fact]
    public void An_interned_uri_classifies_exactly_as_the_wire_bytes_do()
    {
        foreach (string uri in new[]
                 {
                     "spotify:track:4uLU6hMCjMI75M1A2tKUQC", "spotify:track:abc", "wavee:local:file:8J-Ygg",
                     "spotify:user:christos:playlist:37i9dQZF1DXcBWIGoYBM5M", "tr7",
                 })
        {
            var fromText = EntityId.Of(Entities.Strings.Intern(uri));
            Assert.Equal(EntityId.Parse(uri.AsSpan()), fromText);
            Assert.Equal(EntityId.Parse(Encoding.UTF8.GetBytes(uri)), fromText);
        }
        Assert.True(EntityId.Of(StringId.Empty).IsEmpty);
    }

    /// <summary>Formatting is the cold path, but it must be exact: it is the sqlite key, the deep link, the PutState
    /// body and the copy-link. Into a span it allocates nothing.</summary>
    [Fact]
    public void Format_writes_the_canonical_uri_into_a_span_without_allocating()
    {
        string[] uris =
        [
            $"spotify:track:{GidText(1)}", $"spotify:album:{GidText(2)}", $"spotify:artist:{GidText(3)}",
            $"spotify:playlist:{GidText(4)}", $"spotify:show:{GidText(5)}", $"spotify:episode:{GidText(6)}",
            $"spotify:prerelease:{GidText(7)}", "spotify:user:christos", "wavee:local:file:8J-Ygg",
        ];
        var ids = new EntityId[uris.Length];
        for (int i = 0; i < uris.Length; i++) ids[i] = EntityId.Parse(uris[i].AsSpan());

        Span<char> chars = stackalloc char[EntityId.MaxGidTextChars];
        Span<byte> bytes = stackalloc byte[EntityId.MaxGidTextChars];
        for (int i = 0; i < ids.Length; i++)
        {
            Assert.Equal(uris[i], new string(chars[..ids[i].Format(chars)]));
            Assert.Equal(uris[i], Encoding.UTF8.GetString(bytes[..ids[i].Format(bytes)]));
            Assert.Equal(uris[i].Length, ids[i].FormattedLength);
            Assert.Equal(uris[i], ids[i].Text);
            Assert.Equal(uris[i], ids[i].ToString());
        }
        Assert.Equal(0, default(EntityId).Format(chars));
        Assert.Equal("", default(EntityId).Text);

        for (int i = 0; i < 4; i++) ids[0].Format(chars);                // warm-up
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1_000; i++) ids[i % ids.Length].Format(chars);
        Assert.Equal(0L, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    /// <summary>The wire probe is pure: a decoder on the socket thread may call it (C10), and a 300-uri batch must not
    /// allocate a byte (P14).</summary>
    [Fact]
    public void Parsing_a_gid_off_the_wire_allocates_nothing()
    {
        byte[] uri = Encoding.UTF8.GetBytes($"spotify:track:{GidText(9)}");
        for (int i = 0; i < 4; i++) EntityId.TryParseGid(uri, out _);    // warm the JIT

        // Nothing is asserted INSIDE the loop: xunit's own comparers can box, and the measurement is the parse.
        int tracks = 0;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 300; i++)                                    // one wire batch (P14)
            if (EntityId.TryParseGid(uri, out var id) && id.Kind == EntityKind.Track) tracks++;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(300, tracks);
        Assert.Equal(0L, allocated);
    }

    [Theory]
    [InlineData("spotify:user:x:playlist:y", "y")]
    [InlineData("spotify:track:abc", "abc")]
    [InlineData("bare", "bare")]
    [InlineData("spotify:track:", "")]
    public void IdOf_is_the_trailing_segment(string uri, string expected)
    {
        Assert.Equal(expected, EntityUri.IdOf(uri.AsSpan()).ToString());
        Assert.Equal(expected, Encoding.UTF8.GetString(EntityUri.IdOf(Encoding.UTF8.GetBytes(uri))));
    }

    /// <summary>Every spelling of Liked Songs answers yes; every SIBLING collection answers no. The second half is the
    /// bug 0.2.9 shipped four copies of.</summary>
    [Theory]
    [InlineData("spotify:collection:tracks", true)]
    [InlineData("spotify:user:christos:collection", true)]
    [InlineData("spotify:user:christos:collection:tracks", true)]
    [InlineData("spotify:collection:albums", false)]
    [InlineData("spotify:collection:artists", false)]
    [InlineData("spotify:user:collectionX:collection", true)]   // the LAST ":collection" is the one that counts
    [InlineData("spotify:playlist:x", false)]
    public void IsLikedCollection_knows_every_spelling_and_no_sibling(string uri, bool expected)
        => Assert.Equal(expected, EntityUri.IsLikedCollection(uri.AsSpan()));

    [Fact]
    public void CanonicalLiked_folds_every_spelling_to_one_id()
    {
        var namespaced = Entities.Strings.Intern("spotify:user:christos:collection");
        var canonical = Entities.Strings.Intern(EntityUri.LikedCollection);
        Assert.Equal(canonical, EntityUri.CanonicalLiked(namespaced));

        var playlist = Entities.Strings.Intern("spotify:playlist:x");
        Assert.Equal(playlist, EntityUri.CanonicalLiked(playlist));

        // …and over packed ids, which is what a row now holds. A collection is always the text form.
        var likedId = EntityId.Parse(EntityUri.LikedCollection.AsSpan());
        Assert.Equal(likedId, EntityUri.CanonicalLiked(EntityId.Parse("spotify:user:christos:collection".AsSpan())));
        Assert.Equal(likedId, EntityUri.CanonicalLiked(likedId));
        var album = EntityId.Parse("spotify:album:2noRn2Aes5aoNVsU6iWThc".AsSpan());
        Assert.Equal(album, EntityUri.CanonicalLiked(album));            // a gid id is never touched
    }

    [Theory]
    [InlineData("spotify:folder:1a2b3c", "1a2b3c")]
    [InlineData("spotify:folder:", "")]
    [InlineData("spotify:folder:nothex", "")]
    [InlineData("spotify:playlist:1a2b", "")]
    public void FolderIdOf_takes_only_the_hex_shape(string uri, string expected)
        => Assert.Equal(expected, EntityUri.FolderIdOf(uri.AsSpan()).ToString());

    // ── base62 (doc §2; ported from Wavee.Tests/CryptoTests.cs:161-192 plus the overflow vectors) ────────────────────

    [Fact]
    public void Base62_round_trips_every_gid_both_ways()
    {
        var random = new Random(20260912);
        Span<byte> gid = stackalloc byte[Base62.GidBytes];
        Span<char> chars = stackalloc char[Base62.GidChars];
        Span<byte> utf8 = stackalloc byte[Base62.GidChars];
        Span<byte> back = stackalloc byte[Base62.GidBytes];

        for (int n = 0; n < 500; n++)
        {
            random.NextBytes(gid);
            UInt128 value = Base62.ReadBytes(gid);

            Assert.Equal(Base62.GidChars, Base62.Encode(value, chars));
            Assert.Equal(Base62.GidChars, Base62.Encode(value, utf8));
            Assert.True(Base62.TryDecode(chars, out UInt128 fromChars));
            Assert.True(Base62.TryDecode(utf8, out UInt128 fromBytes));
            Assert.Equal(value, fromChars);
            Assert.Equal(value, fromBytes);

            Base62.WriteBytes(value, back);
            Assert.True(gid.SequenceEqual(back));                        // 16 bytes → 22 chars → 16 bytes
        }
    }

    /// <summary>The three vectors that pin the alphabet and the width: zero, the 0.2.9 test's gid, and 2^128−1.</summary>
    [Fact]
    public void Base62_matches_the_known_vectors()
    {
        Span<char> chars = stackalloc char[Base62.GidChars];

        Base62.Encode(UInt128.Zero, chars);
        Assert.Equal(new string('0', 22), new string(chars));

        byte[] deadbeef = [0xde, 0xad, 0xbe, 0xef, 0x00, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88, 0x99, 0xaa, 0xbb];
        Base62.Encode(Base62.ReadBytes(deadbeef), chars);
        Assert.Equal("6MbHjE430hTHczIwsQnWZ5", new string(chars));

        Base62.Encode(UInt128.MaxValue, chars);
        Assert.Equal("7N42dgm5tFLK9N8MT7fHC7", new string(chars));       // the largest 128-bit value there is
        Assert.True(Base62.TryDecode("7N42dgm5tFLK9N8MT7fHC7".AsSpan(), out UInt128 max));
        Assert.Equal(UInt128.MaxValue, max);
    }

    /// <summary>THE overflow guard. 62^22 &gt; 2^128, so a 22-char string can name a value no gid can hold; 0.2.9's
    /// decoder accumulated it in a <c>BigInteger</c> and kept the LOW 16 bytes (<c>Base62.cs:26-36</c>), which maps
    /// <c>7N42dgm5tFLK9N8MT7fHC8</c> — one past the maximum — onto the ALL-ZEROS gid. Two distinct uris, one row. This
    /// decoder refuses, the uri falls to the text form, and they stay two rows (doc §6).</summary>
    [Theory]
    [InlineData("7N42dgm5tFLK9N8MT7fHC8")]                               // 2^128 exactly: the first overflow
    [InlineData("zzzzzzzzzzzzzzzzzzzzzz")]                               // the largest 22-char string
    [InlineData("8000000000000000000000")]
    public void Base62_refuses_a_22_char_string_that_is_not_a_gid(string overflowing)
    {
        Assert.False(Base62.TryDecode(overflowing.AsSpan(), out UInt128 value));
        Assert.Equal(UInt128.Zero, value);
        Assert.False(Base62.TryDecode(Encoding.UTF8.GetBytes(overflowing), out _));

        // …and the uri that carries it is a TEXT row, distinct from the all-zeros gid it would have aliased onto.
        var refused = EntityId.Parse($"spotify:track:{overflowing}".AsSpan());
        var zeros = EntityId.Parse("spotify:track:0000000000000000000000".AsSpan());
        Assert.Equal(EntityForm.Text, refused.Form);
        Assert.Equal(EntityForm.Gid, zeros.Form);
        Assert.NotEqual(zeros, refused);
        Assert.Equal($"spotify:track:{overflowing}", refused.Text);      // and it kept its spelling
    }

    [Theory]
    [InlineData("")]
    [InlineData("4uLU6hMCjMI75M1A2tKU")]                                 // 20 chars
    [InlineData("4uLU6hMCjMI75M1A2tKUQCX")]                              // 23
    [InlineData("4uLU6hMCjMI75M1A2tKU-C")]                               // out of alphabet
    [InlineData("4uLU6hMCjMI75M1A2tKU C")]
    [InlineData("4uLU6hMCjMI75M1A2tKÜQC")]                          // non-ASCII
    public void Base62_refuses_anything_that_is_not_22_in_alphabet_characters(string bad)
    {
        Assert.False(Base62.TryDecode(bad.AsSpan(), out _));
        Assert.False(Base62.TryDecode(Encoding.UTF8.GetBytes(bad), out _));
    }

    [Fact]
    public void Base62_allocates_nothing()
    {
        Span<char> chars = stackalloc char[Base62.GidChars];
        for (int i = 0; i < 4; i++) { Base62.Encode((UInt128)i, chars); Base62.TryDecode(chars, out _); }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1_000; i++)
        {
            Base62.Encode((UInt128)i * 0x9E37_79B9UL, chars);
            Base62.TryDecode(chars, out _);
        }
        Assert.Equal(0L, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    // ── the interner seam (P14) ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Interning_utf8_lands_on_the_same_id_as_interning_chars()
    {
        const string text = "Everything In Its Right Place";
        var fromChars = Entities.Strings.Intern(text);
        var fromBytes = Entities.Intern(Encoding.UTF8.GetBytes(text));
        Assert.Equal(fromChars, fromBytes);
        Assert.Equal(text, Entities.Strings.Resolve(fromBytes));
        Assert.Equal(StringId.Empty, Entities.Intern(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void Interning_utf8_that_is_already_known_allocates_nothing()
    {
        byte[] utf8 = Encoding.UTF8.GetBytes("spotify:track:allocation-probe");
        for (int i = 0; i < 4; i++) Entities.Intern(utf8);          // warm the interner and the JIT

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1_000; i++) Entities.Intern(utf8);
        Assert.Equal(0L, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    /// <summary>DEFECT 1, at the primitive. <c>RetainText</c>/<c>ReleaseText</c> are the pair every owned
    /// <see cref="StringId"/> goes through; the interner drops its map entry when the last reference goes, and a
    /// string nobody ever AddRef'd stays permanent (which is why the pair has to be used, not remembered).</summary>
    [Fact]
    public void RetainText_and_ReleaseText_hand_a_string_back_to_the_interner()
    {
        int before = Entities.Strings.MapCount;
        StringId cell = StringId.Empty;

        Entities.RetainText(ref cell, Entities.Strings.Intern("a title nothing else interns 20260912"));
        Assert.Equal(before + 1, Entities.Strings.MapCount);

        // Overwriting releases what it replaces: the first title goes, the second arrives — net zero, not net one.
        Entities.RetainText(ref cell, Entities.Strings.Intern("a second title nothing else interns 20260912"));
        Assert.Equal(before + 1, Entities.Strings.MapCount);

        Entities.RetainText(ref cell, cell);                            // idempotent: same id, no churn
        Assert.Equal(before + 1, Entities.Strings.MapCount);

        Entities.ReleaseText(ref cell);
        Assert.Equal(before, Entities.Strings.MapCount);
        Assert.Equal(StringId.Empty, cell);                             // blanked, so a recycled row cannot resolve it
    }

    // ── the wanted & ~known sweep (P15) ─────────────────────────────────────────────────────────────────────────────

    /// <summary>P15's equivalence gate: the vector path and the scalar reference must agree, at every length around the
    /// floor and the lane width, on random data. The production function takes the vector path only above
    /// <see cref="Entities.SimdFloor"/>, so the short lengths here also exercise its own scalar arm.</summary>
    [Fact]
    public void The_vector_sweep_and_the_scalar_sweep_agree()
    {
        var random = new Random(20260912);
        Span<int> vector = new int[4096];
        Span<int> scalar = new int[4096];

        foreach (int count in new[] { 0, 1, 3, 4, 5, 7, 15, 16, 17, 31, 64, 127, 1000, 4096 })
        {
            var known = new uint[count];
            for (int i = 0; i < count; i++)
                known[i] = random.Next(4) == 0 ? 0xFFFF_FFFFu : (uint)random.Next(1 << 20);

            foreach (uint wanted in new uint[] { 1, 0b1_0000_0001, 0x3F, 0xFFFF })
            {
                int fromVector = Entities.ScanMissing(known, wanted, 0, count, vector);
                int fromScalar = ScalarScanMissing(known, wanted, 0, count, scalar);
                Assert.Equal(fromScalar, fromVector);
                Assert.True(vector[..fromVector].SequenceEqual(scalar[..fromScalar]),
                    $"count={count} wanted={wanted}: the vector arm disagreed with the scalar reference");
            }
        }
    }

    /// <summary>The reference the vector arm is held to — deliberately the dumbest possible loop.</summary>
    static int ScalarScanMissing(ReadOnlySpan<uint> known, uint wanted, int from, int count, Span<int> dst)
    {
        int n = 0;
        for (int i = from; i < from + count && n < dst.Length; i++)
            if ((wanted & ~known[i]) != 0) dst[n++] = i;
        return n;
    }

    [Fact]
    public void The_sweep_stops_when_the_output_is_full_and_the_caller_can_resume()
    {
        var known = new uint[64];                                    // every row knows nothing
        Span<int> dst = stackalloc int[8];

        int first = Entities.ScanMissing(known, 1, 0, known.Length, dst);
        Assert.Equal(8, first);
        Assert.Equal(0, dst[0]);
        Assert.Equal(7, dst[7]);

        int resumed = Entities.ScanMissing(known, 1, dst[7] + 1, known.Length - dst[7] - 1, dst);
        Assert.Equal(8, resumed);
        Assert.Equal(8, dst[0]);
    }

    [Fact]
    public void A_thousand_row_sweep_allocates_nothing()
    {
        var known = new uint[1024];
        for (int i = 0; i < known.Length; i++) known[i] = (uint)(i % 7 == 0 ? 0 : 0xFF);
        var dst = new int[1024];
        for (int i = 0; i < 4; i++) Entities.ScanMissing(known, 0xFF, 0, known.Length, dst);   // warm-up

        long before = GC.GetAllocatedBytesForCurrentThread();
        int found = Entities.ScanMissing(known, 0xFF, 0, known.Length, dst);
        Assert.Equal(0L, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(147, found);                                    // 0, 7, 14, … 1022
    }

    [Fact]
    public void SelectMissing_gathers_over_an_explicit_slot_list()
    {
        var known = new uint[] { 0, 0b11, 0b01, 0b11, 0 };
        Span<int> dst = stackalloc int[8];
        int n = Entities.SelectMissing([4, 2, 3, 1], known, 0b11, dst);
        Assert.Equal(2, n);
        Assert.Equal(4, dst[0]);                                     // list order is preserved: the planner batches in it
        Assert.Equal(2, dst[1]);
    }

    // ── staging (C10, P14) ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_staging_arena_round_trips_text_and_resets_without_shrinking()
    {
        var staging = Staging.Rent();
        var title = staging.AddText("Weird Fishes"u8);
        var artist = staging.AddText("Radiohead"u8);

        Assert.Equal("Weird Fishes", Encoding.UTF8.GetString(staging.Utf8(title)));
        Assert.Equal("Radiohead", Encoding.UTF8.GetString(staging.Utf8(artist)));
        Assert.Equal(Entities.Strings.Intern("Weird Fishes"), staging.Intern(title));
        Assert.True(staging.AddText(default).IsEmpty);
        Assert.Equal(StringId.Empty, staging.Intern(default));

        staging.Epoch = 7;
        staging.Reset();
        Assert.Equal(0u, staging.Epoch);
        Assert.Equal(Authority.Full, staging.Authority);
        Assert.Equal(0, staging.AddText("x"u8).Offset);              // the arena rewound; the buffer did not shrink
        Staging.Return(staging);
    }

    [Fact]
    public void A_returned_staging_buffer_comes_back_clean()
    {
        var first = Staging.Rent();
        first.Epoch = 42;
        first.AddText("some decoded title"u8);
        Staging.Return(first);

        var second = Staging.Rent();
        Assert.Equal(0u, second.Epoch);
        Assert.Equal(0, second.AddText("y"u8).Offset);
        Staging.Return(second);
    }

    [Fact]
    public void A_staged_list_hands_back_rows_by_reference()
    {
        var list = new StagedList<int>();
        for (int i = 0; i < 40; i++) list.Add() = i;                 // grows past its initial 16 without losing rows
        Assert.Equal(40, list.Count);
        Assert.Equal(39, list[39]);
        Assert.Equal(40, list.Span.Length);

        list.Clear();
        Assert.Equal(0, list.Count);
        Assert.Equal(0, list.Add());                                 // a reused row is zeroed, never the dead row's value
    }
}
