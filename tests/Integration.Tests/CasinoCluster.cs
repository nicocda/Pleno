using Casino.Hosts.Api;
using Casino.Hosts.Games;
using Casino.Hosts.Wallet;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Yarp.ReverseProxy.Forwarder;

namespace Casino.Integration.Tests;

/// <summary>
/// El sistema desplegado, en memoria: cada servicio (la API/gateway, la Wallet y los juegos) es un host REAL e independiente (su propia
/// composicion, su propia base de datos y su propia conexion a RabbitMQ) y se hablan igual que en produccion. Las llamadas HTTP entran por
/// el gateway (la API), que reenvia con el proxy real (YARP) al servicio que corresponda; lo que cruza servicios viaja por RabbitMQ. Lo unico
/// que cambia es que el "cable" entre el gateway y cada servicio es memoria en lugar de red.
/// </summary>
public sealed class CasinoCluster : IDisposable, IAsyncDisposable
{
    /// <summary>Las direcciones ficticias que ve el gateway; el cable en memoria las ignora.</summary>
    public const string WalletAddress = "http://wallet.test";

    public const string GamesAddress = "http://games.test";

    public CasinoCluster(
        WebApplicationFactory<ApiEntryPoint> api,
        WebApplicationFactory<WalletServiceEntryPoint> wallet,
        WebApplicationFactory<GamesServiceEntryPoint> games)
    {
        Api = api;
        Wallet = wallet;
        Games = games;
        Services = new ClusterServices(api.Services, games.Services, wallet.Services);
    }

    /// <summary>El host principal: puerta de entrada (gateway), usuarios y tiempo real.</summary>
    public WebApplicationFactory<ApiEntryPoint> Api { get; }

    /// <summary>El servicio de la Wallet.</summary>
    public WebApplicationFactory<WalletServiceEntryPoint> Wallet { get; }

    /// <summary>El servicio de juegos (equidad, catalogo y los juegos habilitados).</summary>
    public WebApplicationFactory<GamesServiceEntryPoint> Games { get; }

    /// <summary>
    /// Resuelve un servicio en el host que lo tiene (la API, despues los juegos, despues la Wallet). Cada servicio de negocio vive en uno solo;
    /// lo que existe en varios (por ejemplo el IHost) se resuelve en el primero: para elegir, usar <see cref="Api"/>, <see cref="Games"/> o <see cref="Wallet"/>.
    /// </summary>
    public IServiceProvider Services { get; }

    public TestServer Server => Api.Server;

    /// <summary>Un cliente que entra por el gateway, como un navegador.</summary>
    public HttpClient CreateClient() => Api.CreateClient();

    public void Dispose()
    {
        Api.Dispose();
        Games.Dispose();
        Wallet.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await Api.DisposeAsync();
        await Games.DisposeAsync();
        await Wallet.DisposeAsync();
    }

    /// <summary>El cable entre el gateway y los servicios: en vez de abrir un puerto, el proxy envia la peticion al servidor de pruebas del cluster que corresponde.</summary>
    internal sealed class InMemoryForwarderHttpClientFactory(IReadOnlyDictionary<string, HttpMessageHandler> handlers) : IForwarderHttpClientFactory
    {
        public HttpMessageInvoker CreateClient(ForwarderHttpClientContext context) => new(handlers[context.ClusterId], disposeHandler: false);
    }

    private sealed class ClusterServices(params IServiceProvider[] providers) : IServiceProvider
    {
        public object? GetService(Type serviceType)
        {
            foreach (var provider in providers)
            {
                if (provider.GetService(serviceType) is { } service)
                {
                    return service;
                }
            }

            return null;
        }
    }
}
