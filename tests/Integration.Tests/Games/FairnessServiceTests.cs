using System.Security.Cryptography;
using Casino.Integration.Tests.Wallet;
using Casino.Modules.Games.Application;
using Casino.Modules.Games.Fairness;
using Casino.Modules.Games.Infrastructure;
using Casino.Modules.Games.Roulette;
using Casino.Modules.Wallet.Infrastructure;
using JasperFx;
using Marten;
using Npgsql;
using Xunit.Abstractions;

namespace Casino.Integration.Tests.Games;

[Collection(WalletDbDefinition.Name)]
public sealed class FairnessServiceTests(PostgresFixture db, ITestOutputHelper output) : IDisposable
{
    private readonly List<IDocumentStore> _stores = [];

    public void Dispose()
    {
        foreach (var store in _stores)
        {
            store.Dispose();
        }
    }

    private FairnessService NewInstance(int maxAttempts = FairnessService.DefaultMaxAttempts)
    {
        var store = DocumentStore.For(options =>
        {
            WalletMartenConfiguration.Configure(options, db.ConnectionString);
            GamesMartenConfiguration.Register(options, []); // la equidad es del nucleo: no necesita ningun juego
            options.AutoCreateSchemaObjects = AutoCreate.None;
        });
        _stores.Add(store);
        return new FairnessService(store, db.Protector, TimeProvider.System, maxAttempts);
    }

    private async Task<long> CountEventsContainingAsync(string text)
    {
        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            $"SELECT count(*) FROM {WalletMartenConfiguration.SchemaName}.mt_events WHERE position(@text in data::text) > 0", conn);
        cmd.Parameters.AddWithValue("text", text);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task First_access_creates_a_committed_pair_and_is_stable_afterwards()
    {
        var userId = Guid.NewGuid();

        var first = await db.Fairness.GetPublicInfoAsync(userId);
        var again = await db.Fairness.GetPublicInfoAsync(userId);

        Assert.Matches("^[0-9a-f]{64}$", first.Active.Commitment);
        Assert.Matches("^[0-9a-f]{16}$", first.Active.ClientSeed);
        Assert.Equal(0, first.Active.NextNonce);
        Assert.Empty(first.Retired);
        Assert.Equal(first.Active, again.Active);
    }

    [Fact]
    public async Task The_server_seed_is_never_stored_in_clear_until_it_is_revealed()
    {
        var userId = Guid.NewGuid();
        var info = await db.Fairness.GetPublicInfoAsync(userId);
        var bet = Guid.NewGuid();
        await db.Fairness.AllocateNonceAsync(userId, bet);
        var inputs = await db.Fairness.GetDrawInputsAsync(userId, bet);

        Assert.True(ProvablyFair.MatchesCommitment(inputs.ServerSeed, info.Active.Commitment));
        Assert.Equal(0, await CountEventsContainingAsync(inputs.ServerSeed));

        await db.Fairness.CompleteBetAsync(userId, bet);
        await db.Fairness.RotateAsync(userId);

        Assert.Equal(1, await CountEventsContainingAsync(inputs.ServerSeed));
    }

    [Fact]
    public async Task Draw_inputs_never_leak_the_seed_when_logged()
    {
        var userId = Guid.NewGuid();
        var bet = Guid.NewGuid();
        await db.Fairness.AllocateNonceAsync(userId, bet);

        var inputs = await db.Fairness.GetDrawInputsAsync(userId, bet);

        Assert.DoesNotContain(inputs.ServerSeed, inputs.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nonces_are_assigned_atomically_across_instances_without_gaps_or_repeats()
    {
        var userId = Guid.NewGuid();
        var instances = new[] { NewInstance(), NewInstance() };

        var allocations = await Task.WhenAll(Enumerable.Range(0, 200).Select(i => Task.Run(() =>
            instances[i % 2].AllocateNonceAsync(userId, Guid.NewGuid()))));

        Assert.Equal(Enumerable.Range(0, 200).Select(n => (long)n), allocations.Select(a => a.Nonce).Order());
        var info = await db.Fairness.GetPublicInfoAsync(userId);
        Assert.Equal(200, info.Active.NextNonce);
        Assert.Equal(200, info.PendingBets);
    }

    [Fact]
    public async Task The_same_bet_sent_in_parallel_gets_one_nonce()
    {
        var userId = Guid.NewGuid();
        var bet = Guid.NewGuid();
        var instances = new[] { NewInstance(), NewInstance() };

        var allocations = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => Task.Run(() =>
            instances[i % 2].AllocateNonceAsync(userId, bet))));

        Assert.Single(allocations.Select(a => a.Nonce).Distinct());
        Assert.Equal(1, allocations.Count(a => !a.AlreadyAllocated));
        Assert.Equal(1, (await db.Fairness.GetPublicInfoAsync(userId)).Active.NextNonce);
    }

    [Fact]
    public async Task Rotation_is_refused_while_a_bet_is_pending_and_allowed_after()
    {
        var userId = Guid.NewGuid();
        var bet = Guid.NewGuid();
        await db.Fairness.AllocateNonceAsync(userId, bet);

        var error = await Assert.ThrowsAsync<FairnessDomainException>(() => db.Fairness.RotateAsync(userId));
        Assert.Equal(FairnessError.PendingBets, error.Error);

        await db.Fairness.CompleteBetAsync(userId, bet);
        var rotated = await db.Fairness.RotateAsync(userId, "mi-nueva-semilla");

        Assert.Equal("mi-nueva-semilla", rotated.Active.ClientSeed);
        Assert.Equal(0, rotated.Active.NextNonce);
        Assert.Single(rotated.Retired);
    }

    [Fact]
    public async Task A_player_can_verify_a_roulette_result_using_only_public_information()
    {
        var userId = Guid.NewGuid();
        var before = await db.Fairness.GetPublicInfoAsync(userId);
        var commitment = before.Active.Commitment;

        // El juego sortea: toma el nonce del servidor y la seed descifrada.
        var results = new List<(Guid Bet, long Nonce, int Winning)>();
        for (var i = 0; i < 25; i++)
        {
            var betId = Guid.NewGuid();
            var allocation = await db.Fairness.AllocateNonceAsync(userId, betId);
            var inputs = await db.Fairness.GetDrawInputsAsync(userId, betId);
            var outcome = RouletteGame.Play(RouletteBet.Create(RouletteBetType.Red, [], 10), inputs.ServerSeed, inputs.ClientSeed, inputs.Nonce);
            await db.Fairness.CompleteBetAsync(userId, betId);
            results.Add((betId, allocation.Nonce, outcome.WinningNumber));
        }

        // El jugador rota y recibe la server seed anterior.
        var after = await db.Fairness.RotateAsync(userId);
        var retired = Assert.Single(after.Retired);

        // Verificacion 1: la seed revelada es la que estaba comprometida desde el principio.
        Assert.Equal(commitment, retired.Commitment);
        Assert.True(ProvablyFair.MatchesCommitment(retired.ServerSeed, commitment));
        Assert.Equal(25, retired.BetsPlayed);

        // Verificacion 2: recalculando cada apuesta con datos publicos se obtiene el mismo numero.
        foreach (var (_, nonce, winning) in results)
        {
            var recomputed = new FairRng(retired.ServerSeed, retired.ClientSeed, nonce).NextInt(RouletteBet.PocketCount);
            Assert.Equal(winning, recomputed);
        }

        Assert.NotEqual(commitment, after.Active.Commitment);
        output.WriteLine($"25 jugadas verificadas contra el compromiso {commitment[..16]}...");
    }

    [Fact]
    public async Task A_completed_bet_can_still_be_drawn_from_the_revealed_seed_after_rotation()
    {
        var userId = Guid.NewGuid();
        var bet = Guid.NewGuid();
        await db.Fairness.AllocateNonceAsync(userId, bet);
        var duringPlay = await db.Fairness.GetDrawInputsAsync(userId, bet);
        await db.Fairness.CompleteBetAsync(userId, bet);
        await db.Fairness.RotateAsync(userId);

        var afterRotation = await db.Fairness.GetDrawInputsAsync(userId, bet);

        Assert.Equal(duringPlay.ServerSeed, afterRotation.ServerSeed);
        Assert.Equal(duringPlay.Nonce, afterRotation.Nonce);
    }

    [Fact]
    public async Task A_different_master_key_cannot_open_the_stored_seed()
    {
        var userId = Guid.NewGuid();
        var bet = Guid.NewGuid();
        await db.Fairness.AllocateNonceAsync(userId, bet);
        var wrongKey = new FairnessService(db.Store, SeedProtector.FromBase64Key(SeedProtector.GenerateBase64Key()), TimeProvider.System);

        await Assert.ThrowsAnyAsync<CryptographicException>(() => wrongKey.GetDrawInputsAsync(userId, bet));
    }

    [Fact]
    public async Task Allocation_for_an_unknown_bet_draw_is_rejected()
    {
        var userId = Guid.NewGuid();
        await db.Fairness.GetPublicInfoAsync(userId);

        var error = await Assert.ThrowsAsync<FairnessDomainException>(() => db.Fairness.GetDrawInputsAsync(userId, Guid.NewGuid()));

        Assert.Equal(FairnessError.BetNotFound, error.Error);
    }

    [Fact]
    public async Task Connection_kills_during_allocation_never_repeat_or_skip_a_nonce()
    {
        var userId = Guid.NewGuid();
        await db.Fairness.GetPublicInfoAsync(userId);
        var fragile = NewInstance(maxAttempts: 2);
        var bets = Enumerable.Range(0, 100).Select(_ => Guid.NewGuid()).ToList();

        var killed = 0L;
        using var stopChaos = new CancellationTokenSource();
        var chaos = Task.Run(async () =>
        {
            while (!stopChaos.IsCancellationRequested)
            {
                try
                {
                    killed += await db.KillAllOtherConnectionsAsync();
                    await Task.Delay(25, stopChaos.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        });

        var allocations = await Task.WhenAll(bets.Select(bet => Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 200; attempt++)
            {
                try
                {
                    return await fragile.AllocateNonceAsync(userId, bet);
                }
                catch (Exception ex) when (ex is not FairnessDomainException)
                {
                    await Task.Delay(10);
                }
            }

            throw new InvalidOperationException("No se pudo asignar el nonce tras 200 reintentos.");
        })));

        await stopChaos.CancelAsync();
        await chaos;

        output.WriteLine($"Conexiones cortadas: {killed}");
        Assert.True(killed > 0, "El caos no cerro ninguna conexion.");
        Assert.Equal(Enumerable.Range(0, 100).Select(n => (long)n), allocations.Select(a => a.Nonce).Order());
        Assert.Equal(100, (await db.Fairness.GetPublicInfoAsync(userId)).Active.NextNonce);
    }
}
