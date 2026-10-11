using System.Net.Http.Headers;
using System.Security.Cryptography;
using Casino.Integration.Tests.Wallet;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Casino.Hosts.Api;
using Casino.Hosts.Games;
using Casino.Hosts.Wallet;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Yarp.ReverseProxy.Forwarder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Casino.Integration.Tests;

/// <summary>
/// Emite tokens JWT de prueba con una clave aleatoria por corrida (nunca se guarda en disco) y configura la API para
/// confiar en ella. La validacion que corre es la REAL de JwtBearer: firma, emisor, destinatario, vigencia y el
/// mapeo de roles de Keycloak. Solo cambia de donde sale la clave publica.
/// </summary>
public static class TestAuth
{
    public const string Issuer = "https://test-issuer.local/realms/casino";
    public const string Audience = "casino-api";

    private static readonly SymmetricSecurityKey SigningKey = new(RandomNumberGenerator.GetBytes(32));

    /// <summary>Un token como el que entregaria Keycloak: "sub", "preferred_username" y roles dentro de "realm_access".</summary>
    public static string Token(
        Guid userId,
        string username = "tester",
        string[]? roles = null,
        string audience = Audience,
        TimeSpan? lifetime = null,
        SecurityKey? signingKey = null,
        string? subject = null,
        object? realmAccess = null)
    {
        var now = DateTime.UtcNow;
        var lifespan = lifetime ?? TimeSpan.FromMinutes(5);
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = audience,
            // Un token ya vencido necesita fecha de inicio anterior a la de vencimiento.
            NotBefore = lifespan < TimeSpan.Zero ? now.Add(lifespan).AddMinutes(-1) : now,
            Expires = now.Add(lifespan),
            IssuedAt = now,
            Claims = new Dictionary<string, object>
            {
                ["sub"] = subject ?? userId.ToString(),
                ["preferred_username"] = username,
                ["realm_access"] = realmAccess ?? new Dictionary<string, object> { ["roles"] = roles ?? [Casino.BuildingBlocks.Roles.Player] },
            },
            SigningCredentials = new SigningCredentials(signingKey ?? SigningKey, SecurityAlgorithms.HmacSha256),
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    public static SymmetricSecurityKey NewForeignKey() => new(RandomNumberGenerator.GetBytes(32));

    /// <summary>Configura la API de prueba para aceptar los tokens de <see cref="Token"/>.</summary>
    public static void Configure(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseSetting("Authentication:Authority", Issuer);
        builder.ConfigureServices(services => services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
        {
            options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(
                new OpenIdConnectConfiguration { Issuer = Issuer });
            options.TokenValidationParameters.IssuerSigningKey = SigningKey;
            options.TokenValidationParameters.ValidIssuer = Issuer;
        }));
    }

    public static HttpClient ClientFor(this CasinoCluster app, Guid userId, params string[] roles)
    {
        ArgumentNullException.ThrowIfNull(app);

        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            Token(userId, roles: roles.Length == 0 ? [Casino.BuildingBlocks.Roles.Player] : roles));
        return client;
    }

    /// <summary>Como <see cref="ClientFor"/> pero con el nombre de usuario que trae el token (preferred_username).</summary>
    public static HttpClient NamedClientFor(this CasinoCluster app, Guid userId, string username, params string[] roles)
    {
        ArgumentNullException.ThrowIfNull(app);

        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            Token(userId, username, roles.Length == 0 ? [Casino.BuildingBlocks.Roles.Player] : roles));
        return client;
    }

    /// <summary>
    /// El sistema completo con Postgres (y RabbitMQ si se indica) reales y autenticacion de prueba: el host principal (gateway, usuarios,
    /// juegos, tiempo real) y el servicio de la Wallet, cada uno con su base de datos y hablando por RabbitMQ. Sin RabbitMQ no hay
    /// comunicacion entre servicios: solo sirve para lo que no cruza el limite.
    /// </summary>
    public static CasinoCluster StartApp(
        PostgresFixture db,
        RabbitMqFixture? rabbit = null,
        int? welcomeChips = 0,
        Action<IWebHostBuilder>? customize = null,
        bool testIssuer = true,
        string? redis = null)
    {
        ArgumentNullException.ThrowIfNull(db);

        // La Wallet y los juegos arrancan primero (y escuchan sus colas); su "cable" en memoria se le entrega al proxy del gateway.
        var wallet = StartWalletHost(db, rabbit, welcomeChips, customize, testIssuer);
        var games = StartGamesHost(db, rabbit, customize, testIssuer);
        var api = StartApiHost(
            db,
            rabbit,
            new Dictionary<string, HttpMessageHandler> { ["wallet"] = wallet.Server.CreateHandler(), ["games"] = games.Server.CreateHandler() },
            customize,
            testIssuer,
            redis);
        return new CasinoCluster(api, wallet, games);
    }

    /// <summary>Solo el servicio de la Wallet (su base walletdb y su conexion a RabbitMQ).</summary>
    public static WebApplicationFactory<WalletServiceEntryPoint> StartWalletHost(
        PostgresFixture db,
        RabbitMqFixture? rabbit = null,
        int? welcomeChips = 0,
        Action<IWebHostBuilder>? customize = null,
        bool testIssuer = true)
    {
        ArgumentNullException.ThrowIfNull(db);

        return new WebApplicationFactory<WalletServiceEntryPoint>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:walletdb", db.WalletDbConnectionString);
            if (rabbit is not null)
            {
                builder.UseSetting("ConnectionStrings:rabbitmq", rabbit.ConnectionString);
            }

            if (welcomeChips is { } chips)
            {
                builder.UseSetting("Wallet:WelcomeChips", chips.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            if (testIssuer)
            {
                Configure(builder);
            }

            customize?.Invoke(builder);
        });
    }

    /// <summary>Solo el servicio de juegos (su base gamesdb, la clave maestra de las semillas y su conexion a RabbitMQ).</summary>
    public static WebApplicationFactory<GamesServiceEntryPoint> StartGamesHost(
        PostgresFixture db,
        RabbitMqFixture? rabbit = null,
        Action<IWebHostBuilder>? customize = null,
        bool testIssuer = true)
    {
        ArgumentNullException.ThrowIfNull(db);

        return new WebApplicationFactory<GamesServiceEntryPoint>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:gamesdb", db.GamesDbConnectionString);
            builder.UseSetting("Fairness:MasterKey", db.MasterKey);
            // El motor de Crash no corre solo en las pruebas (daria rondas y mensajes de fondo a todos los tests): las que lo necesitan lo prenden.
            builder.UseSetting("Crash:EngineEnabled", "false");
            builder.UseSetting("Blackjack:EngineEnabled", "false");
            builder.UseSetting("Tables:EngineEnabled", "false");
            if (rabbit is not null)
            {
                builder.UseSetting("ConnectionStrings:rabbitmq", rabbit.ConnectionString);
            }

            if (testIssuer)
            {
                Configure(builder);
            }

            customize?.Invoke(builder);
        });
    }

    /// <summary>
    /// Solo el host principal (su base usersdb). Con <paramref name="serviceHandlers"/> (por nombre de cluster: "wallet", "games") el proxy del gateway
    /// reenvia a cada servicio por ese cable en memoria; sin ellos, esas rutas no tienen a quien llegar.
    /// </summary>
    public static WebApplicationFactory<ApiEntryPoint> StartApiHost(
        PostgresFixture db,
        RabbitMqFixture? rabbit = null,
        IReadOnlyDictionary<string, HttpMessageHandler>? serviceHandlers = null,
        Action<IWebHostBuilder>? customize = null,
        bool testIssuer = true,
        string? redis = null)
    {
        ArgumentNullException.ThrowIfNull(db);

        return new WebApplicationFactory<ApiEntryPoint>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:usersdb", db.UsersDbConnectionString);
            builder.UseSetting("ReverseProxy:Clusters:wallet:Destinations:wallet:Address", CasinoCluster.WalletAddress);
            builder.UseSetting("ReverseProxy:Clusters:games:Destinations:games:Address", CasinoCluster.GamesAddress);
            if (serviceHandlers is not null)
            {
                builder.ConfigureTestServices(services => services.AddSingleton<IForwarderHttpClientFactory>(new CasinoCluster.InMemoryForwarderHttpClientFactory(serviceHandlers)));
            }

            if (rabbit is not null)
            {
                builder.UseSetting("ConnectionStrings:rabbitmq", rabbit.ConnectionString);
            }

            if (redis is not null)
            {
                builder.UseSetting("ConnectionStrings:redis", redis);
            }

            if (testIssuer)
            {
                Configure(builder);
            }

            customize?.Invoke(builder);
        });
    }
}
