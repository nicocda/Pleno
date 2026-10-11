namespace Casino.Modules.Games.Platform;

/// <summary>Quien decide el resultado de una ronda.</summary>
public enum GameResolution
{
    /// <summary>El servidor sortea con el RNG provably fair del casino: el resultado se puede verificar.</summary>
    Server = 1,

    /// <summary>Se resuelve del lado del cliente (una pagina que se basta sola): no es verificable por el casino.</summary>
    Client = 2,
}

/// <summary>
/// La ficha de un juego en el catalogo: lo MINIMO que todo juego declara para existir en la plataforma, sin importar como este hecho por
/// dentro. El lobby se arma con estas fichas (no hay una lista fija en el front), asi que agregar o quitar un juego no toca el lobby.
/// </summary>
/// <param name="Id">Identificador estable, en minusculas y sin espacios (por ejemplo <c>roulette</c>).</param>
/// <param name="Name">Nombre para mostrar.</param>
/// <param name="Tagline">Descripcion corta para la tarjeta del lobby.</param>
/// <param name="Route">Ruta del front donde se juega (por ejemplo <c>/ruleta</c>).</param>
/// <param name="Glyph">Simbolo corto para la tarjeta.</param>
/// <param name="Resolution">Quien decide el resultado (ver <see cref="GameResolution"/>).</param>
public sealed record GameInfo(string Id, string Name, string Tagline, string Route, string Glyph, GameResolution Resolution = GameResolution.Server);
