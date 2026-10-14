using System.Net;
using Casino.Hosts.Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using static Casino.Identity.Tests.IdentityServiceFixture;

namespace Casino.Identity.Tests;

public sealed class DemoModeTests(IdentityServiceFixture fixture) : IClassFixture<IdentityServiceFixture>
{
    private const string DemoPassword = "clave-de-la-demo-123";

    private async Task<string> CreateDatabaseAsync()
    {
        var name = "demo_" + Guid.NewGuid().ToString("N")[..10];
        await using (var connection = new NpgsqlConnection(fixture.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE {name}", connection);
            await command.ExecuteNonQueryAsync();
        }

        return new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = name }.ConnectionString;
    }

    private WebApplicationFactory<IdentityServiceEntryPoint> Start(string connectionString, string? demoPassword) =>
        fixture.Factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:identitydb", connectionString);
            builder.UseSetting("Identity:SeedDevUsers", "true");
            if (demoPassword is not null)
            {
                builder.UseSetting("Identity:SeedDevUsersPassword", demoPassword);
            }
        });

    [Fact]
    public async Task En_modo_demo_los_usuarios_de_desarrollo_usan_la_clave_propia_y_no_la_publica()
    {
        var database = await CreateDatabaseAsync();

        await using (var normal = Start(database, null))
        {
            using var client = normal.CreateClient();
            Assert.Equal(HttpStatusCode.OK, (await PasswordLoginAsync(client, "jugador1", "jugador1-dev")).Response.StatusCode);
        }

        // Misma base (ya tenia al usuario con la clave publica), ahora en modo demo: la publica deja de servir.
        await using var demo = Start(database, DemoPassword);
        using var demoClient = demo.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await PasswordLoginAsync(demoClient, "jugador1", "jugador1-dev")).Response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PasswordLoginAsync(demoClient, "jugador1", DemoPassword)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PasswordLoginAsync(demoClient, "backoffice1", DemoPassword)).Response.StatusCode);
    }

    [Fact]
    public async Task Una_clave_de_demo_corta_impide_arrancar()
    {
        var database = await CreateDatabaseAsync();
        await using var demo = Start(database, "corta");

        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            using var client = demo.CreateClient();
            await client.GetAsync("/");
        });
    }
}
