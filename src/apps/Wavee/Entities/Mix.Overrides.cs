// ── Entities/Mix.Overrides.cs — CORE (#48 Mix, plan §3.6a item 2, decision D11 (b)) ──────────────────────────────────
//
// What this PC holds for one pair that Spotify does not: an edit on a playlist the user cannot write ("No mix" (D16), a
// style or an overlap, "Reset to auto" over a Spotify recipe), and — since M2 — an owner's edit Spotify has not taken yet
// (offline, or the save was refused): a PENDING record, which plays here until the user's Retry saves it (the seam card
// says so and offers Retry). One record per (playlist, from item, to item). The planner consults it FIRST
// (Playback.MixPlanner). A record that is not pending says "Saved on this PC only" wherever it shows.
//
// THE STORE. The settings registry, as one string: these rows are made by hand, one pair at a time, so the set stays
// small, and the registry is the one existing local store that already persists strings across launches (plan §3.6a:
// "phase 3 picks one existing store, it does not add one"). Format: one record per line,
// `<playlist uri>|<from hex>|<to hex>|<kind>|<recipe base64 or empty>[|p]` — the trailing `p` marks a pending record (a
// reader before M2 ignores the sixth field and keeps the record as a local one). UI thread.

namespace Wavee;

public enum MixOverrideKind : byte { NoMix, Auto, Recipe }

/// <summary>One local record. <see cref="Raw"/> is the recipe bytes the record's recipe was decoded from (or patched over: the
/// Spotify recipe it replaced), so a save writes back every field Wavee does not model; empty for a recipe Wavee built.
/// <see cref="Pending"/>: an owner's edit that is waiting to be saved to Spotify (it plays here meanwhile).</summary>
public readonly record struct MixOverride(MixOverrideKind Kind, MixRecipe Recipe, byte[] Raw, bool Pending = false);

public static class MixOverrides
{
    static Dictionary<(string Playlist, ulong From, ulong To), MixOverride>? s_map;

    static Dictionary<(string, ulong, ulong), MixOverride> Map => s_map ??= Load();

    /// <summary>Bumped on every change: the meta line, the seam marks and the planner's follow read it.</summary>
    public static int Version { get; private set; }

    public static bool TryGet(string playlistUri, ulong fromItemId, ulong toItemId, out MixOverride value)
        => Map.TryGetValue((playlistUri, fromItemId, toItemId), out value);

    /// <summary>How many pairs of a playlist hold a local record (the meta line's "edited" count).</summary>
    public static int Count(string playlistUri)
    {
        int n = 0;
        foreach (var key in Map.Keys) if (key.Item1 == playlistUri) n++;
        return n;
    }

    /// <summary>Set a pair's record. <paramref name="raw"/>: the bytes of the recipe <paramref name="recipe"/> was edited from (the
    /// pair's Spotify recipe or its earlier local one), carried verbatim where the edit left them alone; empty for a new recipe.
    /// <paramref name="pending"/>: an owner's edit Spotify has not taken yet.</summary>
    public static void Set(string playlistUri, ulong fromItemId, ulong toItemId, MixOverrideKind kind, in MixRecipe recipe = default,
        byte[]? raw = null, bool pending = false)
    {
        if (playlistUri.Length == 0 || fromItemId == 0) return;
        Map[(playlistUri, fromItemId, toItemId)] = new MixOverride(kind, recipe, raw ?? [], pending);
        Save();
    }

    public static void Clear(string playlistUri, ulong fromItemId, ulong toItemId)
    {
        if (Map.Remove((playlistUri, fromItemId, toItemId))) Save();
    }

    /// <summary>Drop the records of a playlist whose items are no longer in it (called with the list's live item ids).</summary>
    public static void Prune(string playlistUri, IReadOnlySet<ulong> liveItems)
    {
        List<(string, ulong, ulong)>? gone = null;
        foreach (var key in Map.Keys)
            if (key.Item1 == playlistUri && (!liveItems.Contains(key.Item2) || (key.Item3 != 0 && !liveItems.Contains(key.Item3))))
                (gone ??= new()).Add(key);
        if (gone is null) return;
        foreach (var key in gone) Map.Remove(key);
        Save();
    }

    static Dictionary<(string, ulong, ulong), MixOverride> Load()
    {
        var map = new Dictionary<(string, ulong, ulong), MixOverride>();
        string raw = Platform.Settings.Get(Platform.Keys.MixOverrides);
        foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var f = line.Split('|');
            if (f.Length < 4 || !byte.TryParse(f[3], out byte kind) || kind > (byte)MixOverrideKind.Recipe) continue;
            ulong from = MixRecipe.Hex64(f[1]), to = MixRecipe.Hex64(f[2]);
            if (from == 0) continue;
            MixRecipe recipe = default;
            byte[] recipeRaw = [];
            if (kind == (byte)MixOverrideKind.Recipe
                && !(f.Length > 4 && MixRecipe.TryDecodeBase64(System.Text.Encoding.ASCII.GetBytes(f[4]), out recipe, out recipeRaw))) continue;
            map[(f[0], from, to)] = new MixOverride((MixOverrideKind)kind, recipe, recipeRaw, f.Length > 5 && f[5] == "p");
        }
        return map;
    }

    static void Save()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var (key, value) in Map)
        {
            sb.Append(key.Item1).Append('|').Append(key.Item2.ToString("x16")).Append('|').Append(key.Item3.ToString("x16"))
              .Append('|').Append((byte)value.Kind).Append('|');
            if (value.Kind == MixOverrideKind.Recipe)
            {
                // Patched over the bytes it came from: a recipe loaded and saved again unchanged keeps them exactly.
                var r = value.Recipe;
                sb.Append(Convert.ToBase64String(MixRecipe.EncodeToArray(in r, value.Raw)));
            }
            if (value.Pending) sb.Append("|p");
            sb.Append('\n');
        }
        Platform.Settings.Set(Platform.Keys.MixOverrides, sb.ToString());
        Version++;
    }
}
