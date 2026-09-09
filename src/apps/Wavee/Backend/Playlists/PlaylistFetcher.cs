using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.Immutable;
using Wavee.Backend.Sync;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend.Spotify;
using Wavee.Core;
using Pl = Wavee.Protocol.Playlist;

namespace Wavee.Backend.Playlists;

/// <summary>How a revision-gated <c>/diff</c> revalidation resolved (§2.6): ops applied in place / already current /
/// fell back to a full re-fetch (no baseline, stale revision (509), torn apply, or an unparseable response).</summary>
public enum DiffOutcome { Applied, UpToDate, FellBackToFull }
public sealed record PlaylistHeaderReadResult(Playlist Header, byte[]? Revision);

// ── The live membership fetch (SpotifyLive boundary, but Backend so the orchestration is unit-tested) ─────────────────
// GETs /playlist/v2/{path}?decorate=... and returns protocol observations. The replica coordinator owns adoption;
// catalog demand independently resolves the returned occurrence URIs.
// The same path serves a playlist and the rootlist (the rootlist is just a playlist of playlist-uri + group markers).
public sealed class PlaylistFetcher
{
    const string Decorate = "?decorate=revision,attributes,length,owner,capabilities,picture";
    readonly IHttpExchange _http;
    readonly Func<string> _baseUrl;
    readonly Func<string> _account;

    public PlaylistFetcher(IHttpExchange http, Func<string> baseUrl, Func<string> account)
        => (_http, _baseUrl, _account) = (http, baseUrl, account);

    public async Task<PlaylistReadResult> FetchPlaylistAsync(string uri, CancellationToken ct = default)
        => ReadSnapshot(uri, await GetAsync(uri, ct).ConfigureAwait(false));

    public async Task<Playlist?> FetchPlaylistHeaderAsync(string uri, CancellationToken ct = default)
    {
        var body = await GetAsync(uri, ct).ConfigureAwait(false);
        return body.Attributes is { } attributes ? HeaderOf(uri, attributes, body) : null;
    }

    public async Task<PlaylistHeaderReadResult> ReadHeaderAsync(string uri, CancellationToken ct = default)
    {
        var body = await GetAsync(uri, ct).ConfigureAwait(false);
        if (body.Attributes is not { } attributes)
            throw new FormatException("The playlist header response omitted its attributes.");
        return new(HeaderOf(uri, attributes, body), body.HasRevision ? body.Revision.ToByteArray() : null);
    }

    public async Task<byte[]?> FetchPlaylistRevisionAsync(string uri, CancellationToken ct = default)
    {
        var url = _baseUrl() + "/playlist/v2/" + PathOf(uri) + "?decorate=revision";
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Accept"] = "application/protobuf" };
        using var response = await _http.SendAsync(new HttpReq("GET", url, headers, null), ct).ConfigureAwait(false);
        if (response.Status != 200) throw new System.Net.Http.HttpRequestException("Playlist revision fetch failed.", null, (System.Net.HttpStatusCode)response.Status);
        var body = Pl.SelectedListContent.Parser.ParseFrom(response.Body);
        return body.HasRevision ? body.Revision.ToByteArray() : null;
    }

    public async Task<RootlistReadResult> FetchRootlistAsync(string uri, CancellationToken ct = default)
    {
        var body = await GetAsync(uri, ct).ConfigureAwait(false);
        return ReadRootlist(body);
    }

    public static RootlistReadResult ReadRootlist(Pl.SelectedListContent body)
    {
        if (body.Contents is { Truncated: true } || body.Contents is { Pos: > 0 })
            throw new InvalidOperationException("The rootlist response is an incomplete membership page.");
        if (body.Contents is null && !(body.HasLength && body.Length == 0))
            throw new InvalidOperationException("The rootlist response omitted its membership.");
        var uris = new List<string>();
        var stamps = new List<long>();
        if (body.Contents is { } contents)
            foreach (var item in contents.Items)
            {
                uris.Add(item.Uri);
                stamps.Add(item.Attributes is { HasTimestamp: true } a ? a.Timestamp : 0);
            }
        return new RootlistReadResult(RootlistTreeBuilder.EntriesFromUris(uris, stamps).ToImmutableArray(),
            body.HasRevision ? body.Revision.ToByteArray() : PlaylistWireMapper.ResultingRevision(body));
    }

    public PlaylistReadResult ReadSnapshot(string uri, Pl.SelectedListContent body)
    {
        if (body.Contents is { Truncated: true } || body.Contents is { Pos: > 0 })
            throw new InvalidOperationException("The playlist response is an incomplete membership page.");
        if (body.Contents is null && !(body.HasLength && body.Length == 0) && body.Attributes?.DeletedByOwner != true)
            throw new InvalidOperationException("The playlist response omitted its membership.");
        var (members, revision) = PlaylistWireMapper.ParseContents(body);
        return new PlaylistReadResult(uri, PlaylistReadKind.Snapshot, null, revision,
            members.ToImmutableArray(), [], body.Attributes is { } attr ? HeaderOf(uri, attr, body) : null);
    }

    public async Task<PlaylistReadResult> FetchPlaylistDiffAsync(string uri, PlaylistReplicaBaseline baseline,
        CancellationToken ct = default)
    {
        var revision = baseline.Revision;
        if (!PlaylistRevisions.IsWellFormed(revision) || baseline.State is ReplicaBaselineState.Missing or ReplicaBaselineState.RecoveryOnly or ReplicaBaselineState.NeedsResync)
            return await FetchPlaylistAsync(uri, ct).ConfigureAwait(false);
        var encoded = Uri.EscapeDataString(FormatRevision(revision!));
        var url = _baseUrl() + "/playlist/v2/" + PathOf(uri) + "/diff?revision=" + encoded + "&handlesContent=&hint_revision=" + encoded;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Accept"] = "application/protobuf" };
        Pl.SelectedListContent? body = null;
        bool unchanged = false;
        using (var response = await _http.SendAsync(new HttpReq("GET", url, headers, null), ct).ConfigureAwait(false))
        {
            if (response.Status == 200)
            {
                using var bytes = new MemoryStream();
                await response.Body.CopyToAsync(bytes, ct).ConfigureAwait(false);
                try { body = Pl.SelectedListContent.Parser.ParseFrom(SpotifyZstd.MaybeDecompressZstd(bytes.ToArray())); }
                catch (Google.Protobuf.InvalidProtocolBufferException) { }
            }
            else if (response.Status == 304) unchanged = true;
            else
                return await FetchPlaylistAsync(uri, ct).ConfigureAwait(false);
        }
        if (body is null && !unchanged)
        {
            // A malformed 200 cannot certify an unchanged baseline; a 304 is handled by the explicit read below.
            return await FetchPlaylistAsync(uri, ct).ConfigureAwait(false);
        }
        body ??= new Pl.SelectedListContent { UpToDate = true };
        if (body.Contents is not null) return ReadSnapshot(uri, body);
        ImmutableArray<PlaylistOp> ops = [];
        var kind = PlaylistReadKind.Unchanged;
        var head = revision;
        if (body.Diff is { } diff && !(body.HasUpToDate && body.UpToDate))
        {
            if (diff.HasFromRevision && !PlaylistRevisions.Equal(revision, diff.FromRevision.ToByteArray()))
                return await FetchPlaylistAsync(uri, ct).ConfigureAwait(false);
            try
            {
                ops = PlaylistWireMapper.MapOps(diff.Ops).ToImmutableArray();
                // Validate without publishing; the coordinator validates the expected base again at commit time.
                var candidate = new List<PlaylistMember>(baseline.Members);
                PlaylistDiffApplier.Apply(candidate, ops);
            }
            catch (ArgumentOutOfRangeException) { return await FetchPlaylistAsync(uri, ct).ConfigureAwait(false); }
            kind = PlaylistReadKind.Delta;
            head = diff.HasToRevision ? diff.ToRevision.ToByteArray() : null;
        }
        if (body.Diff is null && !(body.HasUpToDate && body.UpToDate))
            return await FetchPlaylistAsync(uri, ct).ConfigureAwait(false);
        Playlist? header = body.Attributes is { } attr ? HeaderOf(uri, attr, body) : null;
        return new PlaylistReadResult(uri, kind, revision, head, [], ops, header);
    }

    internal static string FormatRevision(byte[] revision)
        => BinaryPrimitives.ReadInt32BigEndian(revision.AsSpan(0, 4)) + "," + Convert.ToHexStringLower(revision.AsSpan(4));

    async Task<Pl.SelectedListContent> GetAsync(string uri, CancellationToken ct)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Accept"] = "application/protobuf" };
        using var response = await _http.SendAsync(new HttpReq("GET", _baseUrl() + "/playlist/v2/" + PathOf(uri) + Decorate, headers, null), ct).ConfigureAwait(false);
        if (response.Status != 200) throw new System.Net.Http.HttpRequestException("Playlist fetch failed.", null, (System.Net.HttpStatusCode)response.Status);
        return Pl.SelectedListContent.Parser.ParseFrom(response.Body);
    }

    // "spotify:playlist:abc" → "playlist/abc"; "spotify:user:bob:rootlist" → "user/bob/rootlist".
    static string PathOf(string uri) => uri.StartsWith("spotify:", StringComparison.Ordinal) ? uri.Substring(8).Replace(':', '/') : uri.Replace(':', '/');

    Playlist HeaderOf(string uri, Pl.ListAttributes attr, Pl.SelectedListContent slc)
    {
        string name = attr.HasName ? attr.Name : "";
        string? desc = attr.HasDescription ? attr.Description : null;
        string owner = slc.HasOwnerUsername ? slc.OwnerUsername : "";
        int len = slc.HasLength ? slc.Length : 0;
        // Seed a minimal owner chip from the header's owner username (id + name = the username, avatar null) so the owner
        // chip renders its NAME immediately on first paint instead of a bare monogram. This does NOT change owner
        // resolution: StoreLibrarySource.RawOwnerId already returned this same username (its OwnerName fallback), so the
        // UserProfileService overlay (StoreLibrarySource.OverlayOwner) still WINS — Get(raw) ?? header.Owner upgrades this
        // seed to the resolved display name + avatar; the null-avatar seed only fills the gap until the profile lands.
        Owner? ownerChip = owner.Length > 0 ? new Owner(owner, owner, null) : null;
        var daylist = DaylistWindowOf(attr);
        var chart = ChartInfoOf(attr);
        return new Playlist(EntityUri.IdOf(uri), uri, name, desc, owner, CoverOf(attr), len,
            Owner: ownerChip,
            Capabilities: CapabilitiesOf(attr, slc, owner),
            Format: attr.HasFormat ? attr.Format : null,
            Source: "spotify",
            Tuning: TuningOf(attr, slc),
            DaylistExpiresAtMs: daylist.ExpiresAtMs,
            DaylistCreatedAtMs: daylist.CreatedAtMs,
            // A full GET / diff of a playlist the owner deleted still answers 200 — with deleted_by_owner set. The sync
            // loop turns a header carrying this into the same eviction the dealer tombstone push takes.
            DeletedByOwner: attr.HasDeletedByOwner && attr.DeletedByOwner,
            ChartNewEntries: chart.NewEntries, ChartUpdatedAtMs: chart.UpdatedAtMs, ChartRankType: chart.RankType);
    }

    /// <summary>The daylist rollover window from the header's format_attributes — (expires, created) as unix ms,
    /// (0, 0) for every other format or when the keys are absent/unparsable. The Pathfinder home feed states these
    /// attributes as ISO-8601 instants; the playlist4 shape for this format is unpinned by any capture, so the parse
    /// accepts an epoch (seconds or ms) as well — whichever arrives, the same window comes out.</summary>
    internal static (long ExpiresAtMs, long CreatedAtMs) DaylistWindowOf(Pl.ListAttributes attr)
    {
        if (!attr.HasFormat || !string.Equals(attr.Format, "daylist", StringComparison.Ordinal)) return (0, 0);
        long expires = 0, created = 0;
        for (int i = 0; i < attr.FormatAttributes.Count; i++)
        {
            var item = attr.FormatAttributes[i];
            if (!item.HasKey || !item.HasValue) continue;
            if (item.Key == "expires") expires = InstantMs(item.Value);
            else if (item.Key == "created") created = InstantMs(item.Value);
        }
        return (expires, created);
    }

    /// <summary>A chart playlist's header facts from <c>format_attributes</c> (desktop-verified: <c>new_entries_count</c>,
    /// <c>last_updated</c> as ISO-8601, <c>rank_type</c>) — <c>(0, 0, null)</c> for every other format or when the keys
    /// are absent/unparsable. Sibling of <see cref="DaylistWindowOf"/> (same format_attributes bag, a different
    /// format value gates it).</summary>
    internal static (int NewEntries, long UpdatedAtMs, string? RankType) ChartInfoOf(Pl.ListAttributes attr)
    {
        if (!attr.HasFormat || !string.Equals(attr.Format, "chart", StringComparison.Ordinal)) return (0, 0, null);
        int newEntries = 0;
        long updatedAtMs = 0;
        string? rankType = null;
        for (int i = 0; i < attr.FormatAttributes.Count; i++)
        {
            var item = attr.FormatAttributes[i];
            if (!item.HasKey || !item.HasValue) continue;
            switch (item.Key)
            {
                case "new_entries_count":
                    int.TryParse(item.Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out newEntries);
                    break;
                case "last_updated": updatedAtMs = InstantMs(item.Value); break;
                case "rank_type": rankType = item.Value; break;
            }
        }
        return (newEntries, updatedAtMs, rankType);
    }

    /// <summary>Epoch seconds / epoch ms / ISO-8601 → unix ms; 0 when unparsable.</summary>
    internal static long InstantMs(string value)
    {
        if (value.Length == 0) return 0;
        if (long.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long n))
            return n <= 0 ? 0 : n < 100_000_000_000L ? n * 1000L : n;   // 11+ digits ⇒ already ms
        return DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var dto)
            ? dto.ToUnixTimeMilliseconds() : 0;
    }

    internal static PlaylistTuning? TuningOf(Pl.ListAttributes attr, Pl.SelectedListContent slc)
    {
        if (!slc.HasRevision || slc.Revision.Length != 24 || slc.Contents is not { } contents
            || contents.AvailableSignals.Count == 0) return null;

        var format = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < attr.FormatAttributes.Count; i++)
        {
            var item = attr.FormatAttributes[i];
            if (item.HasKey && item.HasValue) format[item.Key] = item.Value;
        }
        format.TryGetValue("session_control.selected_signals", out var selected);
        if (string.IsNullOrWhiteSpace(selected)) selected = null;

        var options = new List<PlaylistTuningOption>(contents.AvailableSignals.Count);
        for (int i = 0; i < contents.AvailableSignals.Count; i++)
        {
            var signal = contents.AvailableSignals[i];
            if (!signal.HasIdentifier || string.IsNullOrWhiteSpace(signal.Identifier)) continue;
            string id = signal.Identifier;
            var kind = string.Equals(id, "session-control-reset", StringComparison.Ordinal)
                ? PlaylistTuningOptionKind.Reset : PlaylistTuningOptionKind.Choice;
            string? label = null;
            int split = id.LastIndexOf('$');
            if (kind == PlaylistTuningOptionKind.Choice && split >= 0 && split + 1 < id.Length)
                format.TryGetValue("session_control_display.displayName." + id[(split + 1)..], out label);
            options.Add(new PlaylistTuningOption(id, string.IsNullOrWhiteSpace(label) ? null : label, kind));
        }
        return options.Count == 0
            ? null
            : new PlaylistTuning(slc.Revision.ToByteArray(), options, selected);
    }

    PlaylistCapabilities CapabilitiesOf(Pl.ListAttributes attr, Pl.SelectedListContent slc, string ownerUsername)
    {
        var cap = slc.Capabilities;
        string account = _account();
        // Compare bare ids: either side may arrive as "spotify:user:<id>" or the bare canonical username.
        bool isOwner = ownerUsername.Length > 0 && account.Length > 0 && string.Equals(
            Wavee.Core.UserProfileIds.BareId(ownerUsername), Wavee.Core.UserProfileIds.BareId(account),
            StringComparison.OrdinalIgnoreCase);
        bool canAdmin = cap?.CanAdministratePermissions ?? false;
        // Server admin flag is authoritative; username match is the fallback when the decorate payload omits it.
        bool effectiveOwner = isOwner || canAdmin;
        return new PlaylistCapabilities(
            CanView: cap?.CanView ?? false,
            CanEditItems: cap?.CanEditItems ?? false,
            CanEditMetadata: cap?.CanEditMetadata ?? false,
            IsCollaborative: attr.HasCollaborative && attr.Collaborative,
            IsOwner: effectiveOwner,
            CanAdministratePermissions: canAdmin || isOwner,
            // A reply without the block says nothing about rights; the all-false defaults above are placeholders, not
            // a revocation, and the page's notice rule reads this flag before it reads CanView.
            Known: cap is not null);
    }

    // The playlist cover: the server's pre-sized URLs first (largest), else the raw picture file id → the image CDN.
    static Image? CoverOf(Pl.ListAttributes attr)
    {
        for (int i = attr.PictureSize.Count - 1; i >= 0; i--)
            if (!string.IsNullOrEmpty(attr.PictureSize[i].Url)) return new Image(attr.PictureSize[i].Url);
        if (attr.Picture.Length > 0) return new Image("https://i.scdn.co/image/" + Convert.ToHexStringLower(attr.Picture.Span));
        return null;
    }

}
