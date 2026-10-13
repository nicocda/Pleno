using Microsoft.AspNetCore.Identity;

namespace Casino.Modules.Identity.Domain;

/// <summary>
/// Cuenta de acceso de un jugador. <see cref="IdentityUser{TKey}.UserName"/> es el alias (lo unico visible para otros jugadores).
/// El email es PII: nunca se guarda en claro (<c>Email</c> queda vacio); se guarda cifrado (<see cref="EmailCipher"/>) y con un hash con clave
/// (<see cref="EmailHash"/>) para buscar y para garantizar que sea unico sin descifrar nada.
/// </summary>
public sealed class PlayerUser : IdentityUser<Guid>
{
    public string EmailCipher { get; set; } = string.Empty;

    public string EmailHash { get; set; } = string.Empty;

    /// <summary>Version de la clave con la que se cifro <see cref="EmailCipher"/>: permite rotar claves sin recifrar todo de golpe.</summary>
    public int EmailKeyVersion { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
