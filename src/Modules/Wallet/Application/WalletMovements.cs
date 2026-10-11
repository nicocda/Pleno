using Casino.Modules.Wallet.Domain;
using Marten;

namespace Casino.Modules.Wallet.Application;

/// <summary>Que paso con las fichas disponibles del jugador.</summary>
public enum MovementKind
{
    /// <summary>Fichas de bienvenida al darse de alta.</summary>
    WelcomeBonus = 1,

    /// <summary>Fichas cargadas a mano por un administrador.</summary>
    Credit = 2,

    /// <summary>Fichas apartadas para una apuesta.</summary>
    Stake = 3,

    /// <summary>Premio cobrado de una apuesta.</summary>
    Prize = 4,

    /// <summary>Fichas devueltas porque la apuesta se anulo.</summary>
    Refund = 5,

    /// <summary>Una operacion anterior se revirtio.</summary>
    Reversal = 6,

    /// <summary>Fichas recibidas de otro jugador (la carga de un cajero, por ejemplo).</summary>
    TransferIn = 7,

    /// <summary>Fichas enviadas a otro jugador.</summary>
    TransferOut = 8,

    /// <summary>Comision que paga la casa por una carga hecha a alguien de tu jurisdiccion.</summary>
    Commission = 9,

    /// <summary>Fichas apartadas para un retiro, a la espera de que las cobre tu cajero.</summary>
    Withdrawal = 10,

    /// <summary>Fichas de un retiro que volvieron a tu saldo (cancelado, rechazado o vencido).</summary>
    WithdrawalReturned = 11,
}

/// <summary>
/// Un movimiento del saldo disponible. <paramref name="Delta"/> es con signo (negativo = salen fichas) y
/// <paramref name="BalanceAfter"/> es lo disponible justo despues. <paramref name="Version"/> ordena y sirve de cursor.
/// <paramref name="GameId"/> es el juego de una apuesta, su premio o su devolucion (null si no aplica o la apuesta es anterior a que se guardara).
/// </summary>
public sealed record Movement(long Version, DateTimeOffset At, MovementKind Kind, long Delta, long BalanceAfter, Guid? Reference, string? GameId = null);

/// <summary>Una pagina de movimientos, de los mas recientes a los mas viejos. <paramref name="NextBefore"/> es el cursor de la pagina siguiente (null si no hay mas).</summary>
public sealed record MovementsPage(IReadOnlyList<Movement> Items, long? NextBefore);

public sealed partial class WalletService
{
    /// <summary>
    /// Extracto de la cuenta: cada operacion que cambio las fichas disponibles, con el saldo resultante. Se deriva de los asientos del
    /// ledger (nunca se guarda aparte), asi que siempre coincide con el saldo. Las apuestas que se pierden se ven como la apuesta
    /// (sale) sin premio (no hay un segundo movimiento: el resultado no mueve fichas disponibles).
    /// </summary>
    public async Task<MovementsPage> GetMovementsAsync(Guid accountId, int limit, long? before, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        await using var session = store.QuerySession();
        var stream = await session.Events.FetchStreamAsync(accountId, token: ct);
        if (stream.Count == 0)
        {
            throw new WalletDomainException(WalletError.AccountNotFound, $"No existe la cuenta {accountId}.");
        }

        var movements = new List<Movement>();
        Guid userId = Guid.Empty;
        long available = 0;
        var gameByReservation = new Dictionary<Guid, string>();
        foreach (var stored in stream)
        {
            switch (stored.Data)
            {
                case AccountOpened opened:
                    userId = opened.UserId;
                    break;

                case LedgerEvent ledger:
                    var delta = ledger.Entries
                        .Where(e => e.Account == LedgerAccountRef.Player(userId))
                        .Sum(e => e.Amount);
                    available += delta;
                    if (ledger is BetReserved { GameId: { } game } reserved)
                    {
                        gameByReservation[reserved.ReservationId] = game;
                    }

                    if (delta != 0)
                    {
                        var reference = ReferenceOf(ledger);
                        var gameId = reference is { } r && gameByReservation.TryGetValue(r, out var g) ? g : null;
                        movements.Add(new Movement(stored.Version, ledger.OccurredAt, KindOf(ledger, delta, gameId == WithdrawalService.GameId), delta, available, reference, gameId));
                    }

                    break;
            }
        }

        var older = movements.Where(m => before is null || m.Version < before).OrderByDescending(m => m.Version).Take(limit + 1).ToList();
        var items = older.Take(limit).ToList();
        return new MovementsPage(items, older.Count > limit ? items[^1].Version : null);
    }

    private static MovementKind KindOf(LedgerEvent ledger, long delta, bool isWithdrawal) => ledger switch
    {
        BetReserved when isWithdrawal => MovementKind.Withdrawal,
        ReservationReleased when isWithdrawal => MovementKind.WithdrawalReturned,
        ChipsTransferred => delta > 0 ? MovementKind.TransferIn : MovementKind.TransferOut,
        ChipsCredited c when c.IdempotencyKey == "welcome-bonus" => MovementKind.WelcomeBonus,
        ChipsCredited c when c.IdempotencyKey.StartsWith("commission:", StringComparison.Ordinal) => MovementKind.Commission,
        ChipsCredited => MovementKind.Credit,
        BetReserved => MovementKind.Stake,
        BetSettled => MovementKind.Prize,
        ReservationReleased => MovementKind.Refund,
        _ => MovementKind.Reversal,
    };

    private static Guid? ReferenceOf(LedgerEvent ledger) => ledger switch
    {
        BetReserved r => r.ReservationId,
        BetSettled s => s.ReservationId,
        ReservationReleased r => r.ReservationId,
        OperationReversed o => o.ReversedTransactionId,
        _ => null,
    };
}
