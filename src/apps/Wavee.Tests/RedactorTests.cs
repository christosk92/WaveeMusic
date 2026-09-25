// ── Wavee.Tests/RedactorTests.cs — `Redactor` (§4, §7.3) ────────────────────────────────────────────────────────────
//
// Unit 3's own tests, per §7: no source-text reads — every assertion instantiates `Redactor` directly against
// literal header/JSON/url fixtures, never by reading production source as text.

using System.Text;
using Xunit;

namespace Wavee.Tests;

public class RedactorTests
{
    // ── RedactHeaderValue ───────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Authorization")]
    [InlineData("authorization")]
    [InlineData("Cookie")]
    [InlineData("Set-Cookie")]
    [InlineData("set-cookie")]
    [InlineData("client-token")]
    [InlineData("Client-Token")]
    [InlineData("Proxy-Authorization")]
    public void Sensitive_header_names_are_redacted_length_preserving(string headerName)
    {
        const string value = "Bearer abcdefghijklmnop"; // 23 chars, all ASCII
        var redacted = Redactor.RedactHeaderValue(headerName, value);

        Assert.NotNull(redacted);
        Assert.NotEqual(value, redacted);
        Assert.Equal("[redacted " + Encoding.UTF8.GetByteCount(value) + "B]", redacted);
        Assert.DoesNotContain("abcdefghijklmnop", redacted);
    }

    [Fact]
    public void Authorization_bearer_token_example_from_the_plan()
    {
        string value = "Bearer " + new string('a', 34); // 41 bytes total, matching §7's own worked example
        var redacted = Redactor.RedactHeaderValue("Authorization", value);
        Assert.Equal("[redacted 41B]", redacted);
    }

    [Theory]
    [InlineData("App-Platform")]
    [InlineData("Spotify-App-Version")]
    [InlineData("User-Agent")]
    [InlineData("If-None-Match")]
    [InlineData("Content-Type")]
    public void Non_sensitive_header_names_pass_through_unchanged_same_reference(string headerName)
    {
        string value = "some-ordinary-value";
        var redacted = Redactor.RedactHeaderValue(headerName, value);
        Assert.Same(value, redacted); // zero-alloc on the clean path: the SAME reference comes back
    }

    [Fact]
    public void Null_header_value_stays_null()
    {
        Assert.Null(Redactor.RedactHeaderValue("Authorization", null));
    }

    [Fact]
    public void Empty_sensitive_header_value_redacts_to_zero_bytes()
    {
        Assert.Equal("[redacted 0B]", Redactor.RedactHeaderValue("Cookie", ""));
    }

    [Fact]
    public void Non_ascii_header_value_length_is_utf8_bytes_not_char_count()
    {
        string value = "Bearer ééé"; // 3 chars beyond "Bearer ", each 2 UTF-8 bytes
        var redacted = Redactor.RedactHeaderValue("Authorization", value);
        Assert.Equal("[redacted " + Encoding.UTF8.GetByteCount(value) + "B]", redacted);
    }

    // ── RedactQueryString ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Query_string_tokens_are_redacted_keeping_scheme_host_and_path()
    {
        string url = "https://api.spotify.com/v1/me?access_token=abcdefgh&sig=xyz";
        var redacted = Redactor.RedactQueryString(url);

        Assert.NotNull(redacted);
        Assert.StartsWith("https://api.spotify.com/v1/me?", redacted);
        Assert.DoesNotContain("abcdefgh", redacted);
        Assert.DoesNotContain("xyz", redacted);
        string query = url["https://api.spotify.com/v1/me?".Length..];
        Assert.Equal("https://api.spotify.com/v1/me?[redacted " + Encoding.UTF8.GetByteCount(query) + "B]", redacted);
    }

    [Fact]
    public void Url_with_no_query_string_passes_through_unchanged_same_reference()
    {
        string url = "https://api.spotify.com/v1/me";
        Assert.Same(url, Redactor.RedactQueryString(url));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Null_or_empty_url_passes_through(string? url)
    {
        Assert.Equal(url, Redactor.RedactQueryString(url));
    }

    [Fact]
    public void A_bare_trailing_question_mark_redacts_to_zero_bytes()
    {
        Assert.Equal("https://x/y?[redacted 0B]", Redactor.RedactQueryString("https://x/y?"));
    }

    // ── RedactJsonBody ──────────────────────────────────────────────────────────────────────────────────────────

    private static string RoundTrip(string json)
    {
        var utf8 = Encoding.UTF8.GetBytes(json);
        Span<byte> into = new byte[utf8.Length + 256];
        int n = Redactor.RedactJsonBody(utf8, into);
        Assert.True(n >= 0, "RedactJsonBody unexpectedly refused well-formed JSON");
        return Encoding.UTF8.GetString(into[..n]);
    }

    private static string Marker(string original) => "[redacted " + Encoding.UTF8.GetByteCount(original) + "B]";

    [Fact]
    public void Access_token_value_is_redacted_sibling_fields_untouched()
    {
        string json = """{"access_token":"abc.def.ghi","token_type":"Bearer","expires_in":3600}""";
        string result = RoundTrip(json);

        Assert.DoesNotContain("abc.def.ghi", result);
        Assert.Contains("\"token_type\":\"Bearer\"", result);
        Assert.Contains("\"expires_in\":3600", result);
        Assert.Contains("\"access_token\":\"" + Marker("abc.def.ghi") + "\"", result);
    }

    [Theory]
    [InlineData("refresh_token")]
    [InlineData("client_token")]
    [InlineData("id_token")]
    [InlineData("auth_token")]
    [InlineData("password")]
    public void Every_known_sensitive_field_name_is_redacted(string field)
    {
        const string secretValue = "super-secret-value";
        string json = "{\"" + field + "\":\"" + secretValue + "\",\"keep\":\"me\"}";
        string result = RoundTrip(json);

        Assert.DoesNotContain(secretValue, result);
        Assert.Contains("\"keep\":\"me\"", result);
        Assert.Contains("\"" + field + "\":\"" + Marker(secretValue) + "\"", result);
    }

    [Fact]
    public void The_stored_spotify_credential_dto_shape_redacts_secret_and_refresh_only()
    {
        // Platform.cs's CredentialDto(Kind, Username, Secret, Refresh) — the exact persisted shape (§4).
        const string secretValue = "the-reusable-blob-bytes";
        const string refreshValue = "the-refresh-token";
        string json = """{"Kind":"ReusableBlob","Username":"christos@isoplanner.app","Secret":"the-reusable-blob-bytes","Refresh":"the-refresh-token"}""";
        string result = RoundTrip(json);

        Assert.Contains("\"Kind\":\"ReusableBlob\"", result);
        Assert.Contains("\"Username\":\"christos@isoplanner.app\"", result); // not a secret — passes through
        Assert.DoesNotContain(secretValue, result);
        Assert.DoesNotContain(refreshValue, result);
        Assert.Contains("\"Secret\":\"" + Marker(secretValue) + "\"", result);
        Assert.Contains("\"Refresh\":\"" + Marker(refreshValue) + "\"", result);
    }

    [Fact]
    public void Nested_objects_and_arrays_are_walked_and_redacted()
    {
        const string secretValue = "nested-secret";
        string json = """{"outer":{"inner":[{"client_token":"nested-secret"},{"other":1}]}}""";
        string result = RoundTrip(json);

        Assert.DoesNotContain(secretValue, result);
        Assert.Contains("\"client_token\":\"" + Marker(secretValue) + "\"", result);
        Assert.Contains("\"other\":1", result);
    }

    [Fact]
    public void A_field_merely_ending_in_token_is_still_redacted_by_the_suffix_rule()
    {
        const string secretValue = "abcdef";
        string json = """{"device_token":"abcdef"}""";
        string result = RoundTrip(json);
        Assert.DoesNotContain(secretValue, result);
        Assert.Contains("\"device_token\":\"" + Marker(secretValue) + "\"", result);
    }

    [Fact]
    public void Numbers_booleans_and_null_survive_untouched()
    {
        string json = """{"n":42,"f":1.5,"t":true,"fa":false,"z":null,"access_token":"x"}""";
        string result = RoundTrip(json);
        Assert.Contains("\"n\":42", result);
        Assert.Contains("\"t\":true", result);
        Assert.Contains("\"fa\":false", result);
        Assert.Contains("\"z\":null", result);
    }

    [Fact]
    public void Clean_input_with_no_sensitive_field_is_copied_through_byte_for_byte()
    {
        string json = """{"a":1,"b":"hello","c":[1,2,3]}""";
        var utf8 = Encoding.UTF8.GetBytes(json);
        Span<byte> into = new byte[utf8.Length];
        int n = Redactor.RedactJsonBody(utf8, into);

        Assert.Equal(utf8.Length, n);
        Assert.True(utf8.AsSpan().SequenceEqual(into));
    }

    [Fact]
    public void Clean_input_allocates_nothing_on_the_managed_heap()
    {
        string json = """{"a":1,"b":"hello","token_type":"Bearer","c":[1,2,3],"nested":{"d":"e"}}""";
        var utf8 = Encoding.UTF8.GetBytes(json);
        byte[] into = new byte[utf8.Length];

        // Warm up (JIT, any static initializers) before measuring.
        Redactor.RedactJsonBody(utf8, into);

        long before = GC.GetAllocatedBytesForCurrentThread();
        Redactor.RedactJsonBody(utf8, into);
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0, after - before);
    }

    [Fact]
    public void Too_small_a_destination_buffer_is_refused_not_truncated_silently()
    {
        string json = """{"a":"hello"}""";
        var utf8 = Encoding.UTF8.GetBytes(json);
        Span<byte> into = new byte[2];
        Assert.Equal(-1, Redactor.RedactJsonBody(utf8, into));
    }

    [Fact]
    public void Too_small_a_destination_buffer_on_the_redacting_path_is_also_refused()
    {
        string json = """{"access_token":"abcdefghijklmnopqrstuvwxyz"}""";
        var utf8 = Encoding.UTF8.GetBytes(json);
        Span<byte> into = new byte[4];
        Assert.Equal(-1, Redactor.RedactJsonBody(utf8, into));
    }

    [Fact]
    public void Malformed_json_is_refused_never_risked()
    {
        var utf8 = Encoding.UTF8.GetBytes("""{"access_token":"abc" this is not valid json""");
        Span<byte> into = new byte[256];
        Assert.Equal(-1, Redactor.RedactJsonBody(utf8, into));
    }

    [Fact]
    public void Truncated_mid_token_json_is_refused_never_risked()
    {
        // A body cut off mid-string (e.g. a payload dropped under lane pressure, §3.2) must never be guessed at.
        var utf8 = Encoding.UTF8.GetBytes("""{"access_token":"abc""");
        Span<byte> into = new byte[256];
        Assert.Equal(-1, Redactor.RedactJsonBody(utf8, into));
    }

    [Fact]
    public void Empty_object_round_trips()
    {
        string json = "{}";
        Assert.Equal("{}", RoundTrip(json));
    }
}
