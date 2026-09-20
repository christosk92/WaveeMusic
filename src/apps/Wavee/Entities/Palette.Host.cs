// ── Entities/Palette.Host.cs ───────────────────────────────────────────────────────────────────────────────────────
// the debounced pump, the filler, the tone plane's mount
//
// Role: SHELL
// Owner: L
// Wave: 4
// Budget: 250 lines
// Spec: DERIVED (ch 00 §9.5: the pump + filler half of CoverColorPlane 556, plus the tone plane's mount)
//
// ── THE THREE HALVES ─────────────────────────────────────────────────────────────────────────────────────────────────
//
//  1. THE PUMP. `Palette.cs` (CORE) queues a slot on every render-path MISS and calls the erased `PalettePump()` hook.
//     This file implements it as a DEBOUNCE ARM and nothing more: a grid realize produces dozens of misses in one frame
//     and they must coalesce into ONE request. The actual send rides the shell's frame tick, exactly like
//     `Fetch.Drain()` — P10: timers are named, few, and owned by the shell, and the palette is not allowed a second
//     clock of its own. A landed batch re-arms the pump for the rows behind it ONLY when no scroll is live
//     (`PublishCadence.PalettePumpAllowed`); the shell re-arms it on the scroll-end edge via `ResumeIfPending` (W2-A1).
//
//  2. THE FILLER. `getDynamicColorsByUris` grades ANY cover image and returns the same five roles extension kind 179
//     carries, but for BOTH themes plus two contrast tiers, so the app does no client-side contrast math. The decode is
//     PURE and lives here (<see cref="Palette.ParseDynamicColors"/>) where a test can drive it; the TRANSPORT is a
//     delegate the composition root installs, because the persisted-query table is `Spotify.Api`'s and owner F's — see
//     the gap note at the bottom of this file.
//
//  3. THE MOUNTS. The four cover leaves are `Platform/Design.cs`'s Components; these are the one-line factories that
//     embed them with their props and their Key, so no page ever writes `Embed.Comp(new CoverPageTonePlane.Props(…))`
//     by hand and no page ever holds a palette subscription at PAGE scope.

using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;

namespace Wavee;

public static partial class Palette
{
    // ══ 1. THE DEBOUNCED PUMP ════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>One image's full grading, as the filler returns it. <see cref="HasLight"/> is separate from
    /// <see cref="Light"/> being non-empty because a light-ONLY grading reuses its own half as the dark one (dark theme
    /// is the default surface) rather than dropping the image back into the unknown bucket and re-asking forever.</summary>
    public readonly record struct Graded(Scheme Dark, Scheme Light, bool HasLight, bool BestFitIsLight);

    /// <summary>The transport. Image uris in (<c>spotify:image:&lt;40-hex id&gt;</c> — NOT https urls), graded colours
    /// out, INDEX-PARALLEL with the request and null where the server had nothing. Installed once by the composition
    /// root; null in `--fake`, in a test and before login, which leaves a pure in-memory plane with no network at all
    /// (the read path still answers from whatever kind 179 has seeded).</summary>
    public static Func<string[], CancellationToken, Task<Graded?[]>>? Filler { get; set; }

    // The debounce, measured on the FRAME clock. `Design.FrameTime.NowMs` is the frame's predicted present time, which
    // is the same epoch every other timed decision in the app compares against; `Environment.TickCount64` would be a
    // second epoch for no gain.
    static long s_dueMs;
    static bool s_armed;
    static bool s_inFlight;

    /// <summary>Implemented here: arm the debounce. Called by `Palette.cs` on every enqueue, so a grid realize that
    /// misses forty covers in one frame arms it forty times and sends ONCE.
    /// <para>Re-arming on every miss is deliberate. A scroll that keeps producing misses keeps pushing the deadline out,
    /// so the batch that eventually goes is the one that covers the rows the user landed on rather than the ones they
    /// flew past — and <see cref="MaxQueue"/> bounds the backlog regardless.</para></summary>
    static partial void PalettePump()
    {
        s_dueMs = Design.FrameTime.NowMs + PumpDebounceMs;
        s_armed = true;
    }

    /// <summary>Bring the armed debounce due NOW. The ONE test seam in this file, and it is a seam rather than an
    /// injected clock on purpose: the debounce is a DEADLINE, so "drive time forward" is exactly "clear the deadline",
    /// and a fake clock would be a second epoch for the one value this file compares.
    /// <para>Public rather than internal because this assembly has no <c>InternalsVisibleTo</c> — the diagnostics page
    /// uses it too, to force a grading pass without waiting out the debounce.</para></summary>
    public static void ForceDue() => s_dueMs = 0;

    /// <summary>THE shell's frame tick calls this (beside `Fetch.Drain()`), and it is the only clock this layer has.
    /// Idempotent and cheap: two comparisons when nothing is due.
    /// <para>ONE request at a time. The rows stay marked <c>Queued</c> until an answer clears them, which is what stops
    /// a second batch re-asking the same covers while the first is in flight; a second concurrent request would buy
    /// latency the user cannot see and cost a second rate-limit slot on a shared endpoint.</para></summary>
    public static void Tick()
    {
        if (!s_armed || s_inFlight) return;
        if (Design.FrameTime.NowMs < s_dueMs) return;
        s_armed = false;
        if (Filler is not { } fill) { DrainUnqueued(); return; }

        Span<StringId> ids = stackalloc StringId[BatchCap];
        int n = Drain(ids);
        if (n == 0) return;

        // Resolve on the UI thread (C1) and hand the request STRINGS to the transport: the ids are alive now, and the
        // batch outlives that guarantee the moment it crosses a thread.
        var uris = new string[n];
        var keys = new StringId[n];
        for (int i = 0; i < n; i++)
        {
            keys[i] = ids[i];
            uris[i] = ImageUriFor(Entities.Strings.Resolve(ids[i]));
        }

        s_inFlight = true;
        _ = RunAsync(fill, uris, keys);
    }

    static async Task RunAsync(Func<string[], CancellationToken, Task<Graded?[]>> fill, string[] uris, StringId[] keys)
    {
        Graded?[]? answers = null;
        try { answers = await fill(uris, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception) { /* a failure is not an answer — Apply's null arm re-frees the rows */ }
        Post(() => Apply(keys, answers));
    }

    /// <summary>Land one batch. UI THREAD, like every other writer into the tables (C1).
    /// <para>A NULL entry is the server saying it has no colours for that cover: a real answer with its own (shorter)
    /// TTL, recorded so the same impossible cover is not re-asked on every launch. A null ARRAY is a transport failure,
    /// which is not an answer at all and frees the rows so the next render can re-queue them.</para></summary>
    static void Apply(StringId[] keys, Graded?[]? answers)
    {
        s_inFlight = false;
        if (answers is null) { Failed(keys); return; }
        for (int i = 0; i < keys.Length; i++)
        {
            var id = Entities.Strings.Resolve(keys[i]);
            if (i < answers.Length && answers[i] is { } g)
                SetGraded(id, g.Dark, g.Light, g.HasLight, g.BestFitIsLight);
            else
                SetNegative(id);
        }
        // An answer unqueues rows, which usually leaves more waiting behind it. Re-arm rather than wait for the next
        // render-path miss: a 200-cover grid is four batches, and nothing else would start the second one.
        //
        // NOT while a scroll is live (W2-A1). A fling misses dozens of covers per frame, so re-arming here made every
        // landed batch queue the next one AND publish — the pump was itself a per-frame publisher, and every `Changed`
        // subscriber re-rendered on every scroll frame. The rows stay queued (a render-path miss still arms the
        // debounce, and a batch in flight still lands); the shell's frame tick calls `ResumeIfPending` on the scroll-end
        // edge. The writes above sit dirty until that tick's next allowed publication, which is the same cadence the
        // UI-thread poster applies after this callback anyway. With no shell (a test, a probe) `Shell.ScrollActive` is
        // never set and both arms run as before.
        if (!PublishCadence.PalettePumpAllowed(Shell.ScrollActive)) return;
        if (Pending > 0) PalettePump();
        Entities.Publish();
    }

    /// <summary>The scroll-end edge: the shell's frame tick calls this once when `ScrollActive` falls. Re-arms the
    /// debounce for the rows <see cref="Apply"/> declined to re-arm during the scroll; nothing to do when the queue is
    /// empty or a batch is already in flight (its landing re-arms, now that the gate is open).</summary>
    public static void ResumeIfPending()
    {
        if (Pending > 0 && !s_inFlight) PalettePump();
    }

    /// <summary>No transport installed: empty the queue rather than let it sit at <see cref="MaxQueue"/> forever, which
    /// would make every later miss a silent no-op once the backlog filled. Nothing is written, so a cover stays
    /// ungraded and simply re-queues on its next render — which is exactly the `--fake` / offline behaviour.</summary>
    static void DrainUnqueued()
    {
        Span<StringId> ids = stackalloc StringId[BatchCap];
        int n = Drain(ids);
        if (n > 0) Failed(ids[..n]);
    }

    /// <summary>The UI-thread post. The composition root installs the host's dispatcher; with none, the callback runs
    /// INLINE — which is correct for a test whose "transport" completed synchronously and is the only reason a palette
    /// test needs no shell.</summary>
    public static Action<Action> Post { get; set; } = static a => a();

    // ══ 2. THE FILLER'S DECODE (pure) ════════════════════════════════════════════════════════════════════════════════
    //
    // Wire shape, verified in the capture corpus:
    //   request   {"imageUris":["spotify:image:ab67616d0000aa54…"]}      ← spotify:image uris, NOT https urls
    //   response  data.dynamicColors[] INDEX-PARALLEL with the request, each
    //             { bestFit, dark|light: { highContrast|higherContrast:
    //               { backgroundBase, backgroundTintedBase, textBase, textSubdued, textBrightAccent } } }
    //             each colour being { red, green, blue, alpha }.
    // This replaced an older endpoint that returned ONE hex per image and forced the app to fabricate a four-slot
    // palette out of a single dark tone.

    /// <summary>Decode a <c>getDynamicColorsByUris</c> response into one entry per REQUESTED image, index-parallel with
    /// the request — the server answers POSITIONALLY, and there is no uri echoed in the response to match on. Entries
    /// the server omitted or graded to nothing come back null.
    /// <para>Pure, so the whole filler is testable against a captured document without a socket.</para></summary>
    public static Graded?[] ParseDynamicColors(JsonElement root, int expected)
    {
        var results = new Graded?[expected];
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
            results[i] = new Graded(dark ?? light!.Value, light ?? default, light is not null, BestFitIsLight(e));
        }
        return results;
    }

    /// <summary>One theme half → the role set. Prefers <c>highContrast</c> (the standard grading); <c>higherContrast</c>
    /// is the accessibility-boosted tier and stands in only when the standard one is absent.</summary>
    static Scheme? SchemeAt(JsonElement themeNode)
    {
        if (themeNode.ValueKind != JsonValueKind.Object) return null;
        var tier = Dig(themeNode, "highContrast");
        if (tier.ValueKind != JsonValueKind.Object) tier = Dig(themeNode, "higherContrast");
        if (tier.ValueKind != JsonValueKind.Object) return null;

        uint bg = Rgba(Dig(tier, "backgroundBase"));
        if (bg == 0) return null;   // no background = nothing an art placeholder can use
        return new Scheme(
            bg,
            Rgba(Dig(tier, "backgroundTintedBase")),
            Rgba(Dig(tier, "textBase")),
            Rgba(Dig(tier, "textSubdued")),
            Rgba(Dig(tier, "textBrightAccent")));
    }

    /// <summary>The provider's own "which theme suits this cover" hint. The corpus records the field but not its
    /// domain, so this reads the two plausible encodings (a "light"/"dark" string, or an object naming one) and defaults
    /// to dark — nothing in the app depends on it being right; it only breaks ties.</summary>
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

    /// <summary><c>{red,green,blue,alpha}</c> → opaque ARGB. Alpha is FORCED to 255: art placeholders are opaque
    /// content, and every captured sample carried 255 anyway.</summary>
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

    // ══ 3. THE MOUNTS ════════════════════════════════════════════════════════════════════════════════════════════════
    //
    // The four cover leaves, embedded with their props and their Key. Watches that must NOT sit in a page's Render():
    // a graded batch would otherwise re-render the whole Artist/Detail/Home tree, including every shelf in it.
    //
    // Every one of these takes a caller-supplied `key`. A leaf's identity is the SURFACE it belongs to, not the cover
    // it happens to be showing — a page that re-keyed on the url would remount (and therefore re-fade) its own ground
    // every time the hero swapped covers, which is the opposite of the 250 ms cross-fade the bound Fill already gives.

    /// <summary>THE detail page's ground: one flat, translucent art-derived plane behind both hero arms.
    /// <para>Mount it as a page-root SIBLING of the scrolling page, never as a child of it.</para></summary>
    public static Element PageTonePlane(string? url, string? fallbackUrl, bool disabled,
                                        float backdropBand, float pageHeight, bool heroOnly, string key,
                                        uint payloadAccent = 0)
        => Embed.Comp(new CoverPageTonePlane.Props(url, fallbackUrl, disabled, backdropBand, pageHeight, heroOnly, payloadAccent),
                      static () => new CoverPageTonePlane()) with { Key = key };

    /// <summary>The artist page's blend wash. <paramref name="height"/> / <paramref name="boundary"/> come from the
    /// caller's own hero layout. <paramref name="payloadAccent"/> is the header row's raw accent (the ladder's payload
    /// rung) — 0 when the caller has none.</summary>
    public static Element ArtistBlendWash(string? url, float height, float boundary, bool disabled, string key,
                                           uint payloadAccent = 0)
        => Embed.Comp(new CoverArtistBlendWash.Props(url, height, boundary, disabled, payloadAccent),
                      static () => new CoverArtistBlendWash()) with { Key = key };

    /// <summary>The full-bleed artist hero veil over photography. <paramref name="payloadAccent"/> is the header row's
    /// raw accent (the ladder's payload rung) — 0 when the caller has none.</summary>
    public static Element ArtistHeroVeil(string? url, bool vertical, float width, float height, string key,
                                          uint payloadAccent = 0)
        => Embed.Comp(new CoverKeyedVeil.Props(url, vertical, width, height, payloadAccent),
                      static () => new CoverKeyedVeil()) with { Key = key };

    /// <summary>Publishes the page-scoped shell-material tint when THIS cover is graded — with no page re-render. Every
    /// detail page must mount one, or the window chrome stays neutral while the page is coloured, which is the single
    /// loudest visual regression available here.</summary>
    public static Element ShellTint(string? url, bool ready, bool disabled, bool apply, object owner,
                                    Signal<ShellMaterialState>? slot, string key, string? fallbackUrl = null,
                                    uint payloadAccent = 0)
        => Embed.Comp(new CoverShellTintBinder.Props(url, fallbackUrl, ready, disabled, apply, owner, slot, payloadAccent),
                      static () => new CoverShellTintBinder()) with { Key = key };
}

// ── WHAT IS NOT HERE, AND WHY ─────────────────────────────────────────────────────────────────────────────────────────
//
// · THE PERSISTED QUERY. `Spotify.Api.Queries.DynamicColors` (`getDynamicColorsByUris`) is the in-tree transport;
//   `Shell.Host.cs` installs it as <see cref="Palette.Filler"/> (`Spotify.Api.GradeCovers`) once the session is online,
//   so both themes now grade from the same batch — the plane is no longer kind-179-only.
//
// · PERSISTENCE. 0.2.9 wrote `<LocalCache>/cover-colors.json` with a 1 500 ms flush debounce and a `Prewarm()` off the
//   thread at startup. 0.3 instead uses a `palette` table in the SQLite store (`Entities/Store.Palette.cs`,
//   `Entities/PalettePersistence.cs`): dirty slots are write-behind flushed on the shell's frame tick beside the pump,
//   and `WarmPaletteCore` restores fresh rows on boot before the first page renders. No second file-based cache — the
//   store IS the persistence now, exactly as the 0.3 decision records.
