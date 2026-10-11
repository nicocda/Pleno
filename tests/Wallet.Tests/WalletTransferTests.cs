using Casino.Modules.Wallet.Domain;

namespace Casino.Wallet.Tests;

/// <summary>Las dos mitades de una transferencia: cada cuenta registra la suya con los mismos asientos y el saldo total del sistema no cambia.</summary>
public class WalletTransferTests
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
    public void Both_halves_move_the_chips_and_the_total_does_not_change()
    {
        var (cashierId, playerId) = (Guid.NewGuid(), Guid.NewGuid());
        var cashier = Account(cashierId, 1_000);
        var player = Account(playerId, 50);
        var transferId = Guid.NewGuid();

        cashier.TransferOut("k1", transferId, playerId, 300, Now);
        player.TransferIn("in-k1", transferId, cashierId, 300, Now);

        Assert.Equal(700, cashier.Available);
        Assert.Equal(350, player.Available);
        Assert.Equal(1_050, cashier.Available + player.Available);
    }

    [Fact]
    public void Each_half_records_the_same_balanced_entries_and_transaction_id()
    {
        var (cashierId, playerId) = (Guid.NewGuid(), Guid.NewGuid());
        var cashier = Account(cashierId, 1_000);
        var player = Account(playerId, 0);
        var transferId = Guid.NewGuid();

        cashier.TransferOut("k1", transferId, playerId, 300, Now);
        player.TransferIn("in-k1", transferId, cashierId, 300, Now);

        var sent = Assert.IsType<ChipsTransferred>(cashier.UncommittedEvents.Last());
        var received = Assert.IsType<ChipsTransferred>(player.UncommittedEvents.Last());
        Assert.Equal(transferId, sent.TransactionId);
        Assert.Equal(transferId, received.TransactionId);
        Assert.Equal(sent.Entries, received.Entries);
        Assert.Equal(0, sent.Entries.Sum(e => e.Amount));
        Assert.Equal((cashierId, playerId, 300L), (sent.FromUserId, sent.ToUserId, sent.Amount));
    }

    [Fact]
    public void The_sender_needs_enough_available_chips_and_nothing_changes_otherwise()
    {
        var cashier = Account(Guid.NewGuid(), 100);

        Assert.Equal(WalletError.InsufficientFunds, ErrorOf(() => cashier.TransferOut("k1", Guid.NewGuid(), Guid.NewGuid(), 101, Now)));

        Assert.Equal(100, cashier.Available);
    }

    [Fact]
    public void Reserved_chips_cannot_be_transferred()
    {
        var cashier = Account(Guid.NewGuid(), 100);
        cashier.Reserve("r1", Guid.NewGuid(), 80, Now);

        Assert.Equal(WalletError.InsufficientFunds, ErrorOf(() => cashier.TransferOut("k1", Guid.NewGuid(), Guid.NewGuid(), 50, Now)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void The_amount_must_be_a_positive_whole_number_of_chips(long amount)
    {
        var cashier = Account(Guid.NewGuid(), 100);

        Assert.Equal(WalletError.InvalidAmount, ErrorOf(() => cashier.TransferOut("k1", Guid.NewGuid(), Guid.NewGuid(), amount, Now)));
    }

    [Fact]
    public void Nobody_can_transfer_to_themselves()
    {
        var userId = Guid.NewGuid();
        var account = Account(userId, 100);

        Assert.Equal(WalletError.InvalidTransfer, ErrorOf(() => account.TransferOut("k1", Guid.NewGuid(), userId, 10, Now)));
    }

    [Fact]
    public void Repeating_a_transfer_with_the_same_key_is_a_duplicate_and_changing_it_is_a_conflict()
    {
        var (cashierId, playerId) = (Guid.NewGuid(), Guid.NewGuid());
        var cashier = Account(cashierId, 1_000);
        var first = cashier.TransferOut("k1", Guid.NewGuid(), playerId, 100, Now);

        var again = cashier.TransferOut("k1", Guid.NewGuid(), playerId, 100, Now);

        Assert.True(again.IsDuplicate);
        Assert.Equal(first.TransactionId, again.TransactionId);
        Assert.Equal(900, cashier.Available);
        Assert.Equal(WalletError.IdempotencyKeyReused, ErrorOf(() => cashier.TransferOut("k1", Guid.NewGuid(), playerId, 200, Now)));
        Assert.Equal(WalletError.IdempotencyKeyReused, ErrorOf(() => cashier.TransferOut("k1", Guid.NewGuid(), Guid.NewGuid(), 100, Now)));
    }

    [Fact]
    public void A_transfer_cannot_be_reversed_on_one_side_only()
    {
        var (cashierId, playerId) = (Guid.NewGuid(), Guid.NewGuid());
        var cashier = Account(cashierId, 1_000);
        var outcome = cashier.TransferOut("k1", Guid.NewGuid(), playerId, 100, Now);

        Assert.Equal(WalletError.ReversalNotAllowed, ErrorOf(() => cashier.Reverse("rev", outcome.TransactionId, Now)));
    }

    [Fact]
    public void The_account_rebuilt_from_its_history_has_the_same_balance()
    {
        var (cashierId, playerId) = (Guid.NewGuid(), Guid.NewGuid());
        var cashier = Account(cashierId, 1_000);
        cashier.TransferOut("k1", Guid.NewGuid(), playerId, 250, Now);
        cashier.TransferIn("in-k2", Guid.NewGuid(), playerId, 40, Now);

        var rebuilt = WalletAccount.Rehydrate(cashier.UncommittedEvents);

        Assert.Equal(cashier.Available, rebuilt.Available);
        Assert.Equal(790, rebuilt.Available);
    }
}
