// ── Wavee/Diagnostics/Capture.Redact.cs — redaction, pure (§4, §7.3) ───────────────────────────────────────────────
//
// Unit 3 of docs/plans/wavee/realtime-capture-implementation.md. PURE — no disk knowledge, no `Capture`/`Sink`
// dependency; unit 2's writer calls this BEFORE anything reaches the payload lane, so a bug in the writer cannot
// leak a token because the writer never holds one. Mirrors the header `Platform.Wire.cs` already carries
// ("the QUERY STRING IS NEVER LOGGED", `Platform.Wire.cs:15`) — this extends the same discipline to headers, JSON
// bodies and any URL string a capture payload might otherwise carry verbatim.
//
// What must never reach a capture payload verbatim, and how each is handled:
//   · Authorization / Cookie / Set-Cookie / client-token / Proxy-Authorization header VALUES — `RedactHeaderValue`.
//   · A JSON body's token/secret/password/credential(s) string VALUES (never its keys) — `RedactJsonBody`, including
//     the persisted `spotify.credential` DTO's own shape (`Platform.cs`'s `CredentialDto(Kind, Username, Secret,
//     Refresh)`) — `Secret`/`Refresh` are redacted, `Kind`/`Username` are not secrets and pass through untouched.
//   · A URL's query string (tokens, signed cdn params, an OAuth code) — `RedactQueryString`.
//
// Length is kept, never the value: a debugging session cares whether a token was present and roughly how big, never
// its value — the same "[redacted NB]" shape for every kind above.
//
// Zero-allocation on already-clean input where feasible: `RedactHeaderValue` returns the SAME string reference when
// the header name is not sensitive; `RedactQueryString` returns the SAME string reference when there is no query
// string; `RedactJsonBody` runs one non-allocating `Utf8JsonReader` scan first (`HasSensitiveField`, using
// `ValueTextEquals` — never `GetString()` — so no managed string is built) and only pays for the rewrite pass
// (`Utf8JsonWriter` over a pooled buffer) when that scan actually finds a sensitive field name.

using System;
using System.Text;
using System.Text.Json;

namespace Wavee;

/// <summary>PURE. What must never reach a capture payload verbatim: bearer tokens, cookies, JSON token/secret
/// fields and query strings. See file header for the exact shapes covered. §4, §7.3.</summary>
public static class Redactor
{
    // ── header values ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Authorization / Cookie / Set-Cookie / client-token / Proxy-Authorization header VALUES become
    /// "[redacted NB]" (N = the ORIGINAL value's UTF-8 byte length) — the length is kept, the value never is. Every
    /// other header name passes through unchanged, returning the SAME reference (zero-alloc on the clean path).
    /// `null` in, `null` out.</summary>
    public static string? RedactHeaderValue(string headerName, string? value)
    {
        if (value is null) return null;
        if (!IsSensitiveHeaderName(headerName)) return value;
        return "[redacted " + Encoding.UTF8.GetByteCount(value) + "B]";
    }

    private static bool IsSensitiveHeaderName(string headerName) =>
        string.Equals(headerName, "Authorization", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(headerName, "Cookie", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(headerName, "Set-Cookie", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(headerName, "client-token", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(headerName, "Proxy-Authorization", StringComparison.OrdinalIgnoreCase);

    // ── query strings ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Everything from (and including) the first '?' becomes "[redacted NB]" (N = the ORIGINAL query
    /// string's UTF-8 byte length, '?' excluded) — a signed cdn url, an OAuth loopback redirect's `code=...`, a
    /// pathfinder query hash. A url with no '?' passes through unchanged, returning the SAME reference (zero-alloc
    /// on the clean path). `null`/empty in, same out.</summary>
    public static string? RedactQueryString(string? url)
    {
        if (string.IsNullOrEmpty(url)) return url;
        int idx = url.IndexOf('?');
        if (idx < 0) return url;
        int byteLen = Encoding.UTF8.GetByteCount(url.AsSpan(idx + 1));
        return string.Concat(url.AsSpan(0, idx + 1), "[redacted ", byteLen.ToString(), "B]");
    }

    // ── JSON bodies ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A JSON body's `access_token`/`refresh_token`/`client_token`/`password`/`secret`/`credential(s)`
    /// string VALUES (not keys — every sibling field, including `Kind`/`Username` on a persisted `spotify.credential`
    /// DTO, passes through untouched) are redacted the same "[redacted NB]" way, by a single `Utf8JsonReader`/
    /// `Utf8JsonWriter` pass — no regex over the raw bytes, no risk of a partial match inside a larger token
    /// corrupting an otherwise-valid capture. Returns the number of bytes written into <paramref name="into"/>, or
    /// -1 when <paramref name="into"/> is too small OR <paramref name="utf8"/> is not well-formed JSON (a torn
    /// payload is dropped, never risked — the caller marks the event `Truncated`, exactly §3.2's payload-lane
    /// drop rule). Zero-alloc on already-clean input: an input with no sensitive field name anywhere is detected by
    /// one non-allocating scan and copied through as-is.</summary>
    public static int RedactJsonBody(ReadOnlySpan<byte> utf8, Span<byte> into)
    {
        try
        {
            if (!HasSensitiveField(utf8))
            {
                if (utf8.Length > into.Length) return -1;
                utf8.CopyTo(into);
                return utf8.Length;
            }

            return RedactSlow(utf8, into);
        }
        catch (JsonException)
        {
            // A torn/malformed body — never guess, never risk leaking a partially-parsed token. The caller marks
            // the event `Truncated` instead (§3.2's payload-lane drop rule), exactly as a dropped-under-pressure
            // body already does.
            return -1;
        }
    }

    // Suffix shapes covering every sensitive field name this plan names (`access_token`, `refresh_token`,
    // `client_token`, `password`, `credentials`, the persisted `CredentialDto`'s `Secret`/`Refresh`) plus the
    // general "anything ending in token/secret/..." family (`device_token`, and the next one nobody's named yet).
    private static readonly byte[] SuffixToken = "token"u8.ToArray();
    private static readonly byte[] SuffixSecret = "secret"u8.ToArray();
    private static readonly byte[] SuffixPassword = "password"u8.ToArray();
    private static readonly byte[] SuffixCredential = "credential"u8.ToArray();
    private static readonly byte[] SuffixCredentials = "credentials"u8.ToArray();
    private static readonly byte[] SuffixRefresh = "refresh"u8.ToArray();

    /// <summary>One forward-only, non-allocating pass: true the instant any property NAME (not value) ends with a
    /// known-sensitive shape. Never calls `reader.GetString()` — compares the reader's own raw UTF-8 `ValueSpan`
    /// directly, so a clean document (the common case, capture ON but nothing sensitive in this particular body)
    /// costs no managed allocation. An ESCAPED property name (`\uXXXX` in a key — vanishingly rare, and never true
    /// for any field name this plan actually redacts) is treated as sensitive rather than compared byte-for-byte —
    /// conservative, never a leak.</summary>
    private static bool HasSensitiveField(ReadOnlySpan<byte> utf8)
    {
        var reader = new Utf8JsonReader(utf8);
        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.PropertyName) continue;
            if (reader.ValueIsEscaped) return true;
            var name = reader.ValueSpan;
            if (EndsWithAsciiIgnoreCase(name, SuffixToken) || EndsWithAsciiIgnoreCase(name, SuffixSecret) ||
                EndsWithAsciiIgnoreCase(name, SuffixPassword) || EndsWithAsciiIgnoreCase(name, SuffixCredential) ||
                EndsWithAsciiIgnoreCase(name, SuffixCredentials) || EndsWithAsciiIgnoreCase(name, SuffixRefresh))
                return true;
        }
        return false;
    }

    private static bool EndsWithAsciiIgnoreCase(ReadOnlySpan<byte> value, ReadOnlySpan<byte> lowerSuffix)
    {
        if (value.Length < lowerSuffix.Length) return false;
        var tail = value[^lowerSuffix.Length..];
        for (int i = 0; i < lowerSuffix.Length; i++)
        {
            byte b = tail[i];
            if (b is >= (byte)'A' and <= (byte)'Z') b += 32;
            if (b != lowerSuffix[i]) return false;
        }
        return true;
    }

    /// <summary>The broader, allocating check used only once a field is already known to need redaction somewhere
    /// in the document — a suffix match (`..._token`, `...Secret`) catches shapes `HasSensitiveField`'s literal
    /// list did not anticipate, without widening the zero-alloc fast path above.</summary>
    private static bool IsSensitiveFieldName(string name) =>
        name.EndsWith("token", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith("secret", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith("password", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith("credential", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith("credentials", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "refresh", StringComparison.OrdinalIgnoreCase);

    private static int RedactSlow(ReadOnlySpan<byte> utf8, Span<byte> into)
    {
        var bufferWriter = new System.Buffers.ArrayBufferWriter<byte>(utf8.Length + 64);
        using (var writer = new Utf8JsonWriter(bufferWriter))
        {
            var reader = new Utf8JsonReader(utf8);
            bool redactNextValue = false;
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                        writer.WriteStartObject();
                        break;
                    case JsonTokenType.EndObject:
                        writer.WriteEndObject();
                        break;
                    case JsonTokenType.StartArray:
                        writer.WriteStartArray();
                        break;
                    case JsonTokenType.EndArray:
                        writer.WriteEndArray();
                        break;
                    case JsonTokenType.PropertyName:
                    {
                        string name = reader.GetString() ?? "";
                        redactNextValue = IsSensitiveFieldName(name);
                        writer.WritePropertyName(name);
                        break;
                    }
                    case JsonTokenType.String:
                    {
                        string value = reader.GetString() ?? "";
                        if (redactNextValue)
                            writer.WriteStringValue("[redacted " + Encoding.UTF8.GetByteCount(value) + "B]");
                        else
                            writer.WriteStringValue(value);
                        redactNextValue = false;
                        break;
                    }
                    case JsonTokenType.Number:
                        // Never a redaction target (§4: string VALUES) — copied through verbatim, raw text so an
                        // integer/float's exact on-wire form survives (no float round-trip drift).
                        writer.WriteRawValue(reader.ValueSpan, skipInputValidation: true);
                        redactNextValue = false;
                        break;
                    case JsonTokenType.True:
                        writer.WriteBooleanValue(true);
                        redactNextValue = false;
                        break;
                    case JsonTokenType.False:
                        writer.WriteBooleanValue(false);
                        redactNextValue = false;
                        break;
                    case JsonTokenType.Null:
                        writer.WriteNullValue();
                        redactNextValue = false;
                        break;
                }
            }
        }

        ReadOnlySpan<byte> written = bufferWriter.WrittenSpan;
        if (written.Length > into.Length) return -1;
        written.CopyTo(into);
        return written.Length;
    }
}
