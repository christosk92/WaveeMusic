using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;

namespace Wavee.Backend.Spotify;

public enum HttpPool { ControlPlane, Cdn, ThirdParty, GitHub }

public static class HttpPools
{
    static readonly Lazy<HttpClient> ControlPlane = new(() => Create(
        pooledLifetime: TimeSpan.FromMinutes(2),
        idleTimeout: TimeSpan.FromMinutes(1),
        maxConnectionsPerServer: 10,
        timeout: TimeSpan.FromSeconds(30),
        preferHttp2: true));

    static readonly Lazy<HttpClient> Cdn = new(() => Create(
        pooledLifetime: TimeSpan.FromMinutes(5),
        idleTimeout: TimeSpan.FromMinutes(5),
        maxConnectionsPerServer: 16,
        timeout: TimeSpan.FromSeconds(30),
        preferHttp2: true));

    static readonly Lazy<HttpClient> ThirdParty = new(() => Create(
        pooledLifetime: TimeSpan.FromMinutes(2),
        idleTimeout: TimeSpan.FromMinutes(1),
        maxConnectionsPerServer: 4,
        timeout: TimeSpan.FromSeconds(15),
        preferHttp2: false));

    /// <summary>GitHub: the update feed (<c>.appinstaller</c>), the release-notes assets and the unauthenticated REST
    /// reads behind the issue chips. Separate from <see cref="ThirdParty"/> because it carries default headers no other
    /// pool may send — a product-token <c>User-Agent</c> (GitHub's API refuses a request without one) and the
    /// <c>application/vnd.github+json</c> accept header — and those are set ONCE here rather than per request.
    /// <para>HTTP/1.1 and a small connection cap on purpose: release-asset GETs redirect to
    /// <c>release-assets.githubusercontent.com</c>, and nothing on this pool is latency-critical.</para></summary>
    static readonly Lazy<HttpClient> GitHub = new(() =>
    {
        var client = Create(
            pooledLifetime: TimeSpan.FromMinutes(2),
            idleTimeout: TimeSpan.FromMinutes(1),
            maxConnectionsPerServer: 4,
            timeout: TimeSpan.FromSeconds(15),
            preferHttp2: false);
        try
        {
            string arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                AppVersion.Info.UserAgent(RuntimeInformation.OSDescription, arch));
        }
        catch (System.Exception)
        {
            // A malformed product token must never take the pool down with it — GitHub only requires SOME user-agent.
            try { client.DefaultRequestHeaders.UserAgent.ParseAdd("Wavee"); } catch (System.Exception) { }
        }
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    });

    public static HttpClient Get(HttpPool pool) => pool switch
    {
        HttpPool.Cdn => Cdn.Value,
        HttpPool.ThirdParty => ThirdParty.Value,
        HttpPool.GitHub => GitHub.Value,
        _ => ControlPlane.Value,
    };

    static HttpClient Create(TimeSpan pooledLifetime, TimeSpan idleTimeout, int maxConnectionsPerServer,
        TimeSpan timeout, bool preferHttp2)
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = pooledLifetime,
            PooledConnectionIdleTimeout = idleTimeout,
            MaxConnectionsPerServer = maxConnectionsPerServer,
            AutomaticDecompression = DecompressionMethods.All,
            EnableMultipleHttp2Connections = true,
        };
        var client = new HttpClient(handler)
        {
            Timeout = timeout,
        };
        if (preferHttp2)
        {
            client.DefaultRequestVersion = HttpVersion.Version20;
            client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
        }
        return client;
    }
}
