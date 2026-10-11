using Casino.Contracts;

namespace Casino.Modules.Games.Platform;

/// <summary>
/// El protocolo de rondas que sigue todo juego que mueve fichas. La Wallet responde con hechos (por RabbitMQ) que solo traen el BetId:
/// cada juego busca esa apuesta en lo suyo y, si no es suya, la ignora. Todos los metodos deben ser IDEMPOTENTES (la entrega es "al menos una vez").
/// <para>
/// El recorrido es: el juego pide reservar (<see cref="ReserveStake"/>) → la Wallet reserva (<see cref="StakeReserved"/>) → el juego resuelve y
/// avisa el premio total (<see cref="RoundResolved"/>) → la Wallet liquida (<see cref="StakeSettled"/>) → el juego cierra la ronda y publica
/// <see cref="RoundClosed"/>. Si la Wallet rechaza o la reserva vence, la ronda se cierra rechazada o anulada.
/// </para>
/// </summary>
public interface IGameRounds
{
    Task OnStakeReservedAsync(StakeReserved message, CancellationToken ct = default);

    Task OnStakeRejectedAsync(StakeRejected message, CancellationToken ct = default);

    Task OnStakeSettledAsync(StakeSettled message, CancellationToken ct = default);

    Task OnStakeReleasedAsync(StakeReleased message, CancellationToken ct = default);

    Task OnSettlementRejectedAsync(StakeSettlementRejected message, CancellationToken ct = default);
}
