using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace Wavee.Backend.Audio;

/// <summary>One live-stream connection: the parsed head plus a forward-only body.</summary>
/// <param name="StatusCode">The numeric status from either an <c>HTTP/1.x</c> or an <c>ICY</c> status line.</param>
/// <param name="Headers">Response headers, keys lower-cased (ICY servers are wildly inconsistent about casing).</param>
/// <param name="Body">The body, PREFIXED with whatever the head read over-consumed — never re-read from the socket.</param>
/// <param name="FinalUrl">The URL after redirects; reconnects target THIS, not the original.</param>
internal sealed record LiveHttpResponse(
    int StatusCode,
    IReadOnlyDictionary<string, string> Headers,
    Stream Body,
    string FinalUrl) : IDisposable
{
    public void Dispose() => Body.Dispose();

    public string? Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;
}

/// <summary>The connect seam: production is <see cref="IcyHttpConnector.ConnectAsync"/>; tests script it.</summary>
internal delegate Task<LiveHttpResponse> LiveHttpConnect(string url, CancellationToken ct);

/// <summary>A hand-rolled HTTP/<b>1.0</b> GET for Icecast/SHOUTcast, because <c>SocketsHttpHandler</c> cannot do this job:
/// SHOUTcast v1 answers with the status line <c>ICY 200 OK</c>, which the BCL parser rejects outright.
///
/// <para>HTTP/1.0 is deliberate — 1.0 has no chunked transfer-encoding, so the body is a raw byte stream to EOF, which is
/// exactly the live shape. <c>Icy-MetaData: 1</c> is sent because the interleaved titles ARE the radio now-playing line
/// (<see cref="IcyDemuxer"/> strips them back out); <c>Connection: close</c> keeps proxies from keep-alive buffering.</para></summary>
internal static class IcyHttpConnector
{
    public const string DefaultUserAgent = "Wavee/1.0";

    const int MaxHeadBytes = 32 * 1024;

    public static async Task<LiveHttpResponse> ConnectAsync(string url, CancellationToken ct,
        string userAgent = DefaultUserAgent, int maxRedirects = 5, int connectTimeoutMs = 10_000)
    {
        var current = url;
        for (int hop = 0; ; hop++)
        {
            if (!Uri.TryCreate(current, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                throw new IOException($"live stream url is not http(s): {current}");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Math.Max(1000, connectTimeoutMs));

            var client = new TcpClient { NoDelay = true };
            Stream? transport = null;
            try
            {
                await client.ConnectAsync(uri.Host, uri.Port, timeout.Token).ConfigureAwait(false);
                transport = client.GetStream();
                if (uri.Scheme == Uri.UriSchemeHttps)
                {
                    var ssl = new SslStream(transport, leaveInnerStreamOpen: false);
                    await ssl.AuthenticateAsClientAsync(uri.IdnHost, null, checkCertificateRevocation: false).ConfigureAwait(false);
                    transport = ssl;
                }

                var request = BuildRequest(uri, userAgent);
                await transport.WriteAsync(request, timeout.Token).ConfigureAwait(false);
                await transport.FlushAsync(timeout.Token).ConfigureAwait(false);

                var (status, headers, leftover) = await ReadHeadAsync(transport, timeout.Token).ConfigureAwait(false);

                if (status is >= 300 and < 400 && headers.TryGetValue("location", out var location) && location.Length > 0)
                {
                    if (hop >= maxRedirects) throw new IOException($"live stream redirect limit ({maxRedirects}) exceeded at {current}");
                    var next = Uri.TryCreate(uri, location, out var abs) ? abs.ToString() : location;
                    transport.Dispose();
                    client.Dispose();
                    current = next;
                    continue;
                }

                var body = new PrefixedBodyStream(transport, client, leftover);
                return new LiveHttpResponse(status, headers, body, uri.ToString());
            }
            catch
            {
                transport?.Dispose();
                client.Dispose();
                throw;
            }
        }
    }

    static byte[] BuildRequest(Uri uri, string userAgent)
    {
        var path = uri.PathAndQuery;
        if (path.Length == 0) path = "/";
        var sb = new StringBuilder(256);
        sb.Append("GET ").Append(path).Append(" HTTP/1.0\r\n");
        sb.Append("Host: ").Append(uri.IdnHost);
        if (!uri.IsDefaultPort) sb.Append(':').Append(uri.Port);
        sb.Append("\r\n");
        sb.Append("User-Agent: ").Append(userAgent).Append("\r\n");
        sb.Append("Accept: */*\r\n");
        sb.Append("Icy-MetaData: 1\r\n");
        sb.Append("Connection: close\r\n\r\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    static async Task<(int Status, Dictionary<string, string> Headers, byte[] Leftover)> ReadHeadAsync(Stream transport, CancellationToken ct)
    {
        var buf = new byte[MaxHeadBytes];
        int filled = 0;
        while (true)
        {
            if (TryParseHead(buf.AsSpan(0, filled), out int status, out var headers, out int consumed))
                return (status, headers, buf.AsSpan(consumed, filled - consumed).ToArray());

            if (filled == buf.Length) throw new IOException("live stream response head exceeded 32 KiB");
            int n = await transport.ReadAsync(buf.AsMemory(filled), ct).ConfigureAwait(false);
            if (n <= 0) throw new IOException("live stream closed before the response head completed");
            filled += n;
        }
    }

    /// <summary>The tested parser. Returns false when the head is not yet complete (no blank line seen).
    /// Accepts BOTH <c>HTTP/1.x &lt;code&gt; &lt;reason&gt;</c> and SHOUTcast's <c>ICY &lt;code&gt; &lt;reason&gt;</c>.</summary>
    internal static bool TryParseHead(ReadOnlySpan<byte> data, out int status, out Dictionary<string, string> headers, out int consumed)
    {
        status = 0;
        headers = new Dictionary<string, string>(StringComparer.Ordinal);
        consumed = 0;

        int end = IndexOfHeadEnd(data, out int terminatorLength);
        if (end < 0) return false;
        consumed = end + terminatorLength;

        var text = Encoding.Latin1.GetString(data[..end]);
        var lines = text.Split('\n');
        if (lines.Length == 0) return false;

        var statusLine = lines[0].TrimEnd('\r').Trim();
        if (!TryParseStatusLine(statusLine, out status)) return false;

        for (int i = 1; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.Length == 0) continue;
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var name = line[..colon].Trim().ToLowerInvariant();
            var value = line[(colon + 1)..].Trim();
            if (name.Length == 0) continue;
            headers[name] = value;   // last wins (ICY servers rarely repeat a header meaningfully)
        }
        return true;
    }

    static bool TryParseStatusLine(string line, out int status)
    {
        status = 0;
        if (line.Length == 0) return false;
        int sp = line.IndexOf(' ');
        if (sp <= 0) return false;
        var proto = line[..sp];
        bool ok = proto.StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase) ||
                  proto.Equals("ICY", StringComparison.OrdinalIgnoreCase);
        if (!ok) return false;
        var rest = line[(sp + 1)..].TrimStart();
        int sp2 = rest.IndexOf(' ');
        var code = sp2 < 0 ? rest : rest[..sp2];
        return int.TryParse(code, out status);
    }

    // A head ends at the first blank line. Tolerate bare-LF separators — a handful of SHOUTcast builds emit them.
    static int IndexOfHeadEnd(ReadOnlySpan<byte> data, out int terminatorLength)
    {
        for (int i = 0; i + 1 < data.Length; i++)
        {
            if (data[i] == (byte)'\n' && data[i + 1] == (byte)'\n') { terminatorLength = 2; return i; }
            if (i + 3 < data.Length && data[i] == (byte)'\r' && data[i + 1] == (byte)'\n' &&
                data[i + 2] == (byte)'\r' && data[i + 3] == (byte)'\n') { terminatorLength = 4; return i; }
        }
        terminatorLength = 0;
        return -1;
    }

    /// <summary>The body as the caller sees it: the bytes the head read pulled in ahead of itself, then the socket.
    /// Owns the transport + the <see cref="TcpClient"/>, so disposing the response closes the connection.</summary>
    sealed class PrefixedBodyStream : Stream
    {
        readonly Stream _inner;
        readonly TcpClient _client;
        readonly byte[] _prefix;
        int _prefixPos;
        bool _disposed;

        public PrefixedBodyStream(Stream inner, TcpClient client, byte[] prefix)
        {
            _inner = inner;
            _client = client;
            _prefix = prefix;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (buffer.IsEmpty) return 0;
            if (_prefixPos < _prefix.Length)
            {
                int n = Math.Min(buffer.Length, _prefix.Length - _prefixPos);
                _prefix.AsSpan(_prefixPos, n).CopyTo(buffer);
                _prefixPos += n;
                return n;
            }
            return _inner.Read(buffer);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (buffer.IsEmpty) return ValueTask.FromResult(0);
            if (_prefixPos < _prefix.Length)
            {
                int n = Math.Min(buffer.Length, _prefix.Length - _prefixPos);
                _prefix.AsSpan(_prefixPos, n).CopyTo(buffer.Span);
                _prefixPos += n;
                return ValueTask.FromResult(n);
            }
            return _inner.ReadAsync(buffer, ct);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _disposed = true;
                try { _inner.Dispose(); } catch { /* the socket is going away anyway */ }
                try { _client.Dispose(); } catch { /* idem */ }
            }
            base.Dispose(disposing);
        }
    }
}
