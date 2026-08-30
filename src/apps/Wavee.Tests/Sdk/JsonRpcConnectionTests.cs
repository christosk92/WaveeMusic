using System;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Sdk;
using Wavee.Sdk.Protocol;
using Xunit;

namespace Wavee.Tests.Sdk;

public class JsonRpcConnectionTests
{
    [Fact]
    public async Task Request_RoundTripsThroughTheOtherPeer()
    {
        await using var pair = new ConnectionPair();
        pair.Server.OnRequest(ModuleMethods.Match, SdkJsonContext.Default.MatchParams,
            SdkJsonContext.Default.MatchResult,
            (MatchParams p, CancellationToken _) =>
                new ValueTask<MatchResult>(new MatchResult(p.Input.ToUpperInvariant(), "t", MediaForm.Video, true, 0.9)));
        pair.Start();

        MatchResult result = await pair.Client.RequestAsync(ModuleMethods.Match, new MatchParams("abc"),
            SdkJsonContext.Default.MatchParams, SdkJsonContext.Default.MatchResult, TestContext.Current.CancellationToken);

        Assert.Equal("ABC", result.PlayableId);
        Assert.Equal(MediaForm.Video, result.Form);
        Assert.True(result.IsLive);
    }

    [Fact]
    public async Task Ids_AreSignedPerSide_SoTheyNeverCollide()
    {
        await using var pair = new ConnectionPair();
        long clientSaw = 0;
        long serverSaw = 0;

        pair.Server.OnRequest(ModuleMethods.Warm, SdkJsonContext.Default.WarmParams, SdkJsonContext.Default.RpcUnit,
            async (WarmParams _, CancellationToken ct) =>
            {
                // the module (server) calls back into the host while the host request is in flight
                serverSaw = 1;
                AuthToken token = await pair.Server.RequestAsync(ModuleMethods.AuthToken,
                    new AuthTokenParams("spotify", false), SdkJsonContext.Default.AuthTokenParams,
                    SdkJsonContext.Default.AuthToken, ct);
                clientSaw = token.ExpiresAtUnixMs;
                return RpcUnit.Value;
            });

        pair.Client.OnRequest(ModuleMethods.AuthToken, SdkJsonContext.Default.AuthTokenParams,
            SdkJsonContext.Default.AuthToken,
            (AuthTokenParams _, CancellationToken __) => new ValueTask<AuthToken>(new AuthToken("tok", 1234)));
        pair.Start();

        await pair.Client.RequestAsync(ModuleMethods.Warm, new WarmParams("x"), SdkJsonContext.Default.WarmParams,
            SdkJsonContext.Default.RpcUnit, TestContext.Current.CancellationToken);

        Assert.Equal(1L, serverSaw);
        Assert.Equal(1234L, clientSaw);
    }

    [Fact]
    public async Task Notification_ReachesTheHandler_AndNeverGetsAReply()
    {
        await using var pair = new ConnectionPair();
        var seen = new TaskCompletionSource<MetadataUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
        pair.Client.OnNotification(ModuleMethods.Metadata, SdkJsonContext.Default.MetadataUpdate,
            u => seen.TrySetResult(u));
        pair.Start();

        pair.Server.Notify(ModuleMethods.Metadata, new MetadataUpdate("p1", "Now playing", ["a"], null),
            SdkJsonContext.Default.MetadataUpdate);

        MetadataUpdate update = await seen.Task.WaitAsync(TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.Equal("p1", update.PlayableId);
        Assert.Equal("Now playing", update.Title);
    }

    [Fact]
    public async Task UnknownMethod_AnswersMinus32601()
    {
        await using var pair = new ConnectionPair();
        pair.Start();

        JsonRpcException error = await Assert.ThrowsAsync<JsonRpcException>(async () =>
            await pair.Client.RequestAsync("playback/doesNotExist", RpcUnit.Value, SdkJsonContext.Default.RpcUnit,
                SdkJsonContext.Default.RpcUnit, TestContext.Current.CancellationToken));

        Assert.Equal(JsonRpcErrorCodes.MethodNotFound, error.Code);
    }

    [Fact]
    public async Task ModuleException_CrossesTheWireWithItsKindAndRetryHint()
    {
        await using var pair = new ConnectionPair();
        pair.Server.OnRequest(ModuleMethods.Resolve, SdkJsonContext.Default.ResolveParams,
            SdkJsonContext.Default.ResolvedPlayable, (ResolveParams _, CancellationToken __) =>
                throw new ModuleException(ModuleErrorCode.GeoBlocked, "not in your country")
                {
                    RetryAfterMs = 5000,
                    Detail = "geoblock_reason",
                });
        pair.Start();

        ModuleException error = await Assert.ThrowsAsync<ModuleException>(async () =>
            await pair.Client.RequestAsync(ModuleMethods.Resolve, new ResolveParams("id", null),
                SdkJsonContext.Default.ResolveParams, SdkJsonContext.Default.ResolvedPlayable,
                TestContext.Current.CancellationToken));

        Assert.Equal(ModuleErrorCode.GeoBlocked, error.Code);
        Assert.Equal("not in your country", error.Message);
        Assert.Equal(5000, error.RetryAfterMs);
        Assert.Equal("geoblock_reason", error.Detail);
    }

    [Fact]
    public async Task Cancellation_FiresTheHandlerToken_ThroughCancelRequest()
    {
        await using var pair = new ConnectionPair();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        pair.Server.OnRequest(ModuleMethods.Resolve, SdkJsonContext.Default.ResolveParams,
            SdkJsonContext.Default.ResolvedPlayable, async (ResolveParams _, CancellationToken ct) =>
            {
                entered.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }
                catch (OperationCanceledException)
                {
                    cancelled.TrySetResult();
                    throw;
                }

                throw new InvalidOperationException("unreachable");
            });
        pair.Start();

        using var cts = new CancellationTokenSource();
        Task<ResolvedPlayable> pending = pair.Client.RequestAsync(ModuleMethods.Resolve, new ResolveParams("id", null),
            SdkJsonContext.Default.ResolveParams, SdkJsonContext.Default.ResolvedPlayable, cts.Token);

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Timeout_ThrowsTimeoutException_AndCancelsThePeer()
    {
        await using var pair = new ConnectionPair();
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        pair.Server.OnRequest(ModuleMethods.Resolve, SdkJsonContext.Default.ResolveParams,
            SdkJsonContext.Default.ResolvedPlayable, async (ResolveParams _, CancellationToken ct) =>
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }
                catch (OperationCanceledException)
                {
                    cancelled.TrySetResult();
                    throw;
                }

                throw new InvalidOperationException("unreachable");
            });
        pair.Start();

        await Assert.ThrowsAsync<TimeoutException>(async () =>
            await pair.Client.RequestAsync(ModuleMethods.Resolve, new ResolveParams("id", null),
                SdkJsonContext.Default.ResolveParams, SdkJsonContext.Default.ResolvedPlayable,
                TimeSpan.FromMilliseconds(150), TestContext.Current.CancellationToken));

        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task BinaryResponse_IsCorrelatedByTheRequestId()
    {
        await using var pair = new ConnectionPair();
        pair.Server.OnBinaryRequest(ModuleMethods.StreamRead, SdkJsonContext.Default.StreamReadParams,
            (StreamReadParams p, CancellationToken _) =>
            {
                var bytes = new byte[p.Count];
                for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)(p.Offset + i);
                return new ValueTask<BinaryPayload>(new BinaryPayload(bytes, Eof: p.Offset > 0));
            });
        pair.Start();

        BinaryPayload first = await pair.Client.RequestBinaryAsync(ModuleMethods.StreamRead,
            new StreamReadParams("h1", 0, 4), SdkJsonContext.Default.StreamReadParams,
            TestContext.Current.CancellationToken);
        BinaryPayload second = await pair.Client.RequestBinaryAsync(ModuleMethods.StreamRead,
            new StreamReadParams("h1", 10, 2), SdkJsonContext.Default.StreamReadParams,
            TestContext.Current.CancellationToken);

        Assert.Equal(new byte[] { 0, 1, 2, 3 }, first.Bytes.ToArray());
        Assert.False(first.Eof);
        Assert.Equal(new byte[] { 10, 11 }, second.Bytes.ToArray());
        Assert.True(second.Eof);
    }

    /// <summary>Two <see cref="JsonRpcConnection"/> peers wired back to back over in-memory pipes.</summary>
    private sealed class ConnectionPair : IAsyncDisposable
    {
        private readonly MemoryPipe _clientToServer = new();
        private readonly MemoryPipe _serverToClient = new();
        private readonly CancellationTokenSource _cts = new();
        private Task _clientLoop = Task.CompletedTask;
        private Task _serverLoop = Task.CompletedTask;

        public ConnectionPair()
        {
            Client = new JsonRpcConnection(_serverToClient, _clientToServer);
            Server = new JsonRpcConnection(_clientToServer, _serverToClient, negativeIds: true);
        }

        /// <summary>The "host" peer: positive request ids.</summary>
        public JsonRpcConnection Client { get; }

        /// <summary>The "module" peer: negative request ids.</summary>
        public JsonRpcConnection Server { get; }

        public void Start()
        {
            _clientLoop = Client.RunAsync(_cts.Token);
            _serverLoop = Server.RunAsync(_cts.Token);
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            _clientToServer.CompleteWriting();
            _serverToClient.CompleteWriting();
            await Client.DisposeAsync();
            await Server.DisposeAsync();
            try
            {
                await Task.WhenAll(_clientLoop, _serverLoop).WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception)
            {
                // the loops are torn down; a cancellation race here is not a test failure
            }

            _cts.Dispose();
        }
    }
}
