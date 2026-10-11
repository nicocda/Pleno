using Casino.Contracts;
using Casino.BuildingBlocks;
using Casino.Modules.Wallet.Application;
using Marten;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Wolverine;
using Wolverine.RabbitMQ;
using Wolverine.Tracking;
using Xunit.Abstractions;

namespace Casino.Integration.Tests.Wallet;

/// <summary>
/// La aplicacion completa (Marten + Wolverine) contra un Postgres y un RabbitMQ reales.
/// "Tracked sessions" de Wolverine espera a que los mensajes terminen de viajar, incluso por el broker.
/// </summary>
[Collection(WalletDbDefinition.Name)]
public sealed class WalletMessagingTests(PostgresFixture db, RabbitMqFixture rabbit, ITestOutputHelper output) : IAsyncLifetime
{
    // Cola de auditoria sin consumidor, enlazada a los exchanges de la Wallet: acumula todo lo que llega al broker.
    private const string StakeEvents = "wallet.stake-events";
    private const string BalanceEvents = "wallet.balance-events";

    private CasinoCluster _factory = null!;
    private IHost _host = null!; // el servicio de juegos: desde aca se publican las ordenes a la Wallet
    private IHost _walletHost = null!;
    private IHost _apiHost = null!; // consume los cambios de saldo (tiempo real)
    private WalletService _wallet = null!;

    public Task InitializeAsync()
    {
        _factory = TestAuth.StartApp(db, rabbit);
        _host = _factory.Games.Services.GetRequiredService<IHost>();
        _walletHost = _factory.Wallet.Services.GetRequiredService<IHost>();
        _apiHost = _factory.Api.Services.GetRequiredService<IHost>();
        _wallet = _factory.Wallet.Services.GetRequiredService<WalletService>();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private async Task<Guid> FundedAccountAsync(long chips)
    {
        var accountId = await _wallet.OpenAccountAsync(Guid.NewGuid());
        await _wallet.CreditAsync(accountId, "initial-credit", chips);
        return accountId;
    }

    private Task<ITrackedSession> TrackAsync(Func<IMessageBus, Task> action) =>
        // Se sigue la actividad de TODOS los servicios que participan: el mensaje sale de uno, viaja por RabbitMQ y lo procesan los otros.
        _host.TrackActivity()
            .AlsoTrack(_walletHost, _apiHost)
            .IncludeExternalTransports()
            .Timeout(TimeSpan.FromSeconds(45))
            .ExecuteAndWaitAsync(action);

    private Task<ITrackedSession> PublishTrackedAsync(object message) =>
        TrackAsync(async bus => await bus.PublishAsync(message));

    private async Task<long> PendingOutgoingAsync()
    {
        await using var conn = new NpgsqlConnection(db.WalletDbConnectionString);
        await conn.OpenAsync();
        await using var find = new NpgsqlCommand(
            "SELECT table_schema FROM information_schema.tables WHERE table_name = 'wolverine_outgoing_envelopes' LIMIT 1", conn);
        var schema = (string?)await find.ExecuteScalarAsync();
        if (schema is null)
        {
            return 0;
        }

        await using var count = new NpgsqlCommand($"SELECT count(*) FROM {schema}.wolverine_outgoing_envelopes", conn);
        return (long)(await count.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task A_reserve_order_travels_through_rabbitmq_and_the_fact_is_published()
    {
        var accountId = await FundedAccountAsync(1_000);
        var bet = Guid.NewGuid();
        var audit = await rabbit.CreateAuditQueueAsync(StakeEvents, BalanceEvents);
        var depthBefore = await rabbit.QueueDepthAsync(audit);

        var session = await PublishTrackedAsync(new ReserveStake(bet, accountId, 100));

        Assert.Equal(bet, session.Received.MessagesOf<ReserveStake>().Single(m => m.BetId == bet).BetId);
        var reserved = session.Sent.MessagesOf<StakeReserved>().Single(m => m.BetId == bet);
        Assert.Equal((bet, accountId, 100L), (reserved.BetId, reserved.AccountId, reserved.Stake));
        var balance = session.Sent.MessagesOf<BalanceChanged>().Single(m => m.AccountId == accountId);
        Assert.Equal((900L, 100L), (balance.Available, balance.Reserved));

        var account = await _wallet.GetAsync(accountId);
        Assert.Equal((900L, 100L), (account.Available, account.Reserved));

        // El hecho llego de verdad al broker: la cola del consumidor tiene los dos mensajes nuevos.
        Assert.True(await rabbit.QueueDepthAsync(audit) >= depthBefore + 2);
    }

    [Fact]
    public async Task A_duplicated_order_has_no_second_effect_and_publishes_nothing_new()
    {
        var accountId = await FundedAccountAsync(1_000);
        var bet = Guid.NewGuid();
        await PublishTrackedAsync(new ReserveStake(bet, accountId, 100));

        var duplicate = await PublishTrackedAsync(new ReserveStake(bet, accountId, 100));

        Assert.DoesNotContain(duplicate.Sent.MessagesOf<StakeReserved>(), m => m.BetId == bet);
        Assert.DoesNotContain(duplicate.Sent.MessagesOf<BalanceChanged>(), m => m.AccountId == accountId);
        var account = await _wallet.GetAsync(accountId);
        Assert.Equal((900L, 100L), (account.Available, account.Reserved));
    }

    [Fact]
    public async Task An_order_that_cannot_be_met_is_answered_with_a_rejection_and_changes_nothing()
    {
        var accountId = await FundedAccountAsync(50);
        var bet = Guid.NewGuid();

        var session = await PublishTrackedAsync(new ReserveStake(bet, accountId, 5_000));

        var rejected = session.Sent.MessagesOf<StakeRejected>().Single(m => m.BetId == bet);
        Assert.Equal((bet, "InsufficientFunds"), (rejected.BetId, rejected.Reason));
        Assert.DoesNotContain(session.Sent.MessagesOf<StakeReserved>(), m => m.BetId == bet);
        Assert.Equal(50, (await _wallet.GetAsync(accountId)).Available);
    }

    [Fact]
    public async Task A_round_result_settles_the_bet_once()
    {
        var accountId = await FundedAccountAsync(1_000);
        var bet = Guid.NewGuid();
        await PublishTrackedAsync(new ReserveStake(bet, accountId, 100));

        var session = await PublishTrackedAsync(new RoundResolved(bet, accountId, 360));
        var duplicate = await PublishTrackedAsync(new RoundResolved(bet, accountId, 360));

        var settled = session.Sent.MessagesOf<StakeSettled>().Single(m => m.BetId == bet);
        Assert.Equal((100L, 360L), (settled.Stake, settled.Payout));
        Assert.DoesNotContain(duplicate.Sent.MessagesOf<StakeSettled>(), m => m.BetId == bet);
        var account = await _wallet.GetAsync(accountId);
        Assert.Equal((1_260L, 0L), (account.Available, account.Reserved));
    }

    [Fact]
    public async Task A_round_result_for_an_unknown_reservation_is_rejected_so_the_game_can_void_it()
    {
        var accountId = await FundedAccountAsync(1_000);
        var bet = Guid.NewGuid();

        var session = await PublishTrackedAsync(new RoundResolved(bet, accountId, 100));

        var rejected = session.Sent.MessagesOf<StakeSettlementRejected>().Single(m => m.BetId == bet);
        Assert.Equal((bet, "ReservationNotFound"), (rejected.BetId, rejected.Reason));
        Assert.Equal(1_000, (await _wallet.GetAsync(accountId)).Available);
    }

    [Fact]
    public async Task Failed_attempts_under_contention_never_leak_phantom_messages()
    {
        // Muchos intentos chocan por la version del stream y se revierten: sus mensajes NO deben salir.
        // Si alguno saliera, habria mas StakeReserved que reservas reales.
        var accountId = await FundedAccountAsync(500);
        var store = _factory.Wallet.Services.GetRequiredService<IDocumentStore>();
        var outbox = _factory.Wallet.Services.GetRequiredService<IOutboxFactory>();
        var instances = new[]
        {
            new WalletService(store, TimeProvider.System, outbox: outbox),
            new WalletService(store, TimeProvider.System, outbox: outbox),
        };

        var session = await TrackAsync(_ => Task.WhenAll(Enumerable.Range(0, 100).Select(i => Task.Run(async () =>
        {
            try
            {
                await instances[i % 2].ReserveAsync(accountId, $"bet-{i}", Guid.NewGuid(), 10);
            }
            catch (Modules.Wallet.Domain.WalletDomainException)
            {
                // Saldo insuficiente: rechazo de negocio esperado para las 50 ultimas.
            }
        }))));

        var account = await _wallet.GetAsync(accountId);
        Assert.Equal(500, account.Reserved);
        Assert.Equal(50, session.Sent.MessagesOf<StakeReserved>().Count(m => m.AccountId == accountId));
        Assert.Equal(50, session.Sent.MessagesOf<BalanceChanged>().Count(m => m.AccountId == accountId));
    }

    [Fact]
    public async Task A_broker_outage_loses_nothing_the_outbox_delivers_after_recovery()
    {
        var accountId = await FundedAccountAsync(1_000);
        await TrackAsync(_ => Task.CompletedTask);
        var audit = await rabbit.CreateAuditQueueAsync(StakeEvents, BalanceEvents);
        var depthBefore = await rabbit.QueueDepthAsync(audit);
        var bet = Guid.NewGuid();

        await rabbit.StopBrokerAsync();
        try
        {
            // Con el broker caido la operacion igual se confirma: el mensaje queda guardado en la base (outbox).
            await _wallet.ReserveAsync(accountId, "reserve-during-outage", bet, 100);
            var account = await _wallet.GetAsync(accountId);
            Assert.Equal((900L, 100L), (account.Available, account.Reserved));
            Assert.True(await PendingOutgoingAsync() > 0, "El mensaje deberia estar esperando en el outbox.");
        }
        finally
        {
            await rabbit.StartBrokerAsync();
        }

        // Al volver el broker, Wolverine reenvia lo pendiente: llegan los 2 hechos y el outbox queda vacio.
        var deadline = DateTime.UtcNow.AddSeconds(120);
        uint depthAfter = 0;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                depthAfter = await rabbit.QueueDepthAsync(audit);
                if (depthAfter >= depthBefore + 2 && await PendingOutgoingAsync() == 0)
                {
                    break;
                }
            }
            catch (Exception)
            {
                // El broker todavia esta arrancando.
            }

            await Task.Delay(1_000);
        }

        output.WriteLine($"Cola de auditoria {audit}: antes={depthBefore}, despues={depthAfter}");
        Assert.True(depthAfter >= depthBefore + 2, "Los hechos no llegaron al broker tras recuperarse.");
        Assert.Equal(0, await PendingOutgoingAsync());
    }
}
