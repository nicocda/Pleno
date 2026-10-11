using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

namespace Casino.BuildingBlocks;

/// <summary>
/// Ids derivados del usuario. Una cuenta por usuario: el id de la cuenta de la Wallet se calcula del id del usuario,
/// asi no hace falta guardar ni enviar "accountId" y un cliente no puede apuntar a una cuenta ajena.
/// No puede ser el mismo Guid que el usuario: la Wallet y el generador de seeds comparten tabla de streams y chocarian.
/// </summary>
public static class PlayerIds
{
    public static Guid WalletAccountFor(Guid userId) => Derive("wallet-account", userId);

    private static Guid Derive(string purpose, Guid userId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{purpose}:{userId:N}"));
        return new Guid(hash.AsSpan(0, 16));
    }
}

public static class Roles
{
    public const string Player = "player";
    public const string Backoffice = "backoffice";

    /// <summary>Cajero: carga fichas (desde su saldo) a los jugadores de su jurisdiccion. Tambien es jugador.</summary>
    public const string Cashier = "cashier";

    /// <summary>Jefe de cajeros: carga fichas (desde su saldo) solo a los cajeros de su jurisdiccion. Tambien es jugador.</summary>
    public const string HeadCashier = "head_cashier";
}

public static class PrincipalExtensions
{
    /// <summary>Id del usuario autenticado: el claim "sub" del token (un UUID en Keycloak). Nunca se toma del cuerpo de la peticion.</summary>
    public static bool TryGetUserId(this ClaimsPrincipal principal, out Guid userId)
    {
        ArgumentNullException.ThrowIfNull(principal);

        var sub = principal.FindFirst("sub")?.Value ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(sub, out userId);
    }

    public static Guid GetUserId(this ClaimsPrincipal principal) =>
        principal.TryGetUserId(out var userId)
            ? userId
            : throw new InvalidOperationException("El token no trae un 'sub' valido.");
}
