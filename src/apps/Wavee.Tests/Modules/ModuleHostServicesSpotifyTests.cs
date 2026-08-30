using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend.Modules;
using Wavee.Backend.Wiring;
using Wavee.Sdk;
using Wavee.Sdk.Protocol;
using Wavee.SpotifyLive;
using Xunit;

namespace Wavee.Tests.Modules;

// ── Part 7.3 — the module→host services the Spotify playback module needs ────────────────────────────────────────────
// These drive the REAL wire: a scripted module on the far side of an in-memory JsonRpcConnection pair asks the host for
// host/auth/token, host/auth/context and spotify/audioKey, exactly as the out-of-process module will. What is under
// test is the registration LiveConnect installs at go-live (LiveSeams.ModuleHostServices) plus the permission gate in
// front of it — a module that did not declare `permission:auth.spotify` must get a typed refusal, never the session.
public class ModuleHostServicesSpotifyTests
{
    static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    const string SpotifyModuleId = "wavee.spotify";
    const string PermittedCapability = ModuleCapabilities.PermissionPrefix + LiveConnect.SpotifyAuthPermission;

    static readonly byte[] FileId = Convert.FromHexString("0123456789abcdef0123456789abcdef01234567");
    static readonly byte[] TrackGid = Convert.FromHexString("fedcba9876543210fedcba9876543210");
    static readonly byte[] AudioKey = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16];

    /// <summary>The session facts the host services read, scripted. The production implementation is
    /// <see cref="SpotifyLiveHostSession"/> over the live spclient handle + the AP socket.</summary>
    sealed class FakeSpotifySession : ISpotifyHostSession
    {
        public string Token = "tok-cached";
        public string ForcedToken = "tok-forced";
        public int TokenCalls;
        public readonly List<bool> Forces = [];
        public AuthContext Context = new("dev-42", "client-token-abc", "https://spclient.test",
            new AuthSession("hunter", "SE", "premium", "en-GB", "premium", ExplicitFilter: true));
        public byte[]? Key = AudioKey;
        public readonly List<(string File, string Gid)> KeyRequests = [];

        public Task<string> GetAccessTokenAsync(bool force, CancellationToken ct)
        {
            TokenCalls++;
            Forces.Add(force);
            return Task.FromResult(force ? ForcedToken : Token);
        }

        public AuthContext GetAuthContext() => Context;

        public Task<byte[]> RequestAudioKeyAsync(ReadOnlyMemory<byte> fileId, ReadOnlyMemory<byte> trackGid,
            CancellationToken ct)
        {
            KeyRequests.Add((Convert.ToHexString(fileId.Span), Convert.ToHexString(trackGid.Span)));
            return Key is { } key
                ? Task.FromResult(key)
                // Exactly what SpotifyLiveHostSession.NoApChannel does for a session with no retained AP socket.
                : throw new ModuleException(ModuleErrorCode.Unavailable, "no AP channel on this session");
        }
    }

    /// <summary>A running host over one fake module, with the three Spotify services already registered.</summary>
    static async Task<(ModuleHost Host, FakeModuleChannel Channel, FakeSpotifySession Session)> StartAsync(
        bool permitted = true, FakeSpotifySession? session = null)
    {
        session ??= new FakeSpotifySession();
        var services = new ModuleHostServices();
        LiveConnect.RegisterModuleHostServices(services, session);

        string[] caps = permitted ? ["playback", PermittedCapability] : ["playback"];
        var script = new FakeModule { Resolve = p => ModuleFixtures.Resolved(p.PlayableId) };
        (ModuleHost host, Func<FakeModuleChannel?> channel) = ModuleFixtures.HostOver(script,
            ModuleFixtures.Manifest(SpotifyModuleId, capabilities: caps), services: services);

        // Any request forces the lazy start + the handshake, and the handshake is where the host installs its services.
        await host.ResolveAsync(ModuleUri.Encode(SpotifyModuleId, "spotify:track:X"), force: false, Ct);
        return (host, channel()!, session);
    }

    static Task<AuthToken> AskTokenAsync(FakeModuleChannel channel, bool force = false, string provider = "spotify")
        => channel.Module.RequestAsync(ModuleMethods.AuthToken, new AuthTokenParams(provider, force),
            SdkJsonContext.Default.AuthTokenParams, SdkJsonContext.Default.AuthToken, Ct);

    static Task<AuthContext> AskContextAsync(FakeModuleChannel channel, string provider = "spotify")
        => channel.Module.RequestAsync(ModuleMethods.AuthContext, new AuthContextParams(provider),
            SdkJsonContext.Default.AuthContextParams, SdkJsonContext.Default.AuthContext, Ct);

    static Task<AudioKeyResult> AskKeyAsync(FakeModuleChannel channel, string? fileIdHex = null, string? gidHex = null)
        => channel.Module.RequestAsync(ModuleMethods.AudioKey,
            new AudioKeyParams(fileIdHex ?? Convert.ToHexString(FileId), gidHex ?? Convert.ToHexString(TrackGid)),
            SdkJsonContext.Default.AudioKeyParams, SdkJsonContext.Default.AudioKeyResult, Ct);

    [Fact]
    public async Task AuthToken_ReturnsTheSessionToken_WithAConservativeExpiry()
    {
        (ModuleHost host, FakeModuleChannel channel, FakeSpotifySession session) = await StartAsync();
        using (host)
        {
            long before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            AuthToken token = await AskTokenAsync(channel);
            long after = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            Assert.Equal("tok-cached", token.AccessToken);
            Assert.Equal(1, session.TokenCalls);
            Assert.False(Assert.Single(session.Forces));
            // The provider hands back the token STRING only, so the host reports now + AssumedTokenLifetime.
            Assert.InRange(token.ExpiresAtUnixMs,
                before + (long)LiveConnect.AssumedTokenLifetime.TotalMilliseconds,
                after + (long)LiveConnect.AssumedTokenLifetime.TotalMilliseconds);
        }
    }

    [Fact]
    public async Task AuthToken_ForceReachesTheForceProvider()
    {
        (ModuleHost host, FakeModuleChannel channel, FakeSpotifySession session) = await StartAsync();
        using (host)
        {
            Assert.Equal("tok-cached", (await AskTokenAsync(channel)).AccessToken);
            Assert.Equal("tok-forced", (await AskTokenAsync(channel, force: true)).AccessToken);
            Assert.Equal([false, true], session.Forces);
        }
    }

    [Fact]
    public async Task AuthToken_RefusesAProviderTheHostDoesNotOwn()
    {
        (ModuleHost host, FakeModuleChannel channel, FakeSpotifySession session) = await StartAsync();
        using (host)
        {
            var ex = await Assert.ThrowsAsync<ModuleException>(() => AskTokenAsync(channel, provider: "deezer"));
            Assert.Equal(ModuleErrorCode.Unsupported, ex.Code);
            Assert.Equal(0, session.TokenCalls);
        }
    }

    [Fact]
    public async Task AuthContext_CarriesTheWholeSessionShape()
    {
        (ModuleHost host, FakeModuleChannel channel, _) = await StartAsync();
        using (host)
        {
            AuthContext ctx = await AskContextAsync(channel);

            Assert.Equal("dev-42", ctx.DeviceId);
            Assert.Equal("client-token-abc", ctx.ClientToken);
            Assert.Equal("https://spclient.test", ctx.SpclientBaseUrl);
            Assert.NotNull(ctx.Session);
            Assert.Equal("hunter", ctx.Session.Account);
            Assert.Equal("SE", ctx.Session.Market);
            Assert.Equal("premium", ctx.Session.Catalogue);
            Assert.Equal("en-GB", ctx.Session.Locale);
            Assert.Equal("premium", ctx.Session.Tier);
            Assert.True(ctx.Session.ExplicitFilter);
        }
    }

    [Fact]
    public async Task AudioKey_IsProxiedToTheApSeam_AsRawBytes()
    {
        (ModuleHost host, FakeModuleChannel channel, FakeSpotifySession session) = await StartAsync();
        using (host)
        {
            AudioKeyResult result = await AskKeyAsync(channel);

            Assert.Equal(AudioKey, result.Key);
            (string file, string gid) = Assert.Single(session.KeyRequests);
            Assert.Equal(Convert.ToHexString(FileId), file);
            Assert.Equal(Convert.ToHexString(TrackGid), gid);
        }
    }

    [Fact]
    public async Task AudioKey_IsUnavailableWhenTheApChannelIsGone()
    {
        (ModuleHost host, FakeModuleChannel channel, _) = await StartAsync(session: new FakeSpotifySession { Key = null });
        using (host)
        {
            var ex = await Assert.ThrowsAsync<ModuleException>(() => AskKeyAsync(channel));
            Assert.Equal(ModuleErrorCode.Unavailable, ex.Code);
        }
    }

    [Fact]
    public async Task AudioKey_RefusesMalformedHex_WithoutTouchingTheAp()
    {
        (ModuleHost host, FakeModuleChannel channel, FakeSpotifySession session) = await StartAsync();
        using (host)
        {
            var ex = await Assert.ThrowsAsync<ModuleException>(() => AskKeyAsync(channel, fileIdHex: "zzzz"));
            Assert.Equal(ModuleErrorCode.Unsupported, ex.Code);
            Assert.Empty(session.KeyRequests);
        }
    }

    [Fact]
    public async Task AModuleWithoutThePermission_GetsNothing()
    {
        (ModuleHost host, FakeModuleChannel channel, FakeSpotifySession session) = await StartAsync(permitted: false);
        using (host)
        {
            foreach (Func<Task> call in new Func<Task>[]
                     {
                         () => AskTokenAsync(channel),
                         () => AskContextAsync(channel),
                         () => AskKeyAsync(channel),
                     })
            {
                var ex = await Assert.ThrowsAsync<ModuleException>(call);
                Assert.Equal(ModuleErrorCode.Unsupported, ex.Code);
                Assert.Contains(LiveConnect.SpotifyAuthPermission, ex.Message, StringComparison.Ordinal);
            }

            Assert.Equal(0, session.TokenCalls);
            Assert.Empty(session.KeyRequests);
        }
    }

    // Go-live happens LONG after a module may already be running (a YouTube link played before sign-in). The registry
    // re-installs onto live connections, so the services must reach a module that is already up.
    [Fact]
    public async Task RegisteringAfterTheModuleIsUp_StillReachesIt()
    {
        var services = new ModuleHostServices();
        var script = new FakeModule { Resolve = p => ModuleFixtures.Resolved(p.PlayableId) };
        (ModuleHost host, Func<FakeModuleChannel?> channel) = ModuleFixtures.HostOver(script,
            ModuleFixtures.Manifest(SpotifyModuleId, capabilities: ["playback", PermittedCapability]),
            services: services);
        using (host)
        {
            await host.ResolveAsync(ModuleUri.Encode(SpotifyModuleId, "spotify:track:X"), force: false, Ct);
            FakeModuleChannel live = channel()!;

            // Signed out: the method is not offered at all (-32601, which the SDK reads as "capability absent").
            var absent = await Assert.ThrowsAsync<JsonRpcException>(() => AskTokenAsync(live));
            Assert.Equal(JsonRpcErrorCodes.MethodNotFound, absent.Code);

            LiveConnect.RegisterModuleHostServices(services, new FakeSpotifySession());
            Assert.Equal("tok-cached", (await AskTokenAsync(live)).AccessToken);

            // …and logout takes them back off. The registry drop is what stops a NEW connection seeing them; this
            // connection already has the handler installed, so what must not happen is it answering from the dead
            // session — the emptied slot turns it into a typed NeedsAuth instead.
            LiveConnect.UnregisterModuleHostServices(services);
            Assert.DoesNotContain(ModuleMethods.AuthToken, services.Methods);
            var gone = await Assert.ThrowsAsync<ModuleException>(() => AskTokenAsync(live));
            Assert.Equal(ModuleErrorCode.NeedsAuth, gone.Code);
        }
    }

    [Fact]
    public void TheThreeMethods_AreRegisteredUnderTheirWireNames()
    {
        var services = new ModuleHostServices();
        LiveConnect.RegisterModuleHostServices(services, new FakeSpotifySession());

        Assert.Contains(ModuleMethods.AuthToken, services.Methods);
        Assert.Contains(ModuleMethods.AuthContext, services.Methods);
        Assert.Contains(ModuleMethods.AudioKey, services.Methods);
        Assert.Equal(["host/auth/context", "host/auth/token", "spotify/audioKey"], services.Methods);
    }

    // The install site is a LiveWiring ledger entry, so the seam name has to be on the roster AssertCovers is run
    // against — otherwise a go-live that never installed it would pass silently.
    [Fact]
    public void TheGoLiveSeam_IsOnTheRoster()
        => Assert.Contains(LiveSeams.ModuleHostServices, LiveSeams.All);
}
