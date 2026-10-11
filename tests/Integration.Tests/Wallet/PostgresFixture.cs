using Casino.Modules.Games.Application;
using Casino.Modules.Games.Infrastructure;
using Casino.Hosts.Games;
using Casino.Modules.Wallet.Application;
using Casino.Modules.Wallet.Infrastructure;
using Marten;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Casino.Integration.Tests.Wallet;

/// <summary>Un Postgres real compartido por la coleccion. Los tests de la coleccion corren en serie.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public string ConnectionString { get; private set; } = string.Empty;

    /// <summary>La base del servicio de la Wallet (cada servicio tiene la suya: ninguno lee las tablas de otro).</summary>
    public string WalletDbConnectionString { get; private set; } = string.Empty;

    /// <summary>La base del host principal (usuarios y juegos).</summary>
    public string UsersDbConnectionString { get; private set; } = string.Empty;

    /// <summary>La base del servicio de juegos.</summary>
    public string GamesDbConnectionString { get; private set; } = string.Empty;

    public IDocumentStore Store { get; private set; } = null!;

    public WalletService Wallet { get; private set; } = null!;

    /// <summary>Clave maestra de pruebas: se genera por corrida y nunca se escribe en disco.</summary>
    public string MasterKey { get; } = SeedProtector.GenerateBase64Key();

    public FairnessService Fairness { get; private set; } = null!;

    public SeedProtector Protector => SeedProtector.FromBase64Key(MasterKey);

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        ConnectionString = new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString())
        {
            MaxPoolSize = 80,
        }.ConnectionString;

        Store = DocumentStore.For(options =>
        {
            WalletMartenConfiguration.Configure(options, ConnectionString);
            GamesMartenConfiguration.Register(options, AvailableGames.All());
        });
        await Store.Storage.ApplyAllConfiguredChangesToDatabaseAsync();
        WalletDbConnectionString = await CreateDatabaseAsync("walletdb");
        UsersDbConnectionString = await CreateDatabaseAsync("usersdb");
        GamesDbConnectionString = await CreateDatabaseAsync("gamesdb");
        Wallet = new WalletService(Store, TimeProvider.System);
        Fairness = new FairnessService(Store, Protector, TimeProvider.System);
    }

    private async Task<string> CreateDatabaseAsync(string name)
    {
        await using (var conn = new NpgsqlConnection(ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE {name}", conn);
            await cmd.ExecuteNonQueryAsync();
        }

        return new NpgsqlConnectionStringBuilder(ConnectionString) { Database = name }.ConnectionString;
    }

    public async Task DisposeAsync()
    {
        Store?.Dispose();
        await _postgres.DisposeAsync();
    }

    /// <summary>Cierra a la fuerza todas las conexiones de otras sesiones (simula caidas de red o reinicios del servidor).</summary>
    public async Task<long> KillAllOtherConnectionsAsync()
    {
        var killerConnection = new NpgsqlConnectionStringBuilder(ConnectionString) { Pooling = false }.ConnectionString;
        await using var conn = new NpgsqlConnection(killerConnection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT count(pg_terminate_backend(pid)) FROM pg_stat_activity WHERE datname = current_database() AND pid <> pg_backend_pid()",
            conn);
        var killed = (long)(await cmd.ExecuteScalarAsync() ?? 0L);

        // Las conexiones muertas que quedaron en el pool romperian la primera consulta de la siguiente prueba: se descartan.
        NpgsqlConnection.ClearAllPools();
        return killed;
    }
}

[CollectionDefinition(Name)]
public sealed class WalletDbDefinition : ICollectionFixture<PostgresFixture>, ICollectionFixture<RabbitMqFixture>, ICollectionFixture<Casino.Integration.Tests.Users.KeycloakFixture>, ICollectionFixture<RedisFixture>
{
    public const string Name = "wallet-db";
}
