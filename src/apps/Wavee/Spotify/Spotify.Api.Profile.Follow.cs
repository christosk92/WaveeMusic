// ── Spotify/Spotify.Api.Profile.Follow.cs ──────────────────────────────────────────────────────────────────────────────
// the user-follow operations: isFollowingUsers (a Follow-only row ask) and followUsers / unfollowUsers (the write)
//
// Role: SHELL (the sends) + CORE (the bodies and the answer readers, tested by ProfileFollowTests)
// Owner: D3 (profile pages)
// Wave: profile pages (2026-10-01)
// Spec: docs/plans/wavee/profile-pages-implementation.md Appendix D §3.1; docs/plans/wavee/profile-pages-api-research.md §3
//
// ONE persisted hash, THREE operation names (the operation name selects). The capture that proved the hash and the
// variables was the DESKTOP client's tuple (risk R3: if the gateway refuses with 400/403, flip `Web` to true in the three
// `ProfileQueries` rows).

using System.Text;
using System.Text.Json;

namespace Wavee;

/// <summary>What <c>isFollowingUsers</c> said about one user.</summary>
public enum FollowAnswer : byte { Unknown, Following, NotFollowing, NotFound }

public static partial class Spotify
{
    public static partial class Api
    {
        /// <summary>The user-follow operations: ONE persisted hash, three operation names (research §3). DESKTOP identity —
        /// the capture that proved the hash and variables was the desktop client's tuple (risk R3 has the one-word flip).</summary>
        public static class ProfileQueries
        {
            const string Hash = "c00e0cb6c7766e7230fc256cf4fe07aec63b53d1160a323940fce7b664e95596";
            public static readonly Query IsFollowingUsers = new("isFollowingUsers", Hash, false);
            public static readonly Query FollowUsers = new("followUsers", Hash, false);
            public static readonly Query UnfollowUsers = new("unfollowUsers", Hash, false);

            /// <summary><c>{uris:[…]}</c> — uris, not ids. PURE.</summary>
            public static byte[] IsFollowingBody(ReadOnlySpan<string> userUris)
            {
                var vars = new Vars(IsFollowingUsers);
                vars.WriteStrings("uris", userUris);
                return vars.Finish();
            }

            /// <summary><c>{usernames:[&lt;bare id&gt;]}</c> — bare, UNESCAPED ids (<see cref="UsernameOf"/>), not uris. PURE.</summary>
            public static byte[] FollowBody(string bareId, bool follow)
            {
                var vars = new Vars(follow ? FollowUsers : UnfollowUsers);
                vars.WriteStrings("usernames", [bareId]);
                return vars.Finish();
            }

            public static Result IsFollowingQuery(string userUri, CancellationToken ct)
                => Pathfinder(IsFollowingUsers, IsFollowingBody([userUri]), ct);

            public static Result Follow(string bareId, bool follow, CancellationToken ct)
                => Pathfinder(follow ? FollowUsers : UnfollowUsers, FollowBody(bareId, follow), ct);

            /// <summary>PathfinderOp.IsFollowingUsers (a Follow-only row ask). API THREAD.</summary>
            public static Result FollowStateAnswer(string userUri, Staging s)
            {
                Result result = IsFollowingQuery(userUri, CancellationToken.None);
                if (result.Ok && result.Length > 0) Decode.FollowState(result.Body, userUri, s);
                return result;
            }
        }

        /// <summary>The follow answers, PURE (JsonDocument: one parse per user click / Follow-only ask — not a hot path).</summary>
        public static class ProfileFollowAnswer
        {
            /// <summary><c>data.users[]: User{uri, following} | NotFound{uri}</c> — the entry for <paramref name="userUri"/>;
            /// a single entry whose uri spelling differs (escaping) is still the answer.</summary>
            public static FollowAnswer IsFollowing(byte[] json, string userUri)
            {
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) return FollowAnswer.Unknown;      // TryGetProperty throws on a non-object
                    if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
                        || !data.TryGetProperty("users", out var users) || users.ValueKind != JsonValueKind.Array)
                        return FollowAnswer.Unknown;
                    JsonElement? only = users.GetArrayLength() == 1 ? users[0] : null;
                    foreach (var u in users.EnumerateArray())
                    {
                        if (u.ValueKind != JsonValueKind.Object) continue;
                        bool match = u.TryGetProperty("uri", out var uri) && uri.ValueKind == JsonValueKind.String && uri.ValueEquals(userUri);
                        if (match) return Read(u);
                    }
                    return only is { ValueKind: JsonValueKind.Object } one ? Read(one) : FollowAnswer.Unknown;
                }
                catch (JsonException) { return FollowAnswer.Unknown; }

                static FollowAnswer Read(JsonElement u)
                {
                    if (u.TryGetProperty("__typename", out var t) && t.ValueKind == JsonValueKind.String && t.ValueEquals("NotFound"))
                        return FollowAnswer.NotFound;
                    if (!u.TryGetProperty("following", out var f)) return FollowAnswer.Unknown;
                    return f.ValueKind switch
                    {
                        JsonValueKind.True => FollowAnswer.Following,
                        JsonValueKind.False => FollowAnswer.NotFollowing,
                        _ => FollowAnswer.Unknown,
                    };
                }
            }

            /// <summary>Did a 200 <c>followUsers</c>/<c>unfollowUsers</c> take? LENIENT on purpose: only an explicit refusal
            /// (an errors array, a missing op, an <c>…Error</c> typename, <c>result: false</c> or a FAIL/ERROR/DENIED/NOT_ALLOWED
            /// string) is a no — a misread success would revert a real follow and toast an error; a misread refusal is
            /// reconciled by the next mount's refresh. An EMPTY body is a yes.</summary>
            public static bool WriteSucceeded(byte[] json, bool follow)
            {
                if (json.Length == 0) return true;
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) return false;
                    if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
                        return false;
                    if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
                        || !data.TryGetProperty(follow ? "followUsers" : "unfollowUsers", out var op) || op.ValueKind != JsonValueKind.Object)
                        return false;
                    if (!op.TryGetProperty("responses", out var responses) || responses.ValueKind != JsonValueKind.Array) return true;
                    foreach (var r in responses.EnumerateArray())
                    {
                        if (r.ValueKind != JsonValueKind.Object) continue;
                        if (r.TryGetProperty("__typename", out var t) && t.ValueKind == JsonValueKind.String
                            && (t.GetString() ?? "").EndsWith("Error", StringComparison.Ordinal)) return false;
                        if (r.TryGetProperty("result", out var result) && Refuses(result)) return false;
                    }
                    return true;
                }
                catch (JsonException) { return false; }
            }

            static bool Refuses(JsonElement result) => result.ValueKind switch
            {
                JsonValueKind.False => true,
                JsonValueKind.String => result.GetString() is { } v
                    && (v.Contains("FAIL", StringComparison.OrdinalIgnoreCase) || v.Contains("ERROR", StringComparison.OrdinalIgnoreCase)
                        || v.Contains("DENIED", StringComparison.OrdinalIgnoreCase) || v.Contains("NOT_ALLOWED", StringComparison.OrdinalIgnoreCase)),
                _ => false,
            };
        }
    }

    public static partial class Decode
    {
        /// <summary><c>isFollowingUsers</c> → the row's Follow group at Full. NotFound is "not followed"; Unknown stages
        /// nothing (the miss policy retries, then seals).</summary>
        public static void FollowState(byte[] json, string userUri, Staging s)
        {
            var answer = Api.ProfileFollowAnswer.IsFollowing(json, userUri);
            if (answer == FollowAnswer.Unknown) return;
            ref var row = ref s.Users.RowFor(Identity(s, Encoding.UTF8.GetBytes(userUri)), Authority.Full, (uint)UserFields.Follow);
            row.Flags = answer == FollowAnswer.Following ? (uint)UserFlags.Followed : 0u;
            s.Users.Settle();
        }
    }
}
