using System;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Catalog;
using Wavee.Backend.Metadata;
using Wavee.Backend.Persistence;
using Wavee.Backend.Spotify;
using Wavee.Core;
using Wavee.Core.Catalog;
using Wavee.SpotifyLive;
using Wavee.SpotifyLive.Catalog;

namespace Wavee.Tests;

/// <summary>Real resource scheduling, durable in-memory commits and finite Spotify recipes over a scripted HTTP port.</summary>
sealed class CatalogResourceWireFixture : IAsyncDisposable
{
    public CatalogRuntime Data { get; }
    public CatalogExtensionReader Reader { get; }
    public CatalogScope Scope { get; }
    public CatalogResourceWireFixture(IHttpExchange http, SessionContext session)
    {
        Scope = new("spotify", session.Account, session.Locale, session.Market, session.Catalogue,
            (int)session.Tier, session.ExplicitFilter);
        var persistence = new MemoryDataPersistence();
        var provider = new SpotifyCatalogResourceProvider(new ExtendedMetadataSource(http, () => "https://spclient.test", () => session),
            persistence, new PathfinderClient(http), http, () => "https://spclient.test", () => HomeModuleTitles.Default, TimeProvider.System);
        Data = new(Scope, session.Account, persistence, persistence, new MemoryReplicaProjection(new InMemoryStore()), [provider]);
        Reader = new(Data.Catalog, Data.Resources, persistence, _ => Scope);
    }
    public ValueTask DisposeAsync() => Data.DisposeAsync();
}
