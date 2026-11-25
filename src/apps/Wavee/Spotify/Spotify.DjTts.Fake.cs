// ── Spotify/Spotify.DjTts.Fake.cs ────────────────────────────────────────────────────────────────────────────────────────
// `--fake`'s text-to-speech service: an in-process spclient that answers the fulfil with a 303, and a CDN that serves silent MP3s
//
// Role: SHELL (two HttpMessageHandlers; no socket, no file)
// Spec: docs/plans/wavee/dj-implementation.md §2.10 (the headless proof) · §1.4 (what the real service does)
//
// WHY HANDLERS AND NOT A STUB. The two handlers replace only the wire: `Api.TtsFulfil` stamps and sends its request exactly as it
// does live (the body, the form content type, Accept-Encoding: identity) and reads the 303's raw Location; Spotify.DjTts downloads it
// with the same GET, reads the same headers (Content-Length or none, Cache-Control max-age, X-Cache) and lands the bytes the same way.
// What the handlers saw is kept (`LastFulfil`, the request and CDN counters, any credential that reached the CDN) so the inspector can
// show the request byte for byte and that the redirect was not followed: the CDN sees a GET only when DjTts makes one.
//
// THE CLIPS are silent MPEG-1 Layer III frames, 44.1 kHz mono at 320 kbit/s: a fixed 4-byte header (FF FB E0 C4) and 1040 zero bytes,
// whose side information (all zero: no Huffman data, main_data_begin 0) decodes to silence. N frames are exactly N × 1152 samples, so a
// clip's length is exact and the real MP3 decoder and the duration walk run over them. No Xing/LAME tag: like a clip synthesised while it
// streams, the length is learned by walking the frames.
//
// THE FIXTURES (invented SSML; nothing here was captured): welcome 3.4 s (130 frames, 3.396 s), jump 4.9 s (188 frames, 4.911 s),
// intro 24.5 s (938 frames, 24.503 s) streamed as a cache MISS with no Content-Length over 5.3 s, flaky (the fulfil fails once, then
// answers), broken (the fulfil fails twice: dropped). Any other SSML is a clip of 70 ms per spoken character plus its
// `<break time="…"/>`s, served whole: `djtts ensure seconds=200 n=9` fills the 64 MB cap with it.

using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Wavee;

public static partial class Spotify
{
    public static partial class DjTts
    {
        internal static partial class Fake
        {
            /// <summary>The fake CDN's host (an `.invalid` name: nothing outside this process ever answers it).</summary>
            public const string Host = "tts.fake.wavee.invalid";

            /// <summary>One silent frame: MPEG-1 Layer III, 320 kbit/s, 44 100 Hz, no padding, mono, no CRC.</summary>
            public const int FrameBytes = 1044;
            public const int SamplesPerFrame = 1152;
            public const int SampleRate = 44_100;
            static ReadOnlySpan<byte> FrameHeader => [0xFF, 0xFB, 0xE0, 0xC4];

            /// <summary>How a fixture's clip is served.</summary>
            public enum Delivery : byte
            {
                /// <summary>At once, with Content-Length (a CDN cache HIT).</summary>
                Whole,
                /// <summary>A MISS: no Content-Length, the first byte after <see cref="StreamFirstByteMs"/>, the last at
                /// <see cref="StreamTotalMs"/> (the 24.5 s intro took 5.3 s live).</summary>
                Stream,
            }

            public const int StreamFirstByteMs = 400, StreamTotalMs = 5_300;

            /// <summary>A named clip. <see cref="FailFirst"/>: how many fulfils of it answer 503 before one answers 303.</summary>
            public sealed record Fixture(string Name, string Ssml, int Frames, Delivery Delivery, int FailFirst = 0)
            {
                public long ExpectedFrames => (long)Frames * SamplesPerFrame;
                public double ExpectedMs => ExpectedFrames * 1000.0 / SampleRate;
            }

            public static readonly Fixture[] Fixtures =
            [
                new("welcome", "<speak xml:lang=\"en-US\">Hey, it's your DJ. Settle in, I've got a few things lined up.</speak>", 130, Delivery.Whole),
                new("jump", "<speak xml:lang=\"en-US\">Switching it up. Here's one from <entity type=\"artist\">The Placeholders</entity>.</speak>", 188, Delivery.Whole),
                new("intro", "<speak xml:lang=\"en-US\">Good morning. I've put together a long run of songs for you today, a few you know well and a few "
                             + "you have not heard in a while. We'll start slow and build from there, and if you want something else, I'm right here.</speak>",
                    938, Delivery.Stream),
                new("flaky", "<speak xml:lang=\"en-US\">One more try and this one gets through.</speak>", 75, Delivery.Whole, FailFirst: 1),
                new("broken", "<speak xml:lang=\"en-US\">This one never arrives.</speak>", 60, Delivery.Whole, FailFirst: 2),
            ];

            public static Fixture? FixtureNamed(string name)
            {
                foreach (Fixture f in Fixtures) if (string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)) return f;
                return null;
            }

            static Fixture? FixtureFor(string ssml)
            {
                foreach (Fixture f in Fixtures) if (string.Equals(f.Ssml, ssml, StringComparison.Ordinal)) return f;
                return null;
            }

            /// <summary>The frames a clip of <paramref name="ssml"/> lasts: a fixture's own count, else 70 ms per spoken character
            /// plus every `&lt;break time="Ns"/&gt;` or `"Nms"`, at least half a second.</summary>
            public static int FramesFor(string ssml)
            {
                if (FixtureFor(ssml) is { } f) return f.Frames;
                double seconds = 0;
                foreach (Match m in BreakPattern().Matches(ssml))
                {
                    double v = double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                    seconds += m.Groups[2].Value == "ms" ? v / 1000 : v;
                }
                int spoken = TagPattern().Replace(ssml, "").Trim().Length;
                seconds = Math.Max(0.5, seconds + spoken * 0.07);
                return (int)Math.Round(seconds * SampleRate / SamplesPerFrame);
            }

            [GeneratedRegex("<break\\s+time=\"([0-9]+(?:\\.[0-9]+)?)(s|ms)\"\\s*/>")]
            private static partial Regex BreakPattern();

            [GeneratedRegex("<[^>]*>")]
            private static partial Regex TagPattern();

            /// <summary><paramref name="frames"/> silent frames back to back: a valid MP3 of exactly frames × 1152 samples.</summary>
            public static byte[] SilentMp3(int frames)
            {
                var bytes = new byte[(long)Math.Max(1, frames) * FrameBytes];
                for (int i = 0; i < frames; i++) FrameHeader.CopyTo(bytes.AsSpan(i * FrameBytes));
                return bytes;
            }

            // ── what the handlers saw ────────────────────────────────────────────────────────────────────────────────────

            /// <summary>The last fulfil request as the transport received it: the verb, the url, every header (credentials
            /// redacted), and the body bytes.</summary>
            public readonly record struct SeenRequest(string Method, string Url, string[] Headers, byte[] Body, int Answer, string Location);

            static readonly Lock s_seenGate = new();
            static SeenRequest s_lastFulfil;
            static readonly Dictionary<string, int> s_fulfilsBySsml = new(StringComparer.Ordinal);
            static long s_fulfilRequests, s_cdnRequests, s_cdnCredentials;

            public static SeenRequest LastFulfil { get { lock (s_seenGate) return s_lastFulfil; } }
            /// <summary>Requests the fake spclient received.</summary>
            public static long FulfilRequests => Interlocked.Read(ref s_fulfilRequests);
            /// <summary>Requests the fake CDN received.</summary>
            public static long CdnRequests => Interlocked.Read(ref s_cdnRequests);
            /// <summary>CDN requests that carried an Authorization or client-token header (must stay 0).</summary>
            public static long CdnCredentials => Interlocked.Read(ref s_cdnCredentials);

            /// <summary>Forget how often each SSML was fulfilled (so `flaky` and `broken` fail again).</summary>
            public static void ResetCounts()
            {
                lock (s_seenGate) { s_fulfilsBySsml.Clear(); s_lastFulfil = default; }
            }

            public static readonly HttpMessageInvoker Spclient = new(new SpclientHandler());
            public static readonly HttpClient Cdn = new(new CdnHandler()) { Timeout = Timeout.InfiniteTimeSpan };

            // ── the spclient ─────────────────────────────────────────────────────────────────────────────────────────────

            sealed class SpclientHandler : HttpMessageHandler
            {
                protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken ct) => Answer(request);

                protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
                    => Task.FromResult(Answer(request));

                static HttpResponseMessage Answer(HttpRequestMessage request)
                {
                    Interlocked.Increment(ref s_fulfilRequests);
                    byte[] body = [];
                    if (request.Content is { } content)
                    {
                        using var buffer = new MemoryStream();
                        content.ReadAsStream().CopyTo(buffer);
                        body = buffer.ToArray();
                    }
                    var headers = new List<string>();
                    foreach (var h in request.Headers) headers.Add(h.Key + ": " + Redact(h.Key, string.Join(", ", h.Value)));
                    if (request.Content is { } c) foreach (var h in c.Headers) headers.Add(h.Key + ": " + string.Join(", ", h.Value));

                    HttpResponseMessage response;
                    string location = "";
                    string ssml = SsmlOf(body);
                    if (request.Method != HttpMethod.Post || request.RequestUri?.AbsolutePath != Api.TtsFulfilPath || ssml.Length == 0)
                        response = new HttpResponseMessage(HttpStatusCode.BadRequest);
                    else
                    {
                        int seen;
                        lock (s_seenGate)
                        {
                            s_fulfilsBySsml.TryGetValue(ssml, out seen);
                            s_fulfilsBySsml[ssml] = seen + 1;
                        }
                        if (FixtureFor(ssml) is { FailFirst: > 0 } f && seen < f.FailFirst)
                            response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                        else
                        {
                            location = SignedUrl(ssml);
                            response = new HttpResponseMessage(HttpStatusCode.SeeOther);
                            response.Headers.TryAddWithoutValidation("Location", location);
                        }
                    }
                    lock (s_seenGate)
                        s_lastFulfil = new SeenRequest(request.Method.Method, request.RequestUri?.ToString() ?? "", [.. headers], body,
                            (int)response.StatusCode, location);
                    response.RequestMessage = request;
                    return response;
                }

                static string Redact(string name, string value)
                    => name.Equals("Authorization", StringComparison.OrdinalIgnoreCase) || name.Equals("client-token", StringComparison.OrdinalIgnoreCase)
                        ? "<redacted " + value.Length + " chars>"
                        : value;
            }

            /// <summary>The SSML a fulfil body carries (field 2), or "".</summary>
            static string SsmlOf(byte[] body)
            {
                var r = new Decode.ProtoReader(body);
                ReadOnlySpan<byte> ssml = r.Bytes(2);
                return ssml.IsEmpty ? "" : Encoding.UTF8.GetString(ssml);
            }

            /// <summary>A url shaped like the real one (the SSML base64url in the path, an expiry 300 s ahead and an hmac in the query).</summary>
            static string SignedUrl(string ssml)
            {
                string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(ssml)).Replace('+', '-').Replace('/', '_');
                long exp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 300;
                string hmac = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(exp + ssml)));
                return "https://" + Host + "/v2/generate/fake/mp3/44100/0/" + b64 + "?__token__=exp=" + exp + "~hmac=" + hmac;
            }

            // ── the CDN ──────────────────────────────────────────────────────────────────────────────────────────────────

            sealed class CdnHandler : HttpMessageHandler
            {
                protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken ct) => Answer(request);

                protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
                    => Task.FromResult(Answer(request));

                static HttpResponseMessage Answer(HttpRequestMessage request)
                {
                    Interlocked.Increment(ref s_cdnRequests);
                    if (request.Headers.Contains("Authorization") || request.Headers.Contains("client-token"))
                        Interlocked.Increment(ref s_cdnCredentials);
                    Uri? uri = request.RequestUri;
                    if (request.Method != HttpMethod.Get || uri is null || uri.Host != Host) return new HttpResponseMessage(HttpStatusCode.NotFound);
                    if (!Fresh(uri.Query)) return new HttpResponseMessage(HttpStatusCode.Forbidden);     // a signed url past its 300 s
                    string segment = uri.AbsolutePath[(uri.AbsolutePath.LastIndexOf('/') + 1)..];
                    string ssml;
                    try { ssml = Encoding.UTF8.GetString(Convert.FromBase64String(Uri.UnescapeDataString(segment).Replace('-', '+').Replace('_', '/'))); }
                    catch (FormatException) { return new HttpResponseMessage(HttpStatusCode.NotFound); }
                    Fixture? fixture = FixtureFor(ssml);
                    byte[] mp3 = SilentMp3(FramesFor(ssml));
                    bool stream = fixture?.Delivery == Delivery.Stream;
                    var response = new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = stream ? new StreamContent(new PacedStream(mp3, StreamFirstByteMs, StreamTotalMs)) : new ByteArrayContent(mp3),
                        RequestMessage = request,
                    };
                    response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/mpeg");
                    response.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { Public = true, MaxAge = TimeSpan.FromSeconds(604_800) };
                    response.Headers.TryAddWithoutValidation("X-Cache", stream ? "MISS" : "HIT");
                    return response;
                }

                static bool Fresh(string query)
                {
                    int at = query.IndexOf("exp=", StringComparison.Ordinal);
                    if (at < 0) return false;
                    int end = query.IndexOf('~', at);
                    return long.TryParse(query.AsSpan(at + 4, (end < 0 ? query.Length : end) - at - 4), out long exp)
                        && exp > DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                }
            }

            /// <summary>A body that arrives as a synthesising server sends it: nothing for <c>firstByteMs</c>, then the rest evenly
            /// until <c>totalMs</c>. Forward-only and of no stated length, so the response carries no Content-Length.</summary>
            sealed class PacedStream(byte[] bytes, int firstByteMs, int totalMs) : System.IO.Stream
            {
                readonly long _t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                int _pos;

                public override bool CanRead => true;
                public override bool CanSeek => false;
                public override bool CanWrite => false;
                public override long Length => throw new NotSupportedException();
                public override long Position { get => _pos; set => throw new NotSupportedException(); }

                long Due(double ms)
                {
                    if (ms < firstByteMs) return 0;
                    double share = Math.Min(1.0, (ms - firstByteMs) / Math.Max(1, totalMs - firstByteMs));
                    return Math.Max(FrameBytes, (long)(bytes.Length * share));
                }

                public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

                public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
                {
                    while (true)
                    {
                        if (_pos >= bytes.Length) return 0;
                        double ms = System.Diagnostics.Stopwatch.GetElapsedTime(_t0).TotalMilliseconds;
                        long due = Math.Min(bytes.Length, Due(ms));
                        if (due > _pos)
                        {
                            int n = (int)Math.Min(buffer.Length, due - _pos);
                            bytes.AsSpan(_pos, n).CopyTo(buffer.Span);
                            _pos += n;
                            return n;
                        }
                        await Task.Delay(20, ct).ConfigureAwait(false);
                    }
                }

                public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
                    => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

                public override void Flush() { }
                public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
                public override void SetLength(long value) => throw new NotSupportedException();
                public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            }
        }
    }
}
