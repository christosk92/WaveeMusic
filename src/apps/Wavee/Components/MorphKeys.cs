namespace Wavee;

// The connected-animation (shared-element / Hero) key for a cover IS the route key the Home card navigates with
// ("album:"+uri / "pl:"+uri) — already unique and present on both the card and the detail page, so source and dest
// agree with zero extra plumbing. One place for the convention so the two sides never drift. Null ⇒ no Hero (liked /
// artist covers in v1) ⇒ today's instant swap.
static class MorphKeys
{
    public static string? For(DetailKind kind, string? id) => id is { Length: > 0 }
        ? kind switch
        {
            DetailKind.Album => "album:" + id,
            DetailKind.Playlist => "pl:" + id,
            _ => null,   // liked has no uri; artist covers (circular) deferred
        }
        : null;
}
