using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;

namespace Wavee.SpotifyLive;

/// <summary>
/// The universal feed behind <see cref="CoverColorPlane"/>: Spotify's <c>getDynamicColorsByUris</c>, which grades ANY
/// cover image — album, playlist, artist, editorial card — and returns the same five roles extension kind 179 carries,
/// but for BOTH themes plus two contrast tiers, so the app does no client-side contrast math.
///
/// Wire shape (verified in the capture corpus, tmp/saz-analysis/research/05-CAPTURES-omg-all.md):
///   request   {"imageUris":["spotify:image:ab67616d0000aa54…"]}      ← spotify:image URIs, NOT https URLs
///   response  data.dynamicColors[] index-parallel with the request, each
///             { bestFit, dark|light: { encoreBaseSetTextColor, highContrast|higherContrast:
///               { backgroundBase, backgroundTintedBase, textBase, textSubdued, textBrightAccent } } }
///             each colour being { red, green, blue, alpha }.
/// This replaced <c>fetchExtractedColors</c>, which returned one hex per image and forced the app to fabricate a
/// four-slot palette out of a single dark tone.
/// </summary>
public static class CoverColorFiller
{
    /// <summary>Build the plane's filler delegate. Keys in ⇒ graded colours out, index-parallel, null where the server
    /// had nothing (the plane remembers those so a colourless cover is not re-asked on every render).</summary>
    public static Func<IReadOnlyList<string>, CancellationToken, Task<IReadOnlyList<CoverColorPlane.GradedColors?>>>
        Create(PathfinderResource pathfinder, WaveeLogger log = default)
        => async (keys, ct) =>
        {
            using var doc = await pathfinder.QueryAsync(
                PathfinderOps.GetDynamicColorsByUris, PathfinderOps.GetDynamicColorsByUrisHash,
                w =>
                {
                    w.WritePropertyName("imageUris");
                    w.WriteStartArray();
                    for (int i = 0; i < keys.Count; i++) w.WriteStringValue(CoverColorPlane.ImageUri(keys[i]));
                    w.WriteEndArray();
                },
                PathfinderClient.Platform.Desktop, ct, ttl: TimeSpan.Zero).ConfigureAwait(false);

            if (doc is null)
            {
                log.Event(WaveeLogLevel.Warning, "colors.fill.empty", "dynamic-colors query returned no document",
                    fields: [WaveeLogField.Of("images", keys.Count)]);
                return Array.Empty<CoverColorPlane.GradedColors?>();
            }
            return Parse(doc.RootElement, keys.Count);
        };

    /// <summary>Decode a <c>getDynamicColorsByUris</c> response into one entry per requested image, index-parallel with
    /// the request (the server answers positionally — there is no uri echoed in the response to match on). Entries the
    /// server omitted or graded to nothing come back null.</summary>
    public static IReadOnlyList<CoverColorPlane.GradedColors?> Parse(JsonElement root, int expected)
    {
        var results = new CoverColorPlane.GradedColors?[expected];
        var arr = Dig(Dig(root, "data"), "dynamicColors");
        if (arr.ValueKind != JsonValueKind.Array) return results;

        int n = Math.Min(expected, arr.GetArrayLength());
        for (int i = 0; i < n; i++)
        {
            var e = arr[i];
            if (e.ValueKind != JsonValueKind.Object) continue;
            var dark = SchemeAt(Dig(e, "dark"));
            var light = SchemeAt(Dig(e, "light"));
            if (dark is null && light is null) continue;
            // A light-only grading still needs a dark half (dark theme is the default surface); reuse what we have
            // rather than dropping the image back into the "unknown" bucket and re-asking forever.
            results[i] = new CoverColorPlane.GradedColors(dark ?? light!.Value, light, BestFitIsLight(e));
        }
        return results;
    }

    /// <summary>One theme half → the role set. Prefers <c>highContrast</c> (the standard grading); <c>higherContrast</c>
    /// is the accessibility-boosted tier and stands in only when the standard one is absent.</summary>
    static CoverColorPlane.Scheme? SchemeAt(JsonElement themeNode)
    {
        if (themeNode.ValueKind != JsonValueKind.Object) return null;
        var tier = Dig(themeNode, "highContrast");
        if (tier.ValueKind != JsonValueKind.Object) tier = Dig(themeNode, "higherContrast");
        if (tier.ValueKind != JsonValueKind.Object) return null;

        uint bg = Rgba(Dig(tier, "backgroundBase"));
        if (bg == 0) return null;   // no background = nothing an art placeholder can use
        return new CoverColorPlane.Scheme(
            bg,
            Rgba(Dig(tier, "backgroundTintedBase")),
            Rgba(Dig(tier, "textBase")),
            Rgba(Dig(tier, "textSubdued")),
            Rgba(Dig(tier, "textBrightAccent")));
    }

    /// <summary>Spotify's own "which theme suits this cover" hint. The corpus records the field but not its domain, so
    /// this reads the two plausible encodings (a "light"/"dark" string, or an object naming one) and defaults to dark —
    /// nothing in the app depends on it being right, it only breaks ties.</summary>
    static bool BestFitIsLight(JsonElement entry)
    {
        var bf = Dig(entry, "bestFit");
        return bf.ValueKind switch
        {
            JsonValueKind.String => bf.GetString()?.Contains("light", StringComparison.OrdinalIgnoreCase) == true,
            JsonValueKind.Object => Dig(bf, "light").ValueKind is JsonValueKind.Object or JsonValueKind.True,
            _ => false,
        };
    }

    /// <summary><c>{red,green,blue,alpha}</c> → opaque ARGB. Alpha is forced to 255: art placeholders are opaque
    /// content, and every captured sample carried alpha 255 anyway.</summary>
    static uint Rgba(JsonElement c)
    {
        if (c.ValueKind != JsonValueKind.Object) return 0;
        uint r = Byte(c, "red"), g = Byte(c, "green"), b = Byte(c, "blue");
        return 0xFF000000u | (r << 16) | (g << 8) | b;

        static uint Byte(JsonElement e, string name)
            => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetUInt32(out var v)
                ? Math.Min(v, 255u) : 0u;
    }

    static JsonElement Dig(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : default;
}
