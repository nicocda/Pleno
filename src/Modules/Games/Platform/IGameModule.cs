using Marten;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Casino.Modules.Games.Platform;

/// <summary>
/// El contrato base de un juego del lado del servidor. Cada juego es un modulo propio (su proyecto) y lo implementa como quiera: sus tablas,
/// sus reglas, sus endpoints y su arquitectura interna son suyos. La plataforma solo le pide lo comun:
/// <list type="number">
/// <item>una ficha de catalogo (<see cref="Info"/>);</item>
/// <item>registrar sus servicios y sus tipos de almacenamiento;</item>
/// <item>exponer sus endpoints (siempre autenticados: la identidad sale del token, nunca del cuerpo);</item>
/// <item>si mueve fichas, seguir el protocolo de rondas con la Wallet (<see cref="IGameRounds"/>): reservar, resolver, liquidar, cerrar.</item>
/// </list>
/// Agregar un juego es agregar su proyecto y listarlo en el host; quitarlo, sacarlo de la lista (o de la configuracion <c>Games:Enabled</c>).
/// </summary>
public interface IGameModule
{
    GameInfo Info { get; }

    /// <summary>Registra los servicios del juego. Si mueve fichas, debe registrar tambien su <see cref="IGameRounds"/>.</summary>
    void ConfigureServices(IServiceCollection services, IConfiguration configuration);

    /// <summary>Registra los tipos de documentos y eventos del juego en la base de datos del servicio de juegos.</summary>
    void ConfigureStorage(StoreOptions options);

    /// <summary>Mapea los endpoints del juego (por convencion bajo <c>/games/{id}</c>).</summary>
    void MapEndpoints(IEndpointRouteBuilder app);
}
