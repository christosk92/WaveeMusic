// ── Entities/ShowViewPrefs.cs ──────────────────────────────────────────────────────────────────────────────────────────
// the per-show view prefs: the `podcast.views` blob's format (filter + sort per show, most recent first, capped) and the
// typed settings door the show page seeds from and persists to
//
// Role: CORE
// Spec: docs/themes/plan/technical-plan.md §5.1 C16 (X41); podcast-show-rework-implementation.md §5.2, §12 D-6
//
// Moved out of `Show.Rules.cs` (the format half is unchanged, byte for byte): the show page no longer reads and writes
// `Platform.Keys.PodcastViews` itself — `Blob()` / `Save(...)` are the ONE typed door, so a page in any theme seeds and
// persists its view the same way, and the key is touched here and nowhere else.

using System.Globalization;
using System.Text;

namespace Wavee;

/// <summary>Filter + sort per show in ONE setting (<see cref="Platform.Keys.PodcastViews"/>, D-6) — never a key per
/// uri, which grows the store by one value for every show ever opened.
/// <para>FORMAT: <c>id:status:order;id:status:order;…</c>, most recently written FIRST, at most <see cref="Cap"/>
/// entries: <see cref="Write"/> moves its show to the front and drops the least recent past the cap. <c>id</c> is the
/// show's BASE62 id — never its uri, whose colons are the separator: an id that is empty or holds ':' or ';' is
/// rejected (<see cref="Read"/> answers the defaults, <see cref="Write"/> returns the blob unchanged). status/order are
/// the caller's enums as non-negative decimal ints (<c>Episode.Rules.Status</c>; 0 newest · 1 oldest) — the caller
/// clamps a value its enum does not define, since the store is hand-editable.</para>
/// <para>MALFORMED input never throws: an entry that is not exactly three fields, an empty id, or a non-digit number is
/// skipped (and dropped by the next write); a blob with no valid entry for the show answers the defaults (0, 0).</para></summary>
public static class ShowViewPrefs
{
    public const int Cap = 64;

    // ── the typed settings door ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The stored blob (every show's view at once; "" when none was ever written).</summary>
    public static string Blob() => Platform.Settings.Get(Platform.Keys.PodcastViews);

    /// <summary>The show's stored (status, order) read from the settings store; (0, 0) when absent, malformed or the id
    /// is invalid. The caller clamps to the values its enums define.</summary>
    public static (int Status, int Order) Stored(ReadOnlySpan<char> showId) => Read(Blob(), showId);

    /// <summary>Persist this show's view: the show moves to the front of the blob and the store is written ONLY when the
    /// blob actually changed (re-saving the front entry costs no settings write). Returns whether it wrote. An invalid id
    /// leaves the blob unchanged and writes nothing.</summary>
    public static bool Save(ReadOnlySpan<char> showId, int status, int order)
    {
        string blob = Blob();
        string next = Write(blob, showId, status, order);
        if (string.Equals(next, blob, StringComparison.Ordinal)) return false;
        Platform.Settings.Set(Platform.Keys.PodcastViews, next);
        return true;
    }

    // ── the format ──────────────────────────────────────────────────────────────────────────────────────────────────

    public static bool IsValidId(ReadOnlySpan<char> id) => id.Length > 0 && id.IndexOfAny(':', ';') < 0;

    /// <summary>The show's stored (status, order); (0, 0) when absent, malformed or the id is invalid. Allocation-free.</summary>
    public static (int Status, int Order) Read(string? blob, ReadOnlySpan<char> showId)
    {
        if (!IsValidId(showId)) return (0, 0);
        var rest = blob.AsSpan();
        while (!rest.IsEmpty)
        {
            var entry = Next(ref rest);
            if (TryParse(entry, out var id, out int status, out int order) && id.SequenceEqual(showId)) return (status, order);
        }
        return (0, 0);
    }

    /// <summary>The blob with this show's entry FIRST (negative values stored as 0), every other valid entry after it in
    /// its old order, trimmed to <see cref="Cap"/>. An invalid id returns <paramref name="blob"/> unchanged.</summary>
    public static string Write(string? blob, ReadOnlySpan<char> showId, int status, int order)
    {
        if (!IsValidId(showId)) return blob ?? "";
        var sb = new StringBuilder((blob?.Length ?? 0) + showId.Length + 24);
        sb.Append(showId).Append(':').Append(Math.Max(0, status)).Append(':').Append(Math.Max(0, order));
        int kept = 1;
        var rest = blob.AsSpan();
        while (!rest.IsEmpty && kept < Cap)
        {
            var entry = Next(ref rest);
            if (!TryParse(entry, out var id, out _, out _) || id.SequenceEqual(showId)) continue;
            sb.Append(';').Append(entry);
            kept++;
        }
        return sb.ToString();
    }

    static ReadOnlySpan<char> Next(ref ReadOnlySpan<char> rest)
    {
        int cut = rest.IndexOf(';');
        var entry = cut < 0 ? rest : rest[..cut];
        rest = cut < 0 ? default : rest[(cut + 1)..];
        return entry;
    }

    static bool TryParse(ReadOnlySpan<char> entry, out ReadOnlySpan<char> id, out int status, out int order)
    {
        status = order = 0;
        int a = entry.IndexOf(':');
        id = a < 0 ? default : entry[..a];
        if (a <= 0) return false;
        var tail = entry[(a + 1)..];
        int b = tail.IndexOf(':');
        return b >= 0
            && int.TryParse(tail[..b], NumberStyles.None, CultureInfo.InvariantCulture, out status)
            && int.TryParse(tail[(b + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out order);
    }
}
