namespace Wavee.Core;

// ── User-id spelling, and nothing else ───────────────────────────────────────────────────────────────────────────────
// P4-C deleted `IUserProfileService` (+ its Switchable/Null wrappers, its private Owner cache and its `Changed` event):
// a resolved owner is now a STORE ENTITY (`IStore.UpsertOwner`/`GetOwner`, hydrated by `UserHydration`), so it reaches
// the UI through the ordinary store change stream like every other entity. What survives is the id vocabulary — the
// wire spells an owner three ways (`bare`, `spotify:user:bare`, mixed case) and every reader has to agree on one.
public static class UserProfileIds
{
    public const string Prefix = "spotify:user:";

    /// <summary>The CANONICAL owner uri (<c>spotify:user:&lt;lowercased id&gt;</c>) for a bare id or a user uri, or
    /// null when the input is not a legal user id. This is the store key (hot dictionary AND cold <c>entity.uri</c>)
    /// and the uri <c>UserHydration</c> is asked with, so a bare id and its uri can never become two rows.</summary>
    public static string? Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var trimmed = input.Trim();
        if (trimmed.Length == 0) return null;

        if (trimmed.StartsWith(Prefix, System.StringComparison.Ordinal))
        {
            var id = trimmed[Prefix.Length..];
            return IsBareId(id) ? Prefix + id.ToLowerInvariant() : null;
        }

        return IsBareId(trimmed) ? Prefix + trimmed.ToLowerInvariant() : null;
    }

    public static string BareId(string userUriOrId)
        => userUriOrId.StartsWith(Prefix, System.StringComparison.Ordinal)
            ? userUriOrId[Prefix.Length..]
            : userUriOrId;

    static bool IsBareId(string value)
    {
        if (value.Length == 0) return false;
        for (int i = 0; i < value.Length; i++)
        {
            char ch = value[i];
            if (char.IsWhiteSpace(ch) || ch == ':') return false;
        }
        return true;
    }
}
