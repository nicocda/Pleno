using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Casino.BuildingBlocks;
using Casino.Integration.Tests.Wallet;
using Casino.Modules.Wallet.Application;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Casino.Integration.Tests.Cluster;

/// <summary>
/// Lo que cambia al separar la Wallet en un servicio propio: bases de datos independientes, cada servicio valida el token por su
/// cuenta, una Wallet caida no tumba el resto y el orden de arranque no pierde mensajes.
/// </summary>
[Collection(WalletDbDefinition.Name)]
public sealed class ServiceSeparationTests(PostgresFixture db, RabbitMqFixture rabbit) : IDisposable
{
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }
    }

    private CasinoCluster StartCluster(int welcomeChips = 0)
    {
        var cluster = TestAuth.StartApp(db, rabbit, welcomeChips: welcomeChips);
        _disposables.Add(cluster);
        return cluster;
    }

    private static async Task<HashSet<string>> SchemasAsync(string connectionString)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT DISTINCT table_schema FROM information_schema.tables WHERE table_schema NOT IN ('pg_catalog', 'information_schema')", conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        var schemas = new HashSet<string>();
        while (await reader.ReadAsync())
        {
            schemas.Add(reader.GetString(0));
        }

        return schemas;
    }

    private static async Task<bool> TableExistsAsync(string connectionString, string schema, string table)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM information_schema.tables WHERE table_schema = @s AND table_name = @t", conn);
        cmd.Parameters.AddWithValue("s", schema);
        cmd.Parameters.AddWithValue("t", table);
        return (long)(await cmd.ExecuteScalarAsync())! > 0;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string what, int seconds = 60)
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

    private static HttpClient WithToken(HttpClient client, string token)
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task Each_service_has_its_own_database_and_none_holds_the_tables_of_the_other()
    {
        using var cluster = StartCluster();
        // Marten crea las tablas al primer uso: se usa cada servicio una vez (la Wallet, los usuarios y los juegos).
        var userId = Guid.NewGuid();
        var accountId = await cluster.Wallet.Services.GetRequiredService<WalletService>().OpenAccountAsync(userId);
        await cluster.Wallet.Services.GetRequiredService<WalletService>().CreditAsync(accountId, "schema-check", 1);
        using (var client = cluster.ClientFor(userId))
        {
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/me")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/games/roulette/rounds")).StatusCode);
        }

        var walletSchemas = await SchemasAsync(db.WalletDbConnectionString);
        var usersSchemas = await SchemasAsync(db.UsersDbConnectionString);
        var gamesSchemas = await SchemasAsync(db.GamesDbConnectionString);

        // La Wallet: sus eventos y su idempotencia. Nada de usuarios ni de juegos.
        Assert.Contains("wallet", walletSchemas);
        Assert.True(await TableExistsAsync(db.WalletDbConnectionString, "wallet", "mt_events"));
        Assert.True(await TableExistsAsync(db.WalletDbConnectionString, "wallet", "mt_doc_idempotencyrecord"));
        Assert.DoesNotContain("casino", walletSchemas);
        Assert.DoesNotContain("games", walletSchemas);
        Assert.False(await TableExistsAsync(db.WalletDbConnectionString, "wallet", "mt_doc_userprofile"));
        Assert.False(await TableExistsAsync(db.WalletDbConnectionString, "wallet", "mt_doc_rouletteround"));

        // El host principal: solo usuarios. Ni rastro de las tablas de la Wallet ni de los juegos.
        Assert.Contains("casino", usersSchemas);
        Assert.True(await TableExistsAsync(db.UsersDbConnectionString, "casino", "mt_doc_userprofile"));
        Assert.DoesNotContain("wallet", usersSchemas);
        Assert.DoesNotContain("games", usersSchemas);
        Assert.False(await TableExistsAsync(db.UsersDbConnectionString, "casino", "mt_doc_idempotencyrecord"));
        Assert.False(await TableExistsAsync(db.UsersDbConnectionString, "casino", "mt_doc_rouletteround"));

        // Los juegos: las semillas (equidad) y las rondas de cada juego. Ni rastro de usuarios ni de la Wallet.
        Assert.Contains("games", gamesSchemas);
        Assert.True(await TableExistsAsync(db.GamesDbConnectionString, "games", "mt_doc_rouletteround"));
        Assert.True(await TableExistsAsync(db.GamesDbConnectionString, "games", "mt_events"));
        Assert.DoesNotContain("wallet", gamesSchemas);
        Assert.DoesNotContain("casino", gamesSchemas);
        Assert.False(await TableExistsAsync(db.GamesDbConnectionString, "games", "mt_doc_userprofile"));
        Assert.False(await TableExistsAsync(db.GamesDbConnectionString, "games", "mt_doc_idempotencyrecord"));
    }

    [Fact]
    public async Task The_wallet_validates_the_token_itself_and_does_not_trust_the_gateway()
    {
        using var cluster = StartCluster();
        var userId = Guid.NewGuid();
        await cluster.Wallet.Services.GetRequiredService<WalletService>().OpenAccountAsync(userId);
        using var direct = cluster.Wallet.CreateClient(); // sin pasar por el gateway

        // Sin token, con un token firmado por otro emisor o para otro destinatario: la Wallet los rechaza por su cuenta.
        Assert.Equal(HttpStatusCode.Unauthorized, (await direct.GetAsync("/wallet/me")).StatusCode);
        using var foreign = WithToken(cluster.Wallet.CreateClient(), TestAuth.Token(userId, signingKey: TestAuth.NewForeignKey()));
        Assert.Equal(HttpStatusCode.Unauthorized, (await foreign.GetAsync("/wallet/me")).StatusCode);
        using var wrongAudience = WithToken(cluster.Wallet.CreateClient(), TestAuth.Token(userId, audience: "otra-api"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await wrongAudience.GetAsync("/wallet/me")).StatusCode);

        // Un token valido sirve, y el rol se exige en la Wallet: un jugador no entra al backoffice aunque llegue directo.
        using var player = WithToken(cluster.Wallet.CreateClient(), TestAuth.Token(userId));
        Assert.Equal(HttpStatusCode.OK, (await player.GetAsync("/wallet/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await player.GetAsync("/backoffice/wallet/audit")).StatusCode);
    }

    [Fact]
    public async Task The_gateway_forwards_wallet_requests_with_the_token_and_the_answer_matches_the_service()
    {
        using var cluster = StartCluster();
        var userId = Guid.NewGuid();
        var accountId = await cluster.Wallet.Services.GetRequiredService<WalletService>().OpenAccountAsync(userId);
        await cluster.Wallet.Services.GetRequiredService<WalletService>().CreditAsync(accountId, "gw-credit", 321);
        using var viaGateway = cluster.ClientFor(userId);
        using var direct = WithToken(cluster.Wallet.CreateClient(), TestAuth.Token(userId));

        var gateway = await viaGateway.GetFromJsonAsync<JsonElement>("/wallet/me");
        var service = await direct.GetFromJsonAsync<JsonElement>("/wallet/me");

        Assert.Equal(321, gateway.GetProperty("available").GetInt64());
        Assert.Equal(service.GetRawText(), gateway.GetRawText());
        // Sin token, el rechazo viene de la Wallet y el gateway lo devuelve tal cual.
        using var anonymous = cluster.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/wallet/me")).StatusCode);
        // El backoffice entra por el gateway a las rutas /backoffice/wallet.
        using var admin = cluster.ClientFor(Guid.NewGuid(), Roles.Backoffice);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/backoffice/wallet/users/{userId}")).StatusCode);
    }

    [Fact]
    public async Task The_wallet_does_not_expose_what_belongs_to_other_services()
    {
        using var cluster = StartCluster();
        using var direct = WithToken(cluster.Wallet.CreateClient(), TestAuth.Token(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.NotFound, (await direct.GetAsync("/games/slots/paytable")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await direct.GetAsync("/games/roulette/rounds")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await direct.GetAsync("/me")).StatusCode);
    }

    [Fact]
    public async Task A_wallet_that_is_down_does_not_take_the_rest_of_the_api_down()
    {
        using var cluster = StartCluster();
        var userId = Guid.NewGuid();
        await cluster.Wallet.Services.GetRequiredService<WalletService>().OpenAccountAsync(userId);
        using var client = cluster.ClientFor(userId);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/wallet/me")).StatusCode);

        await cluster.Wallet.DisposeAsync(); // se cae el servicio de la Wallet

        // Lo que depende de la Wallet falla como un error del gateway (5xx), no como un cuelgue...
        Assert.True((int)(await client.GetAsync("/wallet/me")).StatusCode >= 500);
        // ...y lo que no depende de ella sigue funcionando.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/games/slots/paytable")).StatusCode);
    }

    [Fact]
    public async Task A_player_registered_while_the_wallet_is_not_running_gets_the_account_when_it_starts()
    {
        const int welcome = 250;
        // 1) Arranca SOLO el host principal: el jugador entra y se publica "UserRegistered" sin que la Wallet exista todavia.
        var api = TestAuth.StartApiHost(db, rabbit);
        _disposables.Add(api);
        var userId = Guid.NewGuid();
        using (var client = api.CreateClient())
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestAuth.Token(userId));
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/me")).StatusCode);
        }

        // 2) Recien ahora arranca la Wallet: el mensaje esperaba en su cola (la topologia la declara cualquiera de los dos).
        var wallet = TestAuth.StartWalletHost(db, rabbit, welcomeChips: welcome);
        _disposables.Add(wallet);
        var service = wallet.Services.GetRequiredService<WalletService>();

        await WaitUntilAsync(
            async () => (await service.GetAsync(PlayerIds.WalletAccountFor(userId))).Available == welcome,
            "la cuenta del jugador se abre con las fichas de bienvenida");
    }

    [Fact]
    public async Task The_games_service_validates_the_token_itself_and_does_not_trust_the_gateway()
    {
        using var cluster = StartCluster();
        var userId = Guid.NewGuid();
        using var direct = cluster.Games.CreateClient(); // sin pasar por el gateway

        Assert.Equal(HttpStatusCode.Unauthorized, (await direct.GetAsync("/games")).StatusCode);
        using var foreign = WithToken(cluster.Games.CreateClient(), TestAuth.Token(userId, signingKey: TestAuth.NewForeignKey()));
        Assert.Equal(HttpStatusCode.Unauthorized, (await foreign.GetAsync("/games")).StatusCode);
        using var player = WithToken(cluster.Games.CreateClient(), TestAuth.Token(userId));
        Assert.Equal(HttpStatusCode.OK, (await player.GetAsync("/games")).StatusCode);
        // El rol tambien se exige en el servicio de juegos: la administracion de la tragamonedas es del backoffice.
        Assert.Equal(HttpStatusCode.Forbidden, (await player.GetAsync("/backoffice/games/slots/settings")).StatusCode);
        using var admin = WithToken(cluster.Games.CreateClient(), TestAuth.Token(Guid.NewGuid(), roles: [Roles.Backoffice]));
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/backoffice/games/slots/settings")).StatusCode);
    }

    [Fact]
    public async Task The_gateway_forwards_games_fairness_and_the_games_backoffice_to_the_games_service()
    {
        using var cluster = StartCluster();
        var userId = Guid.NewGuid();
        using var player = cluster.ClientFor(userId);
        using var admin = cluster.ClientFor(Guid.NewGuid(), Roles.Backoffice);

        Assert.Equal(HttpStatusCode.OK, (await player.GetAsync("/games")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await player.GetAsync("/games/slots/paytable")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await player.GetAsync("/fairness/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/backoffice/games/slots/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await player.GetAsync("/backoffice/games/slots/settings")).StatusCode);
        using var anonymous = cluster.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/games")).StatusCode);
    }

    [Fact]
    public async Task The_games_service_does_not_expose_what_belongs_to_other_services()
    {
        using var cluster = StartCluster();
        using var direct = WithToken(cluster.Games.CreateClient(), TestAuth.Token(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.NotFound, (await direct.GetAsync("/wallet/me")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await direct.GetAsync("/me")).StatusCode);
    }

    [Fact]
    public async Task A_games_service_that_is_down_does_not_take_the_wallet_or_the_users_down()
    {
        using var cluster = StartCluster();
        var userId = Guid.NewGuid();
        await cluster.Wallet.Services.GetRequiredService<WalletService>().OpenAccountAsync(userId);
        using var client = cluster.ClientFor(userId);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/games")).StatusCode);

        await cluster.Games.DisposeAsync(); // se cae el servicio de juegos

        Assert.True((int)(await client.GetAsync("/games")).StatusCode >= 500);
        Assert.True((int)(await client.GetAsync("/games/slots/paytable")).StatusCode >= 500);
        // La Wallet y los usuarios siguen funcionando.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/wallet/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/me")).StatusCode);
    }
}
