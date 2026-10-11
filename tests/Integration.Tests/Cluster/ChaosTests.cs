using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Casino.Integration.Tests.Wallet;
using Casino.Modules.Wallet.Application;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Casino.Integration.Tests.Cluster;

/// <summary>
/// Chaos: se tira un servicio a mitad de una apuesta y se lo vuelve a levantar. Lo que importa es que el saldo cierra: las fichas
/// reservadas se liquidan una sola vez (outbox/inbox durables e idempotencia) y nunca queda nada colgado ni duplicado.
/// </summary>
[Collection(WalletDbDefinition.Name)]
public sealed class ChaosTests(PostgresFixture db, RabbitMqFixture rabbit) : IDisposable
{
    private const long Initial = 1_000;
    private const long Stake = 100;
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }
    }

    private T Track<T>(T disposable) where T : IDisposable
    {
        _disposables.Add(disposable);
        return disposable;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string what, int seconds = 90)
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

            await Task.Delay(300);
        }

        throw new TimeoutException($"No se cumplio a tiempo: {what}");
    }

    private static HttpClient DirectClient<T>(WebApplicationFactory<T> service, Guid userId) where T : class
    {
        var client = service.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestAuth.Token(userId));
        return client;
    }

    private static async Task<Guid> SpinAsync(HttpClient client, string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/games/slots/spins") { Content = JsonContent.Create(new { stake = Stake }) };
        request.Headers.Add("Idempotency-Key", key);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("betId").GetGuid();
    }

    private static async Task<string?> StatusAsync(HttpClient client, Guid betId) =>
        (await client.GetFromJsonAsync<JsonElement>($"/games/slots/spins/{betId}")).GetProperty("status").GetString();

    /// <summary>El saldo cierra: lo apostado salio una vez, el premio entro una vez y no queda nada reservado.</summary>
    private static async Task AssertBalanceClosesAsync(WalletService wallet, Guid accountId, HttpClient games, Guid betId)
    {
        var payout = (await games.GetFromJsonAsync<JsonElement>($"/games/slots/spins/{betId}")).GetProperty("payout").GetInt64();
        await WaitUntilAsync(async () => (await wallet.GetAsync(accountId)).Reserved == 0, "no queda nada reservado");
        var account = await wallet.GetAsync(accountId);
        Assert.Equal((Initial - Stake + payout, 0L), (account.Available, account.Reserved));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(40)]
    [InlineData(150)]
    public async Task The_games_service_dies_mid_bet_and_when_it_comes_back_the_bet_settles_once_and_the_balance_closes(int millisecondsBeforeTheCrash)
    {
        var walletHost = Track(new Holder<WebApplicationFactory<Casino.Hosts.Wallet.WalletServiceEntryPoint>>(TestAuth.StartWalletHost(db, rabbit, welcomeChips: 0)));
        var gamesHost = new Holder<WebApplicationFactory<Casino.Hosts.Games.GamesServiceEntryPoint>>(TestAuth.StartGamesHost(db, rabbit));
        var api = Track(new Holder<WebApplicationFactory<Casino.Hosts.Api.ApiEntryPoint>>(TestAuth.StartApiHost(
            db,
            rabbit,
            new Dictionary<string, HttpMessageHandler>
            {
                ["wallet"] = walletHost.Value.Server.CreateHandler(),
                ["games"] = gamesHost.Value.Server.CreateHandler(),
            })));
        _disposables.Add(gamesHost);

        var userId = Guid.NewGuid();
        var wallet = walletHost.Value.Services.GetRequiredService<WalletService>();
        var accountId = await wallet.OpenAccountAsync(userId);
        await wallet.CreditAsync(accountId, "chaos-initial", Initial);

        // El jugador apuesta y, a los pocos milisegundos, se cae el servicio de juegos (sin apagado ordenado: lo tiran).
        using var viaGateway = DirectClient(api.Value, userId);
        var betId = await SpinAsync(viaGateway, $"chaos-{millisecondsBeforeTheCrash}");
        await Task.Delay(millisecondsBeforeTheCrash);
        await gamesHost.Value.DisposeAsync();

        // Vuelve el servicio: retoma lo que quedo pendiente en su outbox/inbox y la apuesta se cierra sola.
        var revived = Track(new Holder<WebApplicationFactory<Casino.Hosts.Games.GamesServiceEntryPoint>>(TestAuth.StartGamesHost(db, rabbit)));
        using var games = DirectClient(revived.Value, userId);
        await WaitUntilAsync(async () => await StatusAsync(games, betId) == "Settled", "la apuesta llega a Settled tras reiniciar los juegos");

        await AssertBalanceClosesAsync(wallet, accountId, games, betId);
    }

    /// <summary>Envuelve una fabrica para poder registrarla en la lista de descartables.</summary>
    private sealed class Holder<T>(T value) : IDisposable, IAsyncDisposable where T : IAsyncDisposable, IDisposable
    {
        public T Value { get; } = value;

        public void Dispose() => Value.Dispose();

        public ValueTask DisposeAsync() => Value.DisposeAsync();
    }
}
