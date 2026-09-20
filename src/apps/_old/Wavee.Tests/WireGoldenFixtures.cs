using System;
using System.Collections.Generic;
using System.IO;
using Google.Protobuf;
using Pl = Wavee.Protocol.Playlist;

namespace Wavee.Tests;

/// <summary>
/// Byte-exact playlist-wire goldens lifted from two official-desktop Fiddler captures (2026-08-15). Each
/// <c>Fixtures/playlist-wire/*.bin</c> is the raw HTTP body — everything after the first <c>\r\n\r\n</c>, with the
/// <c>Content-Length</c> verified against the byte count at extraction time, zstd-decompressed where the capture was
/// compressed. Request headers (including the bearer token) were never written.
/// <para>They back BOTH directions: structural decode assertions, and (P2) byte-exact REBUILD assertions — capture
/// bytes to domain ops via PlaylistWireMapper.MapOps, back through the production builder with the capture own
/// user/timestamp/nonce injected, and the result must equal the capture byte for byte.</para>
/// </summary>
public static class Golden
{
    public static string Dir => Path.Combine(AppContext.BaseDirectory, "Fixtures", "playlist-wire");

    public static byte[] Bytes(string name) => File.ReadAllBytes(Path.Combine(Dir, name + ".bin"));

    /// <summary>Parse a request golden as the <c>/changes</c> envelope.</summary>
    public static Pl.ListChanges Changes(string name) => Pl.ListChanges.Parser.ParseFrom(Bytes(name));

    /// <summary>Parse a response golden as the universal playlist4 read/write reply.</summary>
    public static Pl.SelectedListContent Content(string name) => Pl.SelectedListContent.Parser.ParseFrom(Bytes(name));

    /// <summary>The manifest: every request golden and its captured byte length. A re-extraction that changes a body is
    /// caught here rather than deep inside a decode assertion.</summary>
    public static readonly IReadOnlyDictionary<string, int> RequestSizes = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["a031-create-p1"] = 84,                 // POST /playlist/v2/playlist/{minted}/changes — create via /changes, 8-B base
        ["a042-rootlist-add-p1"] = 135,          // rootlist ADD of the freshly created P1, attrs{ts, public}
        ["a046-add-50-tracks"] = 3034,           // ADD add_last with 50 client-minted item_ids
        ["a143-keyed-rem-x3"] = 254,             // ONE delta, THREE keyed REM ops
        ["a148-keyed-mov-after-item"] = 290,     // keyed MOV, add_after_item
        ["a154-keyed-mov-add-first"] = 133,      // keyed MOV, add_first
        ["a164-folder-create"] = 197,            // rootlist folder create: ADD start-group + ADD end-group in one delta
        ["a281-rootlist-index-rem"] = 126,       // rootlist delete-playlist: index REM, no items_as_key
        ["a498-keyed-mov-add-last"] = 240,       // keyed MOV, add_last
        ["b037-folder-rename"] = 160,            // REM{from,len=1} (no items) + ADD carrying the ORIGINAL create ts
        ["b049-rootlist-mov"] = 85,              // rootlist reorder: positional MOV{from,len,to}
        ["b063-update-list-name"] = 106,         // UPDATE_LIST name, new_attributes only
        ["b078-perm-set-blocked"] = 2,           // SetPermissionLevelRequest{BLOCKED} == 08 01
        ["b108-perm-set-viewer"] = 2,            // SetPermissionLevelRequest{VIEWER}  == 08 02
        ["b128-folder-rename-outer"] = 166,      // outer-folder rename
    };

    /// <summary>Response goldens and their DECOMPRESSED byte lengths.</summary>
    public static readonly IReadOnlyDictionary<string, int> ResponseSizes = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        // NOTE: the create response captured is session 178 — the create of P2 (6QbD3n4hCF6uP8jqyiDsS5), not P1.
        // The shape is what matters: rev + sync_result + resulting_revisions + nonces + capabilities, no name/contents.
        ["a178-create-response"] = 322,          // zstd 209 B on the wire
        ["a164-folder-create-response"] = 113,   // rootlist /changes reply, uncompressed (single-delta)
        ["perm-get-blocked"] = 12,               // GET /permission/base -> Permission{revision(8 B), BLOCKED}
    };

    /// <summary>The user id that signed every capture (a public Spotify id).</summary>
    public const string CaptureUser = "31unjfmo3oefvlz36ef3eb6kj5tq";

    /// <summary>The 8-byte create base revision desktop posts a brand-new playlist against: <c>00000000726f6f74</c>.</summary>
    public static readonly byte[] CreateBase = Convert.FromHexString("00000000726F6F74");

    public static string Hex(ByteString b) => Convert.ToHexString(b.Span).ToLowerInvariant();
}
