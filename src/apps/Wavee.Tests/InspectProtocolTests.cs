// ── Wavee.Tests/InspectProtocolTests.cs — the `--inspect` grammar, argv, key names and reply shapes ─────────────────────────
//
// Pins the pure half of the agent server (`Screens/Diagnostics.Inspect.Protocol.cs`): what a JSON or text line parses to, the
// argv the arm accepts, the key-name table, and the JSON shapes of the replies (one line each, id echoed). No engine, no thread.

using System.Text.Json;
using FluentGpu.Foundation;
using Xunit;
using I = Wavee.Diagnostics.Inspect;

namespace Wavee.Tests;

public class InspectProtocolTests
{
    static I.Command Parse(string line)
    {
        Assert.True(I.TryParse(line, out I.Command cmd, out string error), error);
        return cmd;
    }

    // ── the grammar ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_json_line_with_an_args_object_parses_its_verb_id_and_named_args()
    {
        var cmd = Parse("""{"cmd":"mount","args":{"w":400,"h":860,"theme":"zune","kind":"light","route":"home"},"id":7}""");
        Assert.Equal(I.Verb.Mount, cmd.Verb);
        Assert.Equal("mount", cmd.Name);
        Assert.Equal(7, cmd.Id);
        Assert.Equal(400f, cmd.Args.Float("w", 0f));
        Assert.Equal(860, cmd.Args.Int("h", 0));
        Assert.Equal("zune", cmd.Args.Str("theme"));
        Assert.Equal("light", cmd.Args.Str("kind"));
        Assert.Equal("home", cmd.Args.Str("route"));
        Assert.False(cmd.Args.Has("scale"));
    }

    [Fact]
    public void Args_may_sit_beside_cmd_and_id_at_the_top_level()
    {
        var cmd = Parse("""{"cmd":"click","key":"zune-band-pivot:home","settle":false,"id":3}""");
        Assert.Equal(I.Verb.Click, cmd.Verb);
        Assert.Equal("zune-band-pivot:home", cmd.Args.Str("key"));
        Assert.False(cmd.Args.Bool("settle", true));
        Assert.False(cmd.Args.Has("id"));
        Assert.False(cmd.Args.Has("cmd"));
    }

    [Fact]
    public void A_headless_style_args_array_fills_the_verbs_positional_names()
    {
        var cmd = Parse("""{"cmd":"resize","args":[800,600],"id":1}""");
        Assert.Equal(800f, cmd.Args.Float("w", 0f));
        Assert.Equal(600f, cmd.Args.Float("h", 0f));
        Assert.False(I.TryParse("""{"cmd":"frames","args":[1,2]}""", out _, out string error));
        Assert.Contains("too many", error);
    }

    [Fact]
    public void A_text_line_takes_named_pairs_and_positional_words()
    {
        var cmd = Parse("mount 400 860 theme=zune scale=1.5");
        Assert.Equal(I.Verb.Mount, cmd.Verb);
        Assert.Equal(-1, cmd.Id);
        Assert.Equal(400f, cmd.Args.Float("w", 0f));
        Assert.Equal(860f, cmd.Args.Float("h", 0f));
        Assert.Equal(1.5f, cmd.Args.Float("scale", 1f));
        Assert.Equal("zune", cmd.Args.Str("theme"));

        var go = Parse("goto album:spotify:album:4aawyAB9vmqN3uQ7FjRGTy");
        Assert.Equal(I.Verb.GoTo, go.Verb);
        Assert.Equal("album:spotify:album:4aawyAB9vmqN3uQ7FjRGTy", go.Args.Str("route"));
    }

    [Fact]
    public void Type_keeps_the_rest_of_the_line_with_its_spaces()
    {
        var cmd = Parse("type hello  world");
        Assert.Equal(I.Verb.Type, cmd.Verb);
        Assert.Equal("hello  world", cmd.Args.Str("text"));
    }

    [Fact]
    public void Nested_objects_are_kept_as_raw_json()
    {
        var cmd = Parse("""{"cmd":"dump","args":{"root":"x","filter":{"kind":"Text"}}}""");
        Assert.True(cmd.Args.TryGet("filter", out I.Value v));
        Assert.Equal(I.ValueKind.Json, v.Kind);
        using var doc = JsonDocument.Parse(v.Str);
        Assert.Equal("Text", doc.RootElement.GetProperty("kind").GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("# a comment")]
    public void Blank_and_comment_lines_are_no_command(string line)
    {
        Assert.True(I.TryParse(line, out I.Command cmd, out _));
        Assert.True(cmd.IsNone);
    }

    [Theory]
    [InlineData("""{"cmd":"explode","id":9}""", 9, "unknown command")]
    [InlineData("""{"args":{},"id":4}""", 4, "\"cmd\"")]
    [InlineData("""{"cmd":"mount","args":5,"id":2}""", 2, "\"args\"")]
    [InlineData("""[1,2]""", -1, "object")]
    [InlineData("""{"cmd":"mount",""", -1, "bad JSON")]
    [InlineData("""teleport now""", -1, "unknown command")]
    [InlineData("""frames 1 2""", -1, "unexpected")]
    public void A_bad_line_fails_with_a_reason_and_keeps_its_id(string line, int id, string fragment)
    {
        Assert.False(I.TryParse(line, out I.Command cmd, out string error));
        Assert.Equal(id, cmd.Id);
        Assert.Contains(fragment, error);
    }

    [Fact]
    public void The_state_verb_takes_its_kind_by_name_or_position()
    {
        Assert.Equal(I.Verb.State, I.VerbOf("state"));
        Assert.Contains("state", I.VerbNames);
        var named = Parse("""{"cmd":"state","args":{"kind":"empty"},"id":3}""");
        Assert.Equal(I.Verb.State, named.Verb);
        Assert.Equal("empty", named.Args.Str("kind"));
        var text = Parse("state error");
        Assert.Equal(I.Verb.State, text.Verb);
        Assert.Equal("error", text.Args.Str("kind"));
    }

    [Fact]
    public void Every_listed_verb_name_parses_to_a_verb()
    {
        foreach (string name in I.VerbNames) Assert.NotEqual(I.Verb.None, I.VerbOf(name));
        Assert.Equal(I.Verb.GoTo, I.VerbOf("navigate"));
        Assert.Equal(I.Verb.Status, I.VerbOf("ping"));
        Assert.Equal(I.Verb.None, I.VerbOf("render2"));
    }

    [Fact]
    public void Bool_args_read_words_and_numbers()
    {
        var cmd = Parse("dump visibleOnly=false keysOnly=1 text=off");
        Assert.False(cmd.Args.Bool("visibleOnly", true));
        Assert.True(cmd.Args.Bool("keysOnly", false));
        Assert.False(cmd.Args.Bool("text", true));
        Assert.True(cmd.Args.Bool("absent", true));
    }

    // ── the argv ────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_argv_takes_pipe_profile_and_text()
    {
        Assert.True(I.Options.TryParse(["--inspect", "--pipe", "wv", "--profile", @"C:\scratch\p", "--text", "headless"], out var o, out _));
        Assert.Equal("wv", o.Pipe);
        Assert.Equal(@"C:\scratch\p", o.Profile);
        Assert.True(o.HeadlessText);

        Assert.True(I.Options.TryParse(["--inspect"], out var plain, out _));
        Assert.Equal("", plain.Pipe);
        Assert.Equal("", plain.Profile);
        Assert.False(plain.HeadlessText);
    }

    [Theory]
    [InlineData("--pipe")]
    [InlineData("--text", "gdi")]
    [InlineData("--spotify-login")]
    [InlineData("--profile", "--pipe")]
    public void A_bad_argv_is_a_usage_error(params string[] extra)
    {
        string[] args = ["--inspect", .. extra];
        Assert.False(I.Options.TryParse(args, out _, out string usage));
        Assert.Contains("usage: Wavee.exe --inspect", usage);
    }

    [Fact]
    public void The_users_profile_is_refused_and_a_temp_profile_is_owned()
    {
        string user = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wavee");
        Assert.False(Diagnostics.InspectHost.TryProfile(user, out _, out _, out string why));
        Assert.Contains("scratch", why);
        Assert.False(Diagnostics.InspectHost.TryProfile(Path.Combine(user, "sub"), out _, out _, out _));

        Assert.True(Diagnostics.InspectHost.TryProfile("", out string dir, out bool owned, out _));
        Assert.True(owned);
        Assert.StartsWith(Path.GetTempPath(), dir, StringComparison.OrdinalIgnoreCase);
    }

    // ── key names ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("enter", 0x0D, KeyModifiers.None)]
    [InlineData("Esc", 0x1B, KeyModifiers.None)]
    [InlineData("down", 0x28, KeyModifiers.None)]
    [InlineData("f5", 0x74, KeyModifiers.None)]
    [InlineData("a", 0x41, KeyModifiers.None)]
    [InlineData("7", 0x37, KeyModifiers.None)]
    [InlineData("ctrl+shift+f", 0x46, KeyModifiers.Ctrl | KeyModifiers.Shift)]
    [InlineData("alt+left", 0x25, KeyModifiers.Alt)]
    [InlineData("13", 13, KeyModifiers.None)]
    public void Key_names_resolve_to_virtual_keys(string spec, int vk, KeyModifiers mods)
    {
        Assert.True(I.TryKey(spec, out int got, out KeyModifiers gotMods));
        Assert.Equal(vk, got);
        Assert.Equal(mods, gotMods);
    }

    [Theory]
    [InlineData("")]
    [InlineData("hyper+a")]
    [InlineData("f13")]
    [InlineData("nope")]
    public void Unknown_keys_are_refused(string spec) => Assert.False(I.TryKey(spec, out _, out _));

    // ── the reply shapes ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void An_ok_reply_is_one_line_with_id_ok_cmd_and_the_body()
    {
        string line = I.Reply.Ok(5, "tiers", w =>
        {
            w.WriteString("theme", "zune");
            I.Reply.Rect(w, "rect", new RectF(1.234f, 2f, 3.456f, float.NaN));
        });
        Assert.DoesNotContain("\n", line);
        using var doc = JsonDocument.Parse(line);
        JsonElement r = doc.RootElement;
        Assert.Equal(5, r.GetProperty("id").GetInt32());
        Assert.True(r.GetProperty("ok").GetBoolean());
        Assert.Equal("tiers", r.GetProperty("cmd").GetString());
        Assert.Equal("zune", r.GetProperty("theme").GetString());
        JsonElement rect = r.GetProperty("rect");
        Assert.Equal(1.23, rect.GetProperty("x").GetDouble(), 3);
        Assert.Equal(3.46, rect.GetProperty("w").GetDouble(), 3);
        Assert.Equal(0, rect.GetProperty("h").GetDouble());   // NaN is not JSON
    }

    [Fact]
    public void A_fail_reply_carries_the_error_and_any_extra_fields()
    {
        string line = I.Reply.Fail(9, "render", "offscreen capture unavailable", w => w.WriteString("why", "no WARP"));
        using var doc = JsonDocument.Parse(line);
        JsonElement r = doc.RootElement;
        Assert.Equal(9, r.GetProperty("id").GetInt32());
        Assert.False(r.GetProperty("ok").GetBoolean());
        Assert.Equal("render", r.GetProperty("cmd").GetString());
        Assert.Equal("offscreen capture unavailable", r.GetProperty("error").GetString());
        Assert.Equal("no WARP", r.GetProperty("why").GetString());
    }

    [Fact]
    public void An_event_line_names_its_kind()
    {
        using var doc = JsonDocument.Parse(I.Reply.Event("ready", w => w.WriteString("text", "dwrite")));
        Assert.Equal("ready", doc.RootElement.GetProperty("kind").GetString());
        Assert.Equal("dwrite", doc.RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public void Colours_write_as_rgba_hex()
    {
        Assert.Equal("#FF000080", I.Reply.Hex(new ColorF(1f, 0f, 0f, 0.5f)));
        Assert.Equal("#00000000", I.Reply.Hex(default));
    }

    [Fact]
    public void Text_escapes_stay_readable()
    {
        string line = I.Reply.Ok(1, "find", w => w.WriteString("text", "Liked Songs · “quoted”"));
        Assert.Contains("Liked Songs · “quoted”", line);
    }
}
