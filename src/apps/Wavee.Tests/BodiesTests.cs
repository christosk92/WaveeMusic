// ── Wavee.Tests/BodiesTests.cs — pooled body reads, the lending scope, and the answers built on them ────────────────
//
// `Platform/Bodies.cs` replaced every api answer's `MemoryStream` + `ToArray` read, whose doubling left a dead 128K,
// 256K, 512K … chain on the large-object heap for every answer past 64 KB. What has to hold:
//
//   THE BYTES ARE THE BYTES. However the stream hands them over (one byte at a time, a lying Content-Length, nothing at
//   all), the body read is exactly what the stream carried.
//
//   A LENT BUFFER NEVER OUTLIVES ITS SCOPE. After the scope returns it, reading it throws — it never silently reads an
//   array the pool has handed to someone else — while a copy taken with `ToOwned()` stays valid forever.
//
//   THE OWNED READ PAYS FOR THE BYTES ONCE. After warm-up a 1 MB read allocates about 1 MB, not the 3 MB+ chain.

using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Xunit;
using Xm = Wavee.Protocol.ExtendedMetadata;

namespace Wavee.Tests;

public class BodiesTests
{
    static byte[] Payload(int length)
    {
        var b = new byte[length];
        for (int i = 0; i < length; i++) b[i] = (byte)(i * 31 + 7);
        return b;
    }

    /// <summary>A stream that hands out at most <c>chunk</c> bytes per read — a chunked or decompressing transport.</summary>
    sealed class Trickle(byte[] data, int chunk) : Stream
    {
        int _at;
        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = Math.Min(Math.Min(count, chunk), data.Length - _at);
            Array.Copy(data, _at, buffer, offset, n);
            _at += n;
            return n;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _at; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    sealed class Faulting(int after) : Stream
    {
        int _given;
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_given >= after) throw new IOException("the connection dropped");
            int n = Math.Min(count, after - _given);
            _given += n;
            return n;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _given; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // ── the sizing rule (pure) ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_known_length_rents_one_byte_more_so_the_eof_read_never_grows_it()
    {
        Assert.Equal(456_001, BodySizing.Start(456_000));
        Assert.Equal(BodySizing.UnknownLengthStart, BodySizing.Start(null));
        Assert.Equal(BodySizing.UnknownLengthStart, BodySizing.Start(0));
        Assert.Equal(BodySizing.MaxHintedStart, BodySizing.Start(long.MaxValue));
    }

    [Fact]
    public void Growth_doubles_and_stops_at_the_largest_array()
    {
        Assert.Equal(32 * 1024, BodySizing.Next(16 * 1024));
        Assert.Equal(512, BodySizing.Next(1));
        Assert.Equal(Array.MaxLength, BodySizing.Next(Array.MaxLength - 10));
        Assert.Throws<IOException>(() => BodySizing.Next(Array.MaxLength));
    }

    // ── the reads ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, 1, null)]
    [InlineData(1, 1, null)]
    [InlineData(100_000, 7, null)]            // a trickle past the first rental: grows several times
    [InlineData(300_000, 65_536, 300_000L)]   // an honest Content-Length
    [InlineData(300_000, 65_536, 1_000L)]     // a length that undershoots: grows past it
    [InlineData(1_000, 65_536, 300_000L)]     // a length that overshoots: stops at EOF
    public void The_owned_read_is_exactly_what_the_stream_carried(int length, int chunk, long? hint)
    {
        byte[] data = Payload(length);
        byte[] read = Bodies.ReadOwned(new Trickle(data, chunk), hint);
        Assert.Equal(data, read);
    }

    [Fact]
    public void The_pooled_read_spans_exactly_the_body_and_its_copy_is_made_once()
    {
        byte[] data = Payload(200_000);
        using PooledBody body = Bodies.ReadPooled(new Trickle(data, 4096), null);
        Assert.Equal(data.Length, body.Length);
        Assert.True(body.Span.SequenceEqual(data));
        byte[] owned = body.ToOwned();
        Assert.Equal(data, owned);
        Assert.Same(owned, body.ToOwned());
    }

    [Fact]
    public void A_read_that_faults_mid_stream_throws_and_holds_nothing()
        => Assert.Throws<IOException>(() => Bodies.ReadOwned(new Faulting(after: 100_000), null));

    [Fact]
    public void The_owned_read_allocates_the_body_once_not_a_doubling_chain()
    {
        const int Size = 1_000_000;
        byte[] data = Payload(Size);
        for (int i = 0; i < 3; i++) Bodies.ReadOwned(new Trickle(data, 16_384), null);   // warm the pool's buckets
        var stream = new Trickle(data, 16_384);
        long before = GC.GetAllocatedBytesForCurrentThread();
        byte[] read = Bodies.ReadOwned(stream, null);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(Size, read.Length);
        // The old MemoryStream read of the same body allocated ~3.1 MB (the 4K…2M chain plus the ToArray copy).
        Assert.InRange(allocated, Size, Size + 64 * 1024);
    }

    // ── the lending scope ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_lent_body_reads_inside_its_scope_and_throws_after_it()
    {
        byte[] data = Payload(150_000);
        PooledBody lent;
        byte[] kept;
        Assert.False(Bodies.Lending);
        using (Bodies.Lend())
        {
            Assert.True(Bodies.Lending);
            lent = Bodies.ReadLent(new MemoryStream(data), data.Length);
            Assert.True(lent.Span.SequenceEqual(data));
            kept = lent.ToOwned();
        }
        Assert.False(Bodies.Lending);
        Assert.Equal(data, kept);                         // the copy outlives the scope …
        Assert.Equal(data.Length, lent.Length);
        Assert.True(lent.Span.SequenceEqual(data));       // … and a body that was copied reads from the copy

        PooledBody uncopied;
        using (Bodies.Lend()) uncopied = Bodies.ReadLent(new MemoryStream(data), data.Length);
        Assert.Throws<ObjectDisposedException>(() => uncopied.Span.Length);
        Assert.Throws<ObjectDisposedException>(() => uncopied.ToOwned());
    }

    [Fact]
    public void An_inner_scope_returns_only_its_own_bodies()
    {
        using (Bodies.Lend())
        {
            PooledBody outer = Bodies.ReadLent(new MemoryStream(Payload(10)), 10);
            PooledBody inner;
            using (Bodies.Lend()) inner = Bodies.ReadLent(new MemoryStream(Payload(20)), 20);
            Assert.True(Bodies.Lending);
            Assert.Equal(10, outer.Span.Length);
            Assert.Throws<ObjectDisposedException>(() => inner.Span.Length);
        }
        Assert.False(Bodies.Lending);
    }

    [Fact]
    public void Lending_without_a_scope_is_a_programming_error()
    {
        Assert.Throws<InvalidOperationException>(() => Bodies.ReadLent(new MemoryStream([1, 2, 3]), 3));
        Assert.Throws<InvalidOperationException>(() => Bodies.RentLent(3, out _));
    }

    // ── the api answer on top of it ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_lent_answer_decodes_in_place_and_leaves_its_scope_only_as_a_copy()
    {
        byte[] data = Payload(120_000);
        Spotify.Api.Result owned;
        using (Bodies.Lend())
        {
            var lent = new Spotify.Api.Result(200, Bodies.ReadLent(new MemoryStream(data), data.Length), etag: "e1");
            Assert.Equal(data.Length, lent.Length);
            Assert.True(lent.Bytes.SequenceEqual(data));
            Assert.Equal(data, lent.Body);
            Assert.Same(lent.Body, lent.Body);            // one copy, however often it is asked for
            owned = lent.Owned();
        }
        Assert.Equal(200, owned.Status);
        Assert.Equal("e1", owned.ETag);
        Assert.Equal(data, owned.Body);
        Assert.True(owned.Bytes.SequenceEqual(data));
    }

    [Fact]
    public void An_answer_that_was_never_lent_is_its_own_array()
    {
        byte[] data = Payload(10);
        var result = new Spotify.Api.Result(200, data);
        Assert.Same(data, result.Body);
        Assert.Equal(10, result.Length);
        Assert.Equal(0, default(Spotify.Api.Result).Length);
        Assert.Empty(default(Spotify.Api.Result).Body);
    }

    [Fact]
    public void A_zstd_frame_read_in_place_unwraps_like_the_array_form()
    {
        byte[] raw = Payload(300_000);
        byte[] framed;
        using (var compressor = new ZstdSharp.Compressor(3)) framed = compressor.Wrap(raw).ToArray();
        using (Bodies.Lend())
        {
            var lent = new Spotify.Api.Result(200, Bodies.ReadLent(new MemoryStream(framed), framed.Length));
            Assert.Equal(raw, Spotify.Api.Unzstd(lent.Bytes));
        }
        Assert.Null(Spotify.Api.Unzstd((ReadOnlySpan<byte>)[0x28, 0xB5, 0x2F, 0xFD, 0x01, 0x02, 0x03, 0x04, 0x05]));
    }

    [Fact]
    public void The_metadata_cache_answers_the_same_bytes_lent_or_owned()
    {
        var batch = new Xm.BatchedEntityRequest { Header = new Xm.BatchedEntityRequestHeader { Country = "NL", Catalogue = "premium" } };
        for (int i = 0; i < 40; i++) batch.EntityRequest.Add(new Xm.EntityRequest
        { EntityUri = "spotify:track:t" + i, Query = { new Xm.ExtensionQuery { ExtensionKind = Xm.ExtensionKind.TrackV4 } } });
        byte[] request = batch.ToByteArray();
        static Spotify.Api.Result Reply(byte[] body)
        {
            var asked = Xm.BatchedEntityRequest.Parser.ParseFrom(body);
            var group = new Xm.EntityExtensionDataArray { ExtensionKind = Xm.ExtensionKind.TrackV4 };
            foreach (var entity in asked.EntityRequest)
                group.ExtensionData.Add(new Xm.EntityExtensionData
                {
                    EntityUri = entity.EntityUri,
                    Header = new Xm.EntityExtensionDataHeader { StatusCode = 200, CacheTtlInSeconds = 60 },
                    ExtensionData = new Any { TypeUrl = "t", Value = ByteString.CopyFrom(Payload(4_000)) },
                });
            var answer = new Xm.BatchedExtensionResponse();
            answer.ExtendedMetadata.Add(group);
            byte[] wire = answer.ToByteArray();
            // The wire answer arrives the way the runner hands it over: lent when the thread lends.
            return Bodies.Lending ? new Spotify.Api.Result(200, Bodies.ReadLent(new MemoryStream(wire), wire.Length))
                                  : new Spotify.Api.Result(200, wire);
        }

        byte[] owned = new Spotify.Api.MetadataCache().Execute(request, "a", 1000, Reply).Body;
        byte[] fromLent;
        using (Bodies.Lend())
        {
            Spotify.Api.Result lent = new Spotify.Api.MetadataCache().Execute(request, "a", 1000, Reply);
            Assert.Equal(owned.Length, lent.Length);
            fromLent = lent.Bytes.ToArray();
        }
        Assert.Equal(owned, fromLent);
        Assert.Equal(40, Xm.BatchedExtensionResponse.Parser.ParseFrom(fromLent).ExtendedMetadata[0].ExtensionData.Count);
    }

    [Fact]
    public void A_refused_metadata_answer_leaves_the_cache_as_an_owned_copy()
    {
        var batch = new Xm.BatchedEntityRequest { Header = new Xm.BatchedEntityRequestHeader { Country = "NL" } };
        batch.EntityRequest.Add(new Xm.EntityRequest
        { EntityUri = "spotify:track:x", Query = { new Xm.ExtensionQuery { ExtensionKind = Xm.ExtensionKind.TrackV4 } } });
        byte[] error = Payload(100);
        Spotify.Api.Result result = new Spotify.Api.MetadataCache().Execute(batch.ToByteArray(), "a", 1000,
            _ => new Spotify.Api.Result(503, Bodies.ReadLent(new MemoryStream(error), error.Length)));
        // The cache's own scope is over by now; the refusal survived it as a copy.
        Assert.Equal(503, result.Status);
        Assert.Equal(error, result.Body);
    }
}
