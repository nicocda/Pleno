using Casino.Contracts;
using Casino.Modules.Wallet.Domain;
using Microsoft.Extensions.Configuration;

namespace Casino.Modules.Wallet.Application;

// Handlers de Wolverine (los descubre por convencion: clase terminada en "Handler", metodo "Handle").
// Hay entrega "al menos una vez": cada orden es idempotente porque su IdempotencyKey se deriva de la apuesta.
// Los hechos (StakeReserved, StakeSettled...) los publica WalletService por el outbox, dentro de la transaccion.
// Aca solo se responden los rechazos de negocio, que no cambian estado y por eso no pasan por el outbox.

/// <summary>Un juego pide reservar las fichas de una apuesta.</summary>
public sealed class ReserveStakeHandler(WalletService wallet)
{
    /// <summary>Limites del plazo que un juego puede pedir: ni una reserva eterna ni una que venza antes de poder liquidarse.</summary>
    public const int MinTtlSeconds = 1;

    public const int MaxTtlSeconds = 3_600;

    public async Task<StakeRejected?> Handle(ReserveStake command, CancellationToken ct)
    {
        try
        {
            var key = $"reserve:{command.BetId:N}";
            if (command.TtlSeconds is { } seconds)
            {
                await wallet.ReserveAsync(command.AccountId, key, command.BetId, command.Stake, TimeSpan.FromSeconds(Math.Clamp(seconds, MinTtlSeconds, MaxTtlSeconds)), command.GameId, ct);
            }
            else
            {
                await wallet.ReserveAsync(command.AccountId, key, command.BetId, command.Stake, command.GameId, ct);
            }

            return null;
        }
        catch (WalletDomainException ex)
        {
            return new StakeRejected(command.BetId, command.AccountId, ex.Error.ToString());
        }
    }
}

/// <summary>Un juego informa el resultado: la Wallet liquida con el premio total.</summary>
public sealed class RoundResolvedHandler(WalletService wallet)
{
    public async Task<StakeSettlementRejected?> Handle(RoundResolved message, CancellationToken ct)
    {
        try
        {
            await wallet.SettleAsync(message.AccountId, $"settle:{message.BetId:N}", message.BetId, message.Payout, ct);
            return null;
        }
        catch (WalletDomainException ex)
        {
            // Por ejemplo, la reserva ya vencio y se libero: el resultado llego tarde y el juego debe anular la ronda.
            return new StakeSettlementRejected(message.BetId, message.AccountId, ex.Error.ToString());
        }
    }
}

/// <summary>
/// Vencimiento de una reserva (mensaje programado). Si ya se liquido o libero, no hace nada; si sigue abierta, la libera
/// y la Wallet publica StakeReleased para que el juego anule su ronda.
/// </summary>
public sealed class ExpireReservationHandler(WalletService wallet)
{
    public async Task Handle(ExpireReservation message, CancellationToken ct)
    {
        try
        {
            await wallet.ReleaseAsync(message.AccountId, $"expire:{message.BetId:N}", message.BetId, ct);
        }
        catch (WalletDomainException ex) when (ex.Error is WalletError.ReservationNotOpen or WalletError.ReservationNotFound)
        {
            // Ya se liquido (el caso normal) o nunca existio: nada que compensar.
        }
    }
}

/// <summary>
/// Un jugador nuevo: se abre su cuenta (el id se deriva del usuario) y se le acreditan las fichas de bienvenida.
/// Idempotente: abrir la cuenta y la clave "welcome-bonus" garantizan que un duplicado no regale fichas dos veces.
/// </summary>
public sealed class UserRegisteredHandler(WalletService wallet, IConfiguration configuration)
{
    private const long DefaultWelcomeChips = 1_000;

    public async Task Handle(UserRegistered message, CancellationToken ct)
    {
        var accountId = await wallet.OpenAccountAsync(message.UserId, ct);

        var welcome = long.TryParse(configuration["Wallet:WelcomeChips"], out var configured) && configured >= 0
            ? configured
            : DefaultWelcomeChips;
        if (welcome > 0)
        {
            await wallet.CreditAsync(accountId, "welcome-bonus", welcome, ct);
        }
    }
}
