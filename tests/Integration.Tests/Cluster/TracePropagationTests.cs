using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using Casino.BuildingBlocks;
using Casino.Integration.Tests.Wallet;
using Casino.Modules.Wallet.Application;
using Microsoft.Extensions.DependencyInjection;

namespace Casino.Integration.Tests.Cluster;

/// <summary>
/// Una traza de punta a punta cruza la cola: la llamada HTTP que registra al jugador y el trabajo que hace la Wallet al recibir el
/// mensaje (abrir la cuenta) quedan en la MISMA traza, aunque viajen por RabbitMQ entre dos servicios.
/// </summary>
[Collection(WalletDbDefinition.Name)]
public sealed class TracePropagationTests(PostgresFixture db, RabbitMqFixture rabbit)
{
    private static async Task<bool> HasWelcomeAsync(WalletService service, Guid userId, long welcome)
    {
        try
        {
            return (await service.GetAsync(PlayerIds.WalletAccountFor(userId))).Available == welcome;
        }
        catch (Casino.Modules.Wallet.Domain.WalletDomainException)
        {
            return false; // la cuenta todavia no existe
        }
    }

    [Fact]
    public async Task A_request_and_the_message_it_triggers_in_another_service_share_one_trace()
    {
        var activities = new ConcurrentBag<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name is "Wolverine" or "Microsoft.AspNetCore",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activities.Add,
        };
        ActivitySource.AddActivityListener(listener);

        const int welcome = 250;
        using var cluster = TestAuth.StartApp(db, rabbit, welcomeChips: welcome);
        var userId = Guid.NewGuid();
        using var client = cluster.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestAuth.Token(userId));

        // Primer acceso: el host principal da de alta al jugador y publica "UserRegistered"; la Wallet lo recibe y abre la cuenta.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/me")).StatusCode);
        var service = cluster.Wallet.Services.GetRequiredService<WalletService>();
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (!await HasWelcomeAsync(service, userId, welcome))
        {
            Assert.True(DateTime.UtcNow < deadline, "la cuenta del jugador no se abrio a tiempo");
            await Task.Delay(300);
        }

        var request = activities.Single(a => a.Source.Name == "Microsoft.AspNetCore" && a.DisplayName.Contains("/me", StringComparison.Ordinal));
        var sameTrace = activities.Where(a => a.TraceId == request.TraceId && a.Source.Name == "Wolverine").ToList();

        // Hay trabajo de Wolverine en la traza de la peticion: al menos el envio y la recepcion del mensaje.
        Assert.True(sameTrace.Count >= 2, "se esperaban el envio y la recepcion en la traza de la peticion; hay: " + string.Join(" | ", sameTrace.Select(a => a.DisplayName)));
    }
}
