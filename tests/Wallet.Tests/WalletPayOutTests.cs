using Casino.Modules.Wallet.Domain;

namespace Casino.Wallet.Tests;

/// <summary>El pago de una reserva a otro jugador (el retiro): sale de la reserva de una cuenta y entra a la disponibilidad de la otra, sin tocar el total.</summary>
public class WalletPayOutTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    private static WalletAccount Account(Guid userId, long chips)
    {
        var account = WalletAccount.Open(Guid.NewGuid(), userId, Now);
        if (chips > 0)
        {
            account.Credit("fund", chips, Now);
        }

        return account;
    }

    private static WalletError ErrorOf(Action action) => Assert.Throws<WalletDomainException>(action).Error;

    [Fact]
    public void Paying_a_reservation_closes_it_and_moves_the_chips_to_the_other_account()
    {
        var (playerId, cashierId) = (Guid.NewGuid(), Guid.NewGuid());
        var player = Account(playerId, 500);
        var cashier = Account(cashierId, 10);
        var reservationId = Guid.NewGuid();
        player.Reserve("r1", reservationId, 200, Now, "withdrawal");

        var outcome = player.PayOutReservation("p1", reservationId, cashierId, Now);
        cashier.TransferIn("in-p1", outcome.TransactionId, playerId, 200, Now);

        Assert.Equal((300L, 0L), (player.Available, player.Reserved));
        Assert.Equal(210, cashier.Available);
        Assert.Empty(player.OpenReservations);
        Assert.Equal(510, player.Available + player.Reserved + cashier.Available); // 500 + 10: el total no cambia
    }

    [Fact]
    public void The_payout_entries_are_balanced_and_name_both_accounts()
    {
        var (playerId, cashierId) = (Guid.NewGuid(), Guid.NewGuid());
        var player = Account(playerId, 500);
        var reservationId = Guid.NewGuid();
        player.Reserve("r1", reservationId, 200, Now);

        player.PayOutReservation("p1", reservationId, cashierId, Now);

        var paid = Assert.IsType<ReservationPaidOut>(player.UncommittedEvents.Last());
        Assert.Equal(0, paid.Entries.Sum(e => e.Amount));
        Assert.Contains(paid.Entries, e => e.Account == LedgerAccountRef.Reserve(playerId) && e.Amount == -200);
        Assert.Contains(paid.Entries, e => e.Account == LedgerAccountRef.Player(cashierId) && e.Amount == 200);
        Assert.Equal((cashierId, 200L), (paid.ToUserId, paid.Stake));
    }

    [Fact]
    public void A_reservation_can_only_be_paid_once_and_only_while_open()
    {
        var (playerId, cashierId) = (Guid.NewGuid(), Guid.NewGuid());
        var player = Account(playerId, 500);
        var reservationId = Guid.NewGuid();
        player.Reserve("r1", reservationId, 200, Now);
        var first = player.PayOutReservation("p1", reservationId, cashierId, Now);

        var again = player.PayOutReservation("p1", reservationId, cashierId, Now);

        Assert.True(again.IsDuplicate);
        Assert.Equal(first.TransactionId, again.TransactionId);
        Assert.Equal(WalletError.ReservationNotOpen, ErrorOf(() => player.PayOutReservation("p2", reservationId, cashierId, Now)));
        Assert.Equal(WalletError.ReservationNotFound, ErrorOf(() => player.PayOutReservation("p3", Guid.NewGuid(), cashierId, Now)));
    }

    [Fact]
    public void A_released_reservation_cannot_be_paid()
    {
        var (playerId, cashierId) = (Guid.NewGuid(), Guid.NewGuid());
        var player = Account(playerId, 500);
        var reservationId = Guid.NewGuid();
        player.Reserve("r1", reservationId, 200, Now);
        player.Release("rel", reservationId, Now);

        Assert.Equal(WalletError.ReservationNotOpen, ErrorOf(() => player.PayOutReservation("p1", reservationId, cashierId, Now)));
        Assert.Equal(500, player.Available);
    }

    [Fact]
    public void A_reservation_cannot_be_paid_to_the_same_account()
    {
        var playerId = Guid.NewGuid();
        var player = Account(playerId, 500);
        var reservationId = Guid.NewGuid();
        player.Reserve("r1", reservationId, 200, Now);

        Assert.Equal(WalletError.InvalidTransfer, ErrorOf(() => player.PayOutReservation("p1", reservationId, playerId, Now)));
    }

    [Fact]
    public void A_payout_is_never_reversed()
    {
        var (playerId, cashierId) = (Guid.NewGuid(), Guid.NewGuid());
        var player = Account(playerId, 500);
        var reservationId = Guid.NewGuid();
        player.Reserve("r1", reservationId, 200, Now);
        var outcome = player.PayOutReservation("p1", reservationId, cashierId, Now);

        Assert.Equal(WalletError.ReversalNotAllowed, ErrorOf(() => player.Reverse("rev", outcome.TransactionId, Now)));
    }

    [Fact]
    public void The_account_rebuilt_from_its_history_keeps_the_reservation_closed()
    {
        var (playerId, cashierId) = (Guid.NewGuid(), Guid.NewGuid());
        var player = Account(playerId, 500);
        var reservationId = Guid.NewGuid();
        player.Reserve("r1", reservationId, 200, Now);
        player.PayOutReservation("p1", reservationId, cashierId, Now);

        var rebuilt = WalletAccount.Rehydrate(player.UncommittedEvents);

        Assert.Equal((300L, 0L), (rebuilt.Available, rebuilt.Reserved));
        Assert.Empty(rebuilt.OpenReservations);
    }
}
