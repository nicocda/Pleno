using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casino.Integration.Tests.Wallet;
using Casino.Modules.Games.Application;
using Casino.Modules.Games.Blackjack;
using Casino.Modules.Games.Fairness;
using Marten;
using Casino.Modules.Wallet.Application;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Casino.Integration.Tests.Games;

/// <summary>
/// Blackjack de punta a punta con Postgres y RabbitMQ reales: las mesas, el reparto, los turnos, la carta tapada, el crupier, los pagos, el compromiso
/// provably fair y el saldo que siempre cierra. Las manos se corren de a una y con semillas elegidas para saber que cartas salen.
/// </summary>
[Collection(WalletDbDefinition.Name)]
public sealed class BlackjackFlowTests(PostgresFixture db, RabbitMqFixture rabbit) : IDisposable
{
    private const string Table = "mesa-2"; // apuesta de 10 a 1.000

    private static readonly Func<int[], bool> AnyHand = shoe => !BlackjackMath.IsBlackjack(One(shoe).Player) && !BlackjackMath.IsBlackjack(One(shoe).Dealer);

    /// <summary>El jugador se planta con 20 y el crupier se queda con 17 a 19: gana el jugador sin que el crupier pida.</summary>
    private static readonly Func<int[], bool> StandWins = shoe =>
    {
        var (player, dealer, _) = One(shoe);
        return BlackjackMath.Evaluate(player).Total == 20 && BlackjackMath.Evaluate(dealer).Total is >= 17 and <= 19;
    };

    /// <summary>Una mano de 12 a 16 que se pasa con la proxima carta del zapato.</summary>
    private static readonly Func<int[], bool> HitBusts = shoe =>
    {
        var (player, dealer, next) = One(shoe);
        return BlackjackMath.Evaluate(player).Total is >= 12 and <= 16 && !BlackjackMath.IsBlackjack(dealer) && BlackjackMath.IsBust([.. player, next]);
    };

    private static readonly Func<int[], bool> PlayerBlackjack = shoe => BlackjackMath.IsBlackjack(One(shoe).Player) && !BlackjackMath.IsBlackjack(One(shoe).Dealer);

    private static readonly Func<int[], bool> DealerBlackjack = shoe => BlackjackMath.IsBlackjack(One(shoe).Dealer) && !BlackjackMath.IsBlackjack(One(shoe).Player);

    /// <summary>Dos jugadores, ninguno con blackjack y el crupier tampoco.</summary>
    private static readonly Func<int[], bool> TwoPlayers = shoe =>
        !BlackjackMath.IsBlackjack([shoe[0], shoe[3]]) && !BlackjackMath.IsBlackjack([shoe[1], shoe[4]]) && !BlackjackMath.IsBlackjack([shoe[2], shoe[5]]);

    /// <summary>El reparto de UN jugador: sus cartas, las del crupier (visible y tapada) y la proxima del zapato.</summary>
    private static (int[] Player, int[] Dealer, int Next) One(int[] shoe) => ([shoe[0], shoe[2]], [shoe[1], shoe[3]], shoe[4]);

    /// <summary>Una fuente de semillas que, para cada mano, busca una semilla cuyo zapato cumple lo pedido.</summary>
    private sealed class TargetedSeeds : IBlackjackSeedSource
    {
        private readonly Queue<Func<int[], bool>> _targets = new();

        public void Target(Func<int[], bool> predicate)
        {
            lock (_targets)
            {
                _targets.Enqueue(predicate);
            }
        }

        public string NextSeed(Guid roundId)
        {
            Func<int[], bool> target;
            lock (_targets)
            {
                target = _targets.Count > 0 ? _targets.Dequeue() : AnyHand;
            }

            while (true)
            {
                var seed = ProvablyFair.GenerateServerSeed();
                if (target(BlackjackMath.Shoe(seed, roundId)))
                {
                    return seed;
                }
            }
        }
    }

    private readonly List<CasinoCluster> _clusters = [];
    private readonly TargetedSeeds _seeds = new();

    public void Dispose()
    {
        foreach (var cluster in _clusters)
        {
            cluster.Dispose();
        }
    }

    private CasinoCluster Start(double turnSeconds = 10, int maxSeats = 5)
    {
        var cluster = TestAuth.StartApp(db, rabbit, customize: builder =>
        {
            builder.UseSetting("Blackjack:BettingSeconds", "4");
            builder.UseSetting("Blackjack:TurnSeconds", turnSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.UseSetting("Blackjack:PauseSeconds", "0");
            builder.UseSetting("Blackjack:DealerStepMilliseconds", "0");
            builder.UseSetting("Blackjack:MaxSeats", maxSeats.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.ConfigureTestServices(services => services.AddSingleton<IBlackjackSeedSource>(_seeds));
        });
        _clusters.Add(cluster);
        return cluster;
    }

    private static BlackjackEngine Engine(CasinoCluster cluster) => cluster.Games.Services.GetRequiredService<BlackjackEngine>();

    private static async Task<(Guid UserId, Guid AccountId)> FundedAsync(CasinoCluster cluster, long chips)
    {
        var userId = Guid.NewGuid();
        var wallet = cluster.Wallet.Services.GetRequiredService<WalletService>();
        var accountId = await wallet.OpenAccountAsync(userId);
        await wallet.CreditAsync(accountId, "initial-credit", chips);
        return (userId, accountId);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string what, int seconds = 40)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (await condition())
                {
                    return;
                }
            }
            catch (Exception)
            {
                // Todavia no esta listo.
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"No se cumplio a tiempo: {what}");
    }

    private static async Task<JsonElement> StateAsync(HttpClient client, string table = Table) => await client.GetFromJsonAsync<JsonElement>($"/games/blackjack/tables/{table}");

    private static async Task<string?> PhaseAsync(HttpClient client, string table = Table)
    {
        var state = await StateAsync(client, table);
        return state.TryGetProperty("round", out var round) && round.ValueKind == JsonValueKind.Object ? round.GetProperty("phase").GetString() : null;
    }

    private static Task WaitForPhaseAsync(HttpClient client, string phase, string table = Table) =>
        WaitUntilAsync(async () => await PhaseAsync(client, table) == phase, $"la mano en {phase}");

    /// <summary>Espera a que sea el turno de un asiento.</summary>
    private static Task WaitForTurnAsync(HttpClient client, int seat) =>
        WaitUntilAsync(async () =>
        {
            var round = (await StateAsync(client)).GetProperty("round");
            return round.GetProperty("phase").GetString() == "Playing" && round.GetProperty("activeSeat").ValueKind == JsonValueKind.Number && round.GetProperty("activeSeat").GetInt32() == seat;
        }, $"el turno del asiento {seat}");

    private static Task<JsonElement> BetAsync(HttpClient client, Guid betId) => client.GetFromJsonAsync<JsonElement>($"/games/blackjack/bets/{betId}");

    private static Task WaitForBetAsync(HttpClient client, Guid betId, Func<JsonElement, bool> condition, string what) =>
        WaitUntilAsync(async () => condition(await BetAsync(client, betId)), what);

    private static Task WaitSettledAsync(HttpClient client, Guid betId) =>
        WaitForBetAsync(client, betId, b => b.GetProperty("status").GetString() == "Settled", "la apuesta cobrada");

    private static Task<HttpResponseMessage> PlaceAsync(HttpClient client, long stake, string key, string table = Table)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/games/blackjack/tables/{table}/bets") { Content = JsonContent.Create(new { stake }) };
        request.Headers.Add("Idempotency-Key", key);
        return client.SendAsync(request);
    }

    private static async Task<Guid> PlacedAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("betId").GetGuid();
    }

    private static Task<HttpResponseMessage> ActAsync(HttpClient client, Guid betId, string action) => client.PostAsync($"/games/blackjack/bets/{betId}/{action}", content: null);

    private static async Task<(long Available, long Reserved)> BalanceAsync(CasinoCluster cluster, Guid accountId)
    {
        var account = await cluster.Wallet.Services.GetRequiredService<WalletService>().GetAsync(accountId);
        return (account.Available, account.Reserved);
    }

    /// <summary>Corre una mano del motor en segundo plano, con el reparto ya elegido.</summary>
    private Task StartRound(CasinoCluster cluster, Func<int[], bool> deal, string table = Table, CancellationToken ct = default)
    {
        _seeds.Target(deal);
        return Task.Run(() => Engine(cluster).RunOneRoundAsync(table, ct), ct);
    }

    private static int[] Cards(JsonElement element) => [.. element.GetProperty("cards").EnumerateArray().Select(c => c.GetInt32())];

    [Fact]
    public async Task A_player_who_stands_on_twenty_beats_a_dealer_on_seventeen_to_nineteen_and_is_paid_one_to_one()
    {
        using var cluster = Start();
        var (userId, accountId) = await FundedAsync(cluster, 1_000);
        using var client = cluster.ClientFor(userId);

        var round = StartRound(cluster, StandWins);
        await WaitForPhaseAsync(client, "Betting");
        var betId = await PlacedAsync(await PlaceAsync(client, 100, "stand-1"));
        await WaitForTurnAsync(client, 1);

        var stand = await ActAsync(client, betId, "stand");
        Assert.Equal(HttpStatusCode.OK, stand.StatusCode);
        Assert.Equal("Stood", (await stand.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("hand").GetString());

        await round;
        await WaitSettledAsync(client, betId);
        var bet = await BetAsync(client, betId);
        Assert.Equal(("Win", 200L), (bet.GetProperty("result").GetString(), bet.GetProperty("payout").GetInt64()));
        Assert.Equal((1_100L, 0L), await BalanceAsync(cluster, accountId));
    }

    [Fact]
    public async Task A_player_who_hits_and_busts_loses_and_the_dealer_does_not_need_to_draw()
    {
        using var cluster = Start();
        var (userId, accountId) = await FundedAsync(cluster, 1_000);
        using var client = cluster.ClientFor(userId);

        var round = StartRound(cluster, HitBusts);
        await WaitForPhaseAsync(client, "Betting");
        var betId = await PlacedAsync(await PlaceAsync(client, 100, "bust-1"));
        await WaitForTurnAsync(client, 1);

        var hit = await ActAsync(client, betId, "hit");
        Assert.Equal(HttpStatusCode.OK, hit.StatusCode);
        var after = await hit.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Bust", after.GetProperty("hand").GetString());
        Assert.Equal(3, Cards(after).Length);

        await round;
        await WaitSettledAsync(client, betId);
        var bet = await BetAsync(client, betId);
        Assert.Equal(("Bust", 0L), (bet.GetProperty("result").GetString(), bet.GetProperty("payout").GetInt64()));
        Assert.Equal((900L, 0L), await BalanceAsync(cluster, accountId));
    }

    [Fact]
    public async Task A_natural_blackjack_skips_the_turn_and_pays_three_to_two()
    {
        using var cluster = Start();
        var (userId, accountId) = await FundedAsync(cluster, 1_000);
        using var client = cluster.ClientFor(userId);

        var round = StartRound(cluster, PlayerBlackjack);
        await WaitForPhaseAsync(client, "Betting");
        var betId = await PlacedAsync(await PlaceAsync(client, 100, "natural-1"));

        await round; // nadie juega: el motor reparte, paga y termina solo
        await WaitSettledAsync(client, betId);
        var bet = await BetAsync(client, betId);
        Assert.Equal(("Blackjack", 250L, "Blackjack"), (bet.GetProperty("result").GetString(), bet.GetProperty("payout").GetInt64(), bet.GetProperty("hand").GetString()));
        Assert.Equal((1_150L, 0L), await BalanceAsync(cluster, accountId));
    }

    [Fact]
    public async Task A_dealer_blackjack_is_shown_at_once_and_takes_the_bets_without_any_turn()
    {
        using var cluster = Start();
        var (userId, accountId) = await FundedAsync(cluster, 1_000);
        using var client = cluster.ClientFor(userId);

        var round = StartRound(cluster, DealerBlackjack);
        await WaitForPhaseAsync(client, "Betting");
        var betId = await PlacedAsync(await PlaceAsync(client, 100, "dealer-bj-1"));

        await round;
        await WaitSettledAsync(client, betId);
        Assert.Equal(("Lose", 0L), (
            (await BetAsync(client, betId)).GetProperty("result").GetString(),
            (await BetAsync(client, betId)).GetProperty("payout").GetInt64()));
        var dealer = (await StateAsync(client)).GetProperty("round").GetProperty("dealer");
        Assert.Equal(2, Cards(dealer).Length);
        Assert.True(BlackjackMath.IsBlackjack(Cards(dealer)));
        Assert.Equal((900L, 0L), await BalanceAsync(cluster, accountId));
    }

    [Fact]
    public async Task A_player_who_does_not_act_in_time_is_stood_automatically_and_the_hand_still_pays_out_by_the_rules()
    {
        using var cluster = Start(turnSeconds: 1);
        var (userId, accountId) = await FundedAsync(cluster, 1_000);
        using var client = cluster.ClientFor(userId);

        var round = StartRound(cluster, AnyHand);
        await WaitForPhaseAsync(client, "Betting");
        var betId = await PlacedAsync(await PlaceAsync(client, 100, "idle-1"));

        await round;
        await WaitSettledAsync(client, betId);
        var bet = await BetAsync(client, betId);
        var dealer = Cards((await StateAsync(client)).GetProperty("round").GetProperty("dealer"));
        var expected = BlackjackMath.Resolve(Cards(bet), dealer);
        Assert.Equal(expected.ToString(), bet.GetProperty("result").GetString());
        Assert.Equal(BlackjackMath.PayoutFor(100, expected), bet.GetProperty("payout").GetInt64());
        Assert.Equal((900 + BlackjackMath.PayoutFor(100, expected), 0L), await BalanceAsync(cluster, accountId));
    }

    [Fact]
    public async Task The_dealer_hole_card_stays_hidden_until_it_is_turned_and_the_seed_reveals_the_whole_shoe_afterwards()
    {
        using var cluster = Start();
        var (userId, _) = await FundedAsync(cluster, 1_000);
        using var client = cluster.ClientFor(userId);

        var round = StartRound(cluster, AnyHand);
        await WaitForPhaseAsync(client, "Betting");
        var waiting = (await StateAsync(client)).GetProperty("round");
        Assert.Equal(JsonValueKind.Null, waiting.GetProperty("bettingEndsAt").ValueKind); // sin apuestas no corre ningun reloj
        var commitment = waiting.GetProperty("commitment").GetString()!;
        var roundId = waiting.GetProperty("id").GetGuid();
        var betId = await PlacedAsync(await PlaceAsync(client, 100, "hole-1"));
        await WaitForTurnAsync(client, 1);

        // Durante el turno solo se ve UNA carta del crupier; la semilla no se conoce.
        var playing = (await StateAsync(client)).GetProperty("round");
        Assert.Single(Cards(playing.GetProperty("dealer")));
        Assert.Equal(1, playing.GetProperty("dealer").GetProperty("hiddenCards").GetInt32());
        Assert.Equal(JsonValueKind.Null, playing.GetProperty("serverSeed").ValueKind);
        Assert.DoesNotContain("encryptedSeed", playing.GetRawText(), StringComparison.OrdinalIgnoreCase);

        await ActAsync(client, betId, "stand");
        await round;
        await WaitSettledAsync(client, betId);

        // Verificacion provably fair: la semilla coincide con el compromiso publicado ANTES de apostar y reproduce el reparto.
        var revealed = await client.GetFromJsonAsync<JsonElement>($"/games/blackjack/rounds/{roundId}");
        var seed = revealed.GetProperty("serverSeed").GetString()!;
        Assert.Equal("Finished", revealed.GetProperty("phase").GetString());
        Assert.True(ProvablyFair.MatchesCommitment(seed, commitment));
        var shoe = BlackjackMath.Shoe(seed, roundId);
        var bet = await BetAsync(client, betId);
        var dealer = Cards(revealed.GetProperty("dealer"));
        Assert.Equal([shoe[0], shoe[2]], Cards(bet).Take(2));
        Assert.Equal([shoe[1], shoe[3]], dealer.Take(2));
        Assert.Equal(0, revealed.GetProperty("dealer").GetProperty("hiddenCards").GetInt32());
    }

    [Fact]
    public async Task Two_players_share_one_hand_play_in_seat_order_and_every_balance_closes()
    {
        using var cluster = Start();
        var (userOne, accountOne) = await FundedAsync(cluster, 1_000);
        var (userTwo, accountTwo) = await FundedAsync(cluster, 1_000);
        using var one = cluster.ClientFor(userOne);
        using var two = cluster.ClientFor(userTwo);

        var round = StartRound(cluster, TwoPlayers);
        await WaitForPhaseAsync(one, "Betting");
        var placed = await Task.WhenAll(PlaceAsync(one, 100, "two-1"), PlaceAsync(two, 50, "two-2"));
        var betOne = await PlacedAsync(placed[0]);
        var betTwo = await PlacedAsync(placed[1]);
        await WaitForTurnAsync(one, 1);

        // Es el turno del asiento 1: el otro jugador no puede actuar todavia.
        var seats = (await StateAsync(one)).GetProperty("seats").EnumerateArray().ToList();
        Assert.Equal(2, seats.Count);
        Assert.Single(seats, s => s.GetProperty("mine").GetBoolean());
        var other = seats.Single(s => !s.GetProperty("mine").GetBoolean());
        Assert.Equal(JsonValueKind.Null, other.GetProperty("betId").ValueKind); // el id de la apuesta ajena no se expone
        Assert.True(other.GetProperty("cards").GetArrayLength() >= 2); // pero su mano es publica en la mesa
        var mySeat = seats.Single(s => s.GetProperty("mine").GetBoolean()).GetProperty("seat").GetInt32();
        var (firstClient, firstBet, secondClient, secondBet) = mySeat == 1 ? (one, betOne, two, betTwo) : (two, betTwo, one, betOne);
        Assert.Equal(HttpStatusCode.Conflict, (await ActAsync(secondClient, secondBet, "stand")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await ActAsync(firstClient, firstBet, "stand")).StatusCode);
        await WaitForTurnAsync(one, 2);
        Assert.Equal(HttpStatusCode.OK, (await ActAsync(secondClient, secondBet, "stand")).StatusCode);

        await round;
        await WaitSettledAsync(one, betOne);
        await WaitSettledAsync(two, betTwo);
        var roundId = (await BetAsync(one, betOne)).GetProperty("roundId").GetGuid();
        Assert.Equal(roundId, (await BetAsync(two, betTwo)).GetProperty("roundId").GetGuid()); // la MISMA mano
        var dealer = Cards((await StateAsync(one)).GetProperty("round").GetProperty("dealer"));
        foreach (var (client, betId, account, stake) in new[] { (one, betOne, accountOne, 100L), (two, betTwo, accountTwo, 50L) })
        {
            var bet = await BetAsync(client, betId);
            var expected = BlackjackMath.PayoutFor(stake, BlackjackMath.Resolve(Cards(bet), dealer));
            Assert.Equal(expected, bet.GetProperty("payout").GetInt64());
            Assert.Equal((1_000 - stake + expected, 0L), await BalanceAsync(cluster, account));
        }
    }

    [Fact]
    public async Task Bets_and_actions_are_validated_and_one_player_cannot_touch_another_players_hand()
    {
        using var cluster = Start();
        var (userId, accountId) = await FundedAsync(cluster, 1_000);
        var (strangerId, _) = await FundedAsync(cluster, 1_000);
        using var client = cluster.ClientFor(userId);
        using var stranger = cluster.ClientFor(strangerId);

        // Sin mano abierta no se puede apostar, y la mesa y el monto se validan.
        Assert.Equal(HttpStatusCode.Conflict, (await PlaceAsync(client, 100, "none-1")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PlaceAsync(client, 100, "table-1", "mesa-99")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/games/blackjack/tables/mesa-99")).StatusCode);

        var round = StartRound(cluster, AnyHand);
        await WaitForPhaseAsync(client, "Betting");
        Assert.Equal(HttpStatusCode.BadRequest, (await PlaceAsync(client, 5, "low-1")).StatusCode); // la mesa pide al menos 10
        Assert.Equal(HttpStatusCode.BadRequest, (await PlaceAsync(client, 5_000, "high-1")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PlaceAsync(client, 100, "")).StatusCode);

        var betId = await PlacedAsync(await PlaceAsync(client, 100, "same-key"));
        var again = await PlaceAsync(client, 100, "same-key"); // el mismo pedido otra vez: la misma apuesta
        Assert.Equal(HttpStatusCode.Accepted, again.StatusCode);
        var repeated = await again.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal((betId, true), (repeated.GetProperty("betId").GetGuid(), repeated.GetProperty("alreadyPlaced").GetBoolean()));
        Assert.Equal(HttpStatusCode.Conflict, (await PlaceAsync(client, 200, "same-key")).StatusCode); // otra apuesta con la misma clave
        Assert.Equal(HttpStatusCode.Conflict, (await PlaceAsync(client, 100, "another-key")).StatusCode); // ya tiene un asiento en esta mano

        await WaitForTurnAsync(client, 1);
        Assert.Equal(HttpStatusCode.Conflict, (await PlaceAsync(stranger, 100, "late-1")).StatusCode); // ya se reparti
        Assert.Equal(HttpStatusCode.NotFound, (await ActAsync(stranger, betId, "hit")).StatusCode); // una mano ajena no se toca
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/games/blackjack/bets/{betId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ActAsync(client, Guid.NewGuid(), "stand")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await ActAsync(client, betId, "stand")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await ActAsync(client, betId, "hit")).StatusCode); // ya se planto

        await round;
        await WaitSettledAsync(client, betId);
        Assert.Equal(0L, (await BalanceAsync(cluster, accountId)).Reserved); // nada colgado
    }

    [Fact]
    public async Task With_nobody_betting_the_table_just_waits_without_dealing_or_running_any_clock()
    {
        using var cluster = Start();
        using var client = cluster.ClientFor(Guid.NewGuid());
        using var cancel = new CancellationTokenSource();

        var round = StartRound(cluster, AnyHand, ct: cancel.Token);
        await WaitForPhaseAsync(client, "Betting");
        await Task.Delay(TimeSpan.FromSeconds(6)); // mas que la ventana de apuestas

        var state = (await StateAsync(client)).GetProperty("round");
        Assert.Equal("Betting", state.GetProperty("phase").GetString());
        Assert.Equal(JsonValueKind.Null, state.GetProperty("bettingEndsAt").ValueKind);
        Assert.Equal(0, state.GetProperty("seatCount").GetInt32());

        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => round);
    }

    [Fact]
    public async Task A_full_table_refuses_more_players()
    {
        using var cluster = Start(maxSeats: 2);
        var players = new List<(Guid UserId, HttpClient Client)>();
        for (var i = 0; i < 3; i++)
        {
            var (userId, _) = await FundedAsync(cluster, 1_000);
            players.Add((userId, cluster.ClientFor(userId)));
        }

        try
        {
            using var cancel = new CancellationTokenSource();
            var round = StartRound(cluster, AnyHand, ct: cancel.Token);
            await WaitForPhaseAsync(players[0].Client, "Betting");
            Assert.Equal(HttpStatusCode.Accepted, (await PlaceAsync(players[0].Client, 100, "full-1")).StatusCode);
            Assert.Equal(HttpStatusCode.Accepted, (await PlaceAsync(players[1].Client, 100, "full-2")).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await PlaceAsync(players[2].Client, 100, "full-3")).StatusCode);

            await cancel.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => round);
        }
        finally
        {
            foreach (var player in players)
            {
                player.Client.Dispose();
            }
        }
    }

    [Fact]
    public async Task A_hand_cut_short_by_a_restart_gives_the_bets_back()
    {
        using var cluster = Start();
        var (userId, accountId) = await FundedAsync(cluster, 1_000);
        using var client = cluster.ClientFor(userId);
        var blackjack = cluster.Games.Services.GetRequiredService<BlackjackService>();

        var opened = await blackjack.OpenRoundAsync(Table); // sin motor: la mano queda abierta como si el servicio se hubiera caido
        var betId = await PlacedAsync(await PlaceAsync(client, 100, "recover-1"));
        await WaitUntilAsync(async () => (await BalanceAsync(cluster, accountId)).Reserved == 100, "las fichas reservadas");

        await blackjack.RecoverAsync();

        await WaitUntilAsync(async () => await BalanceAsync(cluster, accountId) == (1_000, 0), "las fichas devueltas");
        Assert.Equal("Aborted", (await client.GetFromJsonAsync<JsonElement>($"/games/blackjack/rounds/{opened.Id}")).GetProperty("phase").GetString());
        await WaitForBetAsync(client, betId, b => b.GetProperty("status").GetString() == "Settled", "la apuesta cerrada");
    }

    /// <summary>Una apuesta puesta en una mano que espera la primera apuesta (sin reloj), como si su reserva estuviera todavia en camino.</summary>
    private static async Task<Guid> InsertBetAsync(CasinoCluster cluster, Guid roundId, Guid userId, Guid accountId, bool reserved)
    {
        var bet = new BlackjackBet { Id = Guid.NewGuid(), RoundId = roundId, TableId = Table, UserId = userId, AccountId = accountId, Stake = 100, Status = RoundStatus.Placed, Reserved = reserved, PlacedAt = DateTimeOffset.UtcNow };
        await using var session = cluster.Games.Services.GetRequiredService<IDocumentStore>().LightweightSession();
        session.Insert(bet);
        await session.SaveChangesAsync();
        return bet.Id;
    }

    [Fact]
    public async Task A_reservation_that_arrives_after_the_hand_went_back_to_waiting_starts_the_betting_clock()
    {
        // Pasa con el sistema recien levantado: la primera reserva tarda mas que la ventana, la mano vuelve a esperar y la reserva llega despues.
        using var cluster = Start();
        var blackjack = cluster.Games.Services.GetRequiredService<BlackjackService>();
        var (userId, accountId) = await FundedAsync(cluster, 1_000);
        var round = await blackjack.OpenRoundAsync(Table);
        var betId = await InsertBetAsync(cluster, round.Id, userId, accountId, reserved: false);

        await blackjack.OnStakeReservedAsync(new Casino.Contracts.StakeReserved(betId, accountId, 100, Guid.NewGuid()));

        var after = await blackjack.GetRoundAsync(round.Id);
        Assert.NotNull(after.BettingEndsAt); // antes quedaba esperando para siempre con una apuesta adentro
        Assert.True((await blackjack.GetBetAsync(betId)).Reserved);
    }

    [Fact]
    public async Task A_waiting_hand_with_a_reserved_bet_but_no_clock_is_re_armed_and_one_without_bets_is_left_alone()
    {
        using var cluster = Start();
        var blackjack = cluster.Games.Services.GetRequiredService<BlackjackService>();
        var (userId, accountId) = await FundedAsync(cluster, 1_000);

        var empty = await blackjack.OpenRoundAsync(Table);
        Assert.False(await blackjack.RearmClockAsync(empty.Id)); // sin apuestas: sigue esperando, sin reloj
        Assert.Null((await blackjack.GetRoundAsync(empty.Id)).BettingEndsAt);

        var stuck = await blackjack.OpenRoundAsync("mesa-3");
        await InsertBetAsync(cluster, stuck.Id, userId, accountId, reserved: true);
        Assert.True(await blackjack.RearmClockAsync(stuck.Id));
        Assert.NotNull((await blackjack.GetRoundAsync(stuck.Id)).BettingEndsAt);
        Assert.False(await blackjack.RearmClockAsync(stuck.Id)); // ya tiene reloj
    }

    [Fact]
    public async Task The_table_list_shows_every_configured_table_and_who_is_playing()
    {
        using var cluster = Start();
        using var client = cluster.ClientFor(Guid.NewGuid());

        var tables = await client.GetFromJsonAsync<JsonElement>("/games/blackjack/tables");

        Assert.Equal(["mesa-1", "mesa-2", "mesa-3"], tables.EnumerateArray().Select(t => t.GetProperty("id").GetString()));
        Assert.All(tables.EnumerateArray(), t =>
        {
            Assert.False(string.IsNullOrWhiteSpace(t.GetProperty("name").GetString()));
            Assert.Equal(5, t.GetProperty("maxSeats").GetInt32());
        });
        using var anonymous = cluster.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/games/blackjack/tables")).StatusCode);
    }
}
