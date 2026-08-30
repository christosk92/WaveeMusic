using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Wavee.Backend.Hydration;
using Wavee.Backend.Spotify;
using Wavee.Core;
using Xm = Wavee.Protocol.ExtendedMetadata;

namespace Wavee.SpotifyLive.Hydration;

// ── Playlist owner / added-by identities, as a port (design §2.2 `IUserProfileFetch`) ────────────────────────────────
// TWO arms, and they are not interchangeable. Kind 15 answers a whole page of owners in ONE batched POST and is the arm
// that matters for a 10k playlist; `/user-profile-view/v3/profile/{id}` answers ONE user per request and exists only
// because the batch does not know every account (a fresh or restricted profile 404s on 15). So: batch first, REST for
// whatever is LEFT.
//
// THIN OVER IExtensionReader (design §2.5) for the batch arm — the etag cache, the 300-per-POST chunking and the shared
// negative memo are the reader's, so a user the wire has already said "no" for stops costing a batch slot in every later
// prefetch. The REST arm keeps its OWN SemaphoreSlim(4): that is not batching, it is a fan-out throttle on a
// one-request-per-user endpoint, and nothing in the reader replaces it.
//
// What this class is NOT any more: the Owner read model. There is no dictionary, no in-flight map and no `Changed`
// event here — `UserHydration` writes the resolved owners into the store and the ledger owns the dedupe, so the whole
// "a read source subscribes to a service so it can Bump the playlists that referenced the owner" contraption is gone.
//
// The reader is OPTIONAL: `LiveSessionHost` resolves the signed-in account's own profile during login, BEFORE the
// session's extended-metadata reader exists, and it must not grow a third copy of this parser to do it. A null reader
// simply means "REST only".
public sealed class SpotifyUserProfileFetch : IUserProfileFetch
{
    /// <summary>Concurrent REST requests. One request PER USER on this endpoint, so the fan-out is throttled here and
    /// only here.</summary>
    const int RestConcurrency = 4;

    readonly IExtensionReader? _reader;
    readonly IHttpExchange _http;
    readonly Func<string> _baseUrl;
    readonly WaveeLogger _log;

    public SpotifyUserProfileFetch(IExtensionReader? reader, IHttpExchange http, Func<string> baseUrl,
                                   WaveeLogger log = default)
    {
        _reader = reader;
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _baseUrl = baseUrl ?? throw new ArgumentNullException(nameof(baseUrl));
        _log = log;
    }

    /// <summary>Resolve a set of user ids (bare or `spotify:user:`). The result is keyed by the CANONICAL uri; a null
    /// value is a real answer ("no renderable profile"), an ABSENT key means the transport could not say — which is what
    /// keeps a dead socket from earning a genuine-absence seal.</summary>
    public async Task<IReadOnlyDictionary<string, Owner?>> ResolveAsync(IReadOnlyList<string> userIds, CancellationToken ct)
    {
        var result = new Dictionary<string, Owner?>(userIds?.Count ?? 0, StringComparer.Ordinal);
        if (userIds is not { Count: > 0 }) return result;

        var asked = new List<string>(userIds.Count);
        var unresolved = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < userIds.Count; i++)
            if (UserProfileIds.Normalize(userIds[i]) is { } canonical && unresolved.Add(canonical)) asked.Add(canonical);
        if (asked.Count == 0) return result;

        if (_reader is { } reader)
        {
            IReadOnlyDictionary<string, ProfileFields> fields;
            try
            {
                fields = await reader.ReadManyAsync(asked, Xm.ExtensionKind.UserProfile, ParsePayload,
                                                    TraitSurface.UserProfiles, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.Info("USER_PROFILE extended-metadata fetch: " + ex.Message);
                fields = new Dictionary<string, ProfileFields>();
            }
            foreach (var (uri, parsed) in fields)
            {
                result[uri] = ToOwner(parsed, uri);
                unresolved.Remove(uri);
            }
        }

        if (unresolved.Count > 0)
        {
            using var gate = new SemaphoreSlim(RestConcurrency);
            var pending = new List<string>(unresolved);
            var tasks = new List<Task<(string Uri, bool Answered, Owner? Owner)>>(pending.Count);
            for (int i = 0; i < pending.Count; i++) tasks.Add(ResolveRestAsync(pending[i], gate, ct));
            foreach (var (uri, answered, owner) in await Task.WhenAll(tasks).ConfigureAwait(false))
                if (answered) result[uri] = owner;
        }

        return result;
    }

    async Task<(string Uri, bool Answered, Owner? Owner)> ResolveRestAsync(string userUri, SemaphoreSlim gate, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var username = UserProfileIds.BareId(userUri);
            var url = _baseUrl() + "/user-profile-view/v3/profile/" + Uri.EscapeDataString(username) + "?market=from_token";
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Accept"] = "application/json" };
            using var resp = await _http.SendAsync(new HttpReq("GET", url, headers, null), ct).ConfigureAwait(false);
            // A 404 IS an answer ("this account has no public profile") and must be recorded so the ladder seals it;
            // any other non-200 is a transport verdict we are not entitled to cache as an absence.
            if (resp.Status != 200) return (userUri, resp.Status == 404, null);
            using var doc = await JsonDocument.ParseAsync(resp.Body, default, ct).ConfigureAwait(false);
            return (userUri, true, ReadFields(doc.RootElement) is { } parsed ? ToOwner(parsed, userUri) : null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Info("user profile REST fetch: " + ex.Message);
            return (userUri, false, null);
        }
        finally { gate.Release(); }
    }

    /// <summary>What kind 15's JSON body actually carries. This — not <see cref="Owner"/> — is what the reader caches,
    /// because it is URI-INDEPENDENT: the canonical id is derived from the key the caller asked with, and a parsed
    /// answer that baked one caller's spelling in would be wrong for the next.</summary>
    sealed record ProfileFields(string? Uri, string? Name, string? Avatar);

    /// <summary>The reader's parse hook. Null means "nothing renderable here", which the reader caches and memoizes
    /// exactly like the 404 it is indistinguishable from on screen. A malformed body throws out of here on purpose —
    /// the reader logs undecodable and treats it as the same null, in one place.</summary>
    static ProfileFields? ParsePayload(ByteString payload)
    {
        if (payload.IsEmpty) return null;
        using var doc = JsonDocument.Parse(payload.ToByteArray());
        return ReadFields(doc.RootElement);
    }

    static ProfileFields? ReadFields(JsonElement root)
    {
        string? name = StringValue(root, "name") ?? StringValue(root, "display_name");
        string? avatar = StringValue(root, "image_url") ?? FirstImage(root);
        if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(avatar)) return null;
        return new ProfileFields(StringValue(root, "uri"), name, avatar);
    }

    /// <summary>The store row. The id is the one we ASKED with, always: it is the spelling the playlist header and the
    /// membership row carry, so it is the spelling every later <c>GetOwner</c> uses. A payload uri that disagreed would
    /// file the answer under a key nobody looks up, and the page would re-ask forever.</summary>
    static Owner ToOwner(ProfileFields fields, string canonicalUri)
    {
        string id = UserProfileIds.BareId(canonicalUri);
        return new Owner(id,
            string.IsNullOrWhiteSpace(fields.Name) ? id : fields.Name.Trim(),
            string.IsNullOrWhiteSpace(fields.Avatar) ? null : new Image(fields.Avatar));
    }

    static string? StringValue(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } s
            ? s
            : null;

    static string? FirstImage(JsonElement root)
    {
        if (!root.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Array) return null;
        foreach (var image in images.EnumerateArray())
            if (StringValue(image, "url") is { Length: > 0 } url) return url;
        return null;
    }
}
