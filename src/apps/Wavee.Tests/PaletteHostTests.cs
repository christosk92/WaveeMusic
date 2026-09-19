// ── Wavee.Tests/PaletteHostTests.cs — the filler's decode and the debounced pump ─────────────────────────────────────
//
// Wave 4's gate for `Entities/Palette.Host.cs` (owner L). Two halves:
//   · the `getDynamicColorsByUris` DECODE — pure, driven against hand-built documents in the captured wire shape
//     (ported from 0.2.9's `CoverColorPlaneTests` filler facts): index-parallel answers, the light-only fallback, the
//     contrast-tier preference, the best-fit encodings, forced-opaque colours;
//   · the PUMP — a render-path miss arms the debounce, the frame tick sends ONE batch through the installed filler, and
//     the answer lands in the CORE table. The transport here is a synchronous fake and the UI-thread post is the inline
//     default, so no socket, no thread and no engine loop is involved.
//
// The palette table and its demand queue are PROCESS-WIDE by design, so this class joins the entities collection, mints
// its own image ids, drains any queue a sibling left behind, and puts the transport seam back when it is done.

using System.Text.Json;
using System.Threading.Tasks;
using FluentGpu.Foundation;
using Wavee;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public sealed class PaletteHostTests : IDisposable
{
    public PaletteHostTests()
    {
        TestScope.Fresh();
        DrainQueue();
    }

    public void Dispose()
    {
        Palette.Filler = null;
        Palette.Post = static a => a();
        DrainQueue();
    }

    static void DrainQueue()
    {
        Span<StringId> buf = stackalloc StringId[Palette.BatchCap];
        while (Palette.Pending > 0) Palette.Drain(buf);
    }

    static string Id(string tail24) => "ab67616d0000b273" + tail24;
    static string Url(string id) => "https://i.scdn.co/image/" + id;

    static string Rgb(int r, int g, int b) => $"{{\"red\":{r},\"green\":{g},\"blue\":{b},\"alpha\":255}}";

    static string Tier(int r, int g, int b) =>
        $"{{\"backgroundBase\":{Rgb(r, g, b)},\"backgroundTintedBase\":{Rgb(r + 5, g + 5, b + 5)}," +
        $"\"textBase\":{Rgb(255, 255, 255)},\"textSubdued\":{Rgb(180, 180, 180)},\"textBrightAccent\":{Rgb(255, 255, 255)}}}";

    static JsonElement Doc(string entries)
    {
        using var doc = JsonDocument.Parse("{\"data\":{\"dynamicColors\":[" + entries + "]}}");
        return doc.RootElement.Clone();
    }

    // ══ the decode ═══════════════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Answers_are_index_parallel_with_the_request()
    {
        // The server answers POSITIONALLY — there is no uri echoed to match on — so entry i IS request i.
        var root = Doc(
            "{\"dark\":{\"highContrast\":" + Tier(20, 60, 30) + "}}," +
            "{\"dark\":{\"highContrast\":" + Tier(90, 20, 20) + "}}");
        var answers = Palette.ParseDynamicColors(root, expected: 2);
        Assert.Equal(2, answers.Length);
        Assert.Equal(0xFF143C1Eu, answers[0]!.Value.Dark.BackgroundBase);
        Assert.Equal(0xFF5A1414u, answers[1]!.Value.Dark.BackgroundBase);
    }

    [Fact]
    public void An_omitted_or_empty_entry_is_null_and_the_array_keeps_its_length()
    {
        var root = Doc("{},null");
        var answers = Palette.ParseDynamicColors(root, expected: 3);
        Assert.Equal(3, answers.Length);
        Assert.All(answers, a => Assert.Null(a));
    }

    [Fact]
    public void A_document_with_no_array_answers_nothing_rather_than_throwing()
    {
        using var doc = JsonDocument.Parse("{\"errors\":[{\"message\":\"boom\"}]}");
        var answers = Palette.ParseDynamicColors(doc.RootElement, expected: 2);
        Assert.Equal(2, answers.Length);
        Assert.All(answers, a => Assert.Null(a));
    }

    [Fact]
    public void A_light_only_grading_reuses_its_own_half_as_the_dark_one()
    {
        // Dark theme is the default surface. Dropping the image back into the unknown bucket would re-ask forever.
        var root = Doc("{\"light\":{\"highContrast\":" + Tier(240, 235, 230) + "}}");
        var g = Palette.ParseDynamicColors(root, 1)[0]!.Value;
        Assert.True(g.HasLight);
        Assert.Equal(g.Light.BackgroundBase, g.Dark.BackgroundBase);
    }

    [Fact]
    public void A_dark_only_grading_has_no_light_half()
    {
        var g = Palette.ParseDynamicColors(Doc("{\"dark\":{\"highContrast\":" + Tier(20, 60, 30) + "}}"), 1)[0]!.Value;
        Assert.False(g.HasLight);
        Assert.True(g.Light.IsEmpty);
    }

    [Fact]
    public void The_standard_tier_wins_and_the_boosted_tier_only_stands_in()
    {
        var both = Doc("{\"dark\":{\"highContrast\":" + Tier(20, 60, 30) + ",\"higherContrast\":" + Tier(1, 2, 3) + "}}");
        Assert.Equal(0xFF143C1Eu, Palette.ParseDynamicColors(both, 1)[0]!.Value.Dark.BackgroundBase);

        var boostedOnly = Doc("{\"dark\":{\"higherContrast\":" + Tier(1, 2, 3) + "}}");
        Assert.Equal(0xFF010203u, Palette.ParseDynamicColors(boostedOnly, 1)[0]!.Value.Dark.BackgroundBase);
    }

    [Fact]
    public void A_half_with_no_background_is_nothing_a_placeholder_can_use()
    {
        var root = Doc("{\"dark\":{\"highContrast\":{\"textBase\":" + Rgb(255, 255, 255) + "}}}");
        Assert.Null(Palette.ParseDynamicColors(root, 1)[0]);
    }

    [Fact]
    public void Colours_are_forced_opaque_and_channels_clamp_to_a_byte()
    {
        var root = Doc("{\"dark\":{\"highContrast\":{\"backgroundBase\":{\"red\":300,\"green\":0,\"blue\":0,\"alpha\":10}}}}");
        var g = Palette.ParseDynamicColors(root, 1)[0]!.Value;
        Assert.Equal(0xFFFF0000u, g.Dark.BackgroundBase);
    }

    [Theory]
    [InlineData("\"light\"", true)]
    [InlineData("\"LIGHT_THEME\"", true)]
    [InlineData("\"dark\"", false)]
    [InlineData("{\"light\":{}}", true)]
    [InlineData("{\"light\":true}", true)]
    [InlineData("{\"dark\":{}}", false)]
    [InlineData("42", false)]
    public void The_best_fit_hint_reads_both_plausible_encodings_and_defaults_to_dark(string bestFit, bool light)
    {
        var root = Doc("{\"bestFit\":" + bestFit + ",\"dark\":{\"highContrast\":" + Tier(20, 60, 30) + "}}");
        Assert.Equal(light, Palette.ParseDynamicColors(root, 1)[0]!.Value.BestFitIsLight);
    }

    // ══ the pump ═════════════════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void A_miss_is_queued_and_nothing_is_sent_before_the_debounce_is_due()
    {
        int calls = 0;
        Palette.Filler = (uris, _) => { calls++; return Task.FromResult(new Palette.Graded?[uris.Length]); };
        string url = Url(Id("ab0000000000000000000001"));

        Assert.False(Palette.TryScheme(url, lightTheme: false, out _));   // rendering the art IS the request
        Assert.True(Palette.Pending > 0);
        Palette.Tick();                                                   // the debounce window has not elapsed
        Assert.Equal(0, calls);
    }

    [Fact]
    public void A_grid_realize_of_misses_goes_out_as_ONE_batch()
    {
        // Dozens of misses in one frame must coalesce into one request — that is the whole point of the debounce.
        var batches = new List<string[]>();
        Palette.Filler = (uris, _) => { batches.Add(uris); return Task.FromResult(new Palette.Graded?[uris.Length]); };
        for (int i = 0; i < 12; i++)
            Palette.TryScheme(Url(Id("ab00000000000000000001" + i.ToString("x2"))), false, out _);

        Palette.ForceDue();
        Palette.Tick();
        Assert.Single(batches);
        Assert.Equal(12, batches[0].Length);
        // The transport is handed spotify:image uris, never https urls.
        Assert.All(batches[0], u => Assert.StartsWith("spotify:image:", u));
    }

    [Fact]
    public void An_answer_lands_in_the_table_and_the_next_read_hits()
    {
        var graded = new Palette.Graded(
            Dark: new Scheme(0xFF143C1E, 0xFF19411F, 0xFFFFFFFF, 0xFFB4B4B4, 0xFFFFFFFF),
            Light: new Scheme(0xFFF0EBE6, 0xFFF5F0EB, 0xFF000000, 0xFF505050, 0xFF000000),
            HasLight: true, BestFitIsLight: false);
        Palette.Filler = (uris, _) => Task.FromResult(new Palette.Graded?[] { graded });
        string url = Url(Id("ab0000000000000000000abc"));

        Assert.False(Palette.TryScheme(url, lightTheme: true, out _));
        Palette.ForceDue();
        Palette.Tick();

        Assert.True(Palette.TryScheme(url, lightTheme: true, out var light));
        Assert.Equal(graded.Light.BackgroundBase, light.BackgroundBase);
        Assert.True(Palette.TryScheme(url, lightTheme: false, out var dark));
        Assert.Equal(graded.Dark.BackgroundBase, dark.BackgroundBase);
    }

    [Fact]
    public void A_null_answer_is_a_real_answer_and_is_not_re_asked()
    {
        // "The server has no colours for this cover" lands on the short miss TTL, so the same impossible cover is not
        // queued again on the next render.
        Palette.Filler = (uris, _) => Task.FromResult(new Palette.Graded?[uris.Length]);
        string url = Url(Id("ab0000000000000000000def"));
        Palette.TryScheme(url, false, out _);
        Palette.ForceDue();
        Palette.Tick();

        DrainQueue();
        Assert.False(Palette.TryScheme(url, false, out _));
        Assert.Equal(0, Palette.Pending);
    }

    [Fact]
    public void A_transport_failure_is_not_an_answer_and_the_cover_can_be_asked_again()
    {
        Palette.Filler = (_, _) => Task.FromException<Palette.Graded?[]>(new InvalidOperationException("offline"));
        string url = Url(Id("ab0000000000000000000fed"));
        Palette.TryScheme(url, false, out _);
        Palette.ForceDue();
        Palette.Tick();

        // The row was freed, so the next render re-queues it rather than leaving it silently stuck.
        Assert.False(Palette.TryScheme(url, false, out _));
        Assert.True(Palette.Pending > 0);
    }

    [Fact]
    public void With_no_transport_the_queue_is_emptied_rather_than_left_to_fill_up()
    {
        // `--fake`, offline, before login: a queue that sat at its cap would turn every later miss into a silent no-op.
        Palette.Filler = null;
        Palette.TryScheme(Url(Id("ab0000000000000000000123")), false, out _);
        Assert.True(Palette.Pending > 0);
        Palette.ForceDue();
        Palette.Tick();
        Assert.Equal(0, Palette.Pending);
    }

    [Fact]
    public void An_ungradeable_image_is_never_queued()
    {
        // A mosaic tile or a custom cover has no 40-hex id the endpoint could answer about; queueing it would be a
        // request that can never be made.
        Palette.TryScheme("https://mosaic.scdn.co/640/abc", false, out _);
        Assert.Equal(0, Palette.Pending);
    }
}
