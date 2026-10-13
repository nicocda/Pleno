using System.Net;
using Casino.Api;

namespace Casino.Integration.Tests.Cluster;

/// <summary>La resiliencia del gateway hacia un servicio: reintenta lecturas, no escrituras, corta el timeout y abre el circuito.</summary>
public sealed class GatewayResilienceTests
{
    private static readonly GatewayResilienceOptions Fast = new()
    {
        AttemptTimeout = TimeSpan.FromMilliseconds(300),
        MaxRetries = 2,
        RetryDelay = TimeSpan.FromMilliseconds(5),
        MinimumThroughput = 4,
        FailureRatio = 0.5,
        SamplingDuration = TimeSpan.FromSeconds(30),
        BreakDuration = TimeSpan.FromMilliseconds(500),
    };

    private sealed class ScriptedHandler(Func<int, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public int Calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(Interlocked.Increment(ref Calls), cancellationToken);
    }

    private static HttpMessageInvoker Gateway(HttpMessageHandler inner, GatewayResilienceOptions? options = null) =>
        new(ResilientForwarderHttpClientFactory.Wrap(options ?? Fast, inner));

    private static Task<HttpResponseMessage> Respond(HttpStatusCode status) => Task.FromResult(new HttpResponseMessage(status));

    [Fact]
    public async Task A_read_is_retried_until_the_service_answers()
    {
        var inner = new ScriptedHandler((n, _) => n < 3 ? Respond(HttpStatusCode.ServiceUnavailable) : Respond(HttpStatusCode.OK));
        using var gateway = Gateway(inner);

        var response = await gateway.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://games.test/games"), default);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, inner.Calls);
    }

    [Fact]
    public async Task A_read_is_retried_after_a_connection_failure()
    {
        var inner = new ScriptedHandler((n, _) => n == 1 ? throw new HttpRequestException("connection refused") : Respond(HttpStatusCode.OK));
        using var gateway = Gateway(inner);

        var response = await gateway.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://games.test/games"), default);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task A_write_is_never_retried()
    {
        var inner = new ScriptedHandler((_, _) => Respond(HttpStatusCode.ServiceUnavailable));
        using var gateway = Gateway(inner);

        var response = await gateway.SendAsync(new HttpRequestMessage(HttpMethod.Post, "http://wallet.test/wallet/transfers"), default);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public async Task A_business_error_is_not_a_failure_and_is_not_retried()
    {
        var inner = new ScriptedHandler((_, _) => Respond(HttpStatusCode.Conflict));
        using var gateway = Gateway(inner);

        var response = await gateway.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://games.test/games"), default);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public async Task A_service_that_does_not_answer_in_time_is_cut_off()
    {
        var inner = new ScriptedHandler(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var gateway = Gateway(inner, new GatewayResilienceOptions { AttemptTimeout = TimeSpan.FromMilliseconds(100), MaxRetries = 0, MinimumThroughput = 100 });

        var started = DateTime.UtcNow;
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => gateway.SendAsync(new HttpRequestMessage(HttpMethod.Post, "http://wallet.test/wallet/me"), default));

        Assert.Equal(HttpStatusCode.GatewayTimeout, ex.StatusCode);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_service_that_keeps_failing_opens_the_circuit_and_recovers_after_the_break()
    {
        var healthy = false;
        var inner = new ScriptedHandler((_, _) => healthy ? Respond(HttpStatusCode.OK) : Respond(HttpStatusCode.ServiceUnavailable));
        using var gateway = Gateway(inner);

        for (var i = 0; i < 4; i++)
        {
            await gateway.SendAsync(new HttpRequestMessage(HttpMethod.Post, "http://wallet.test/wallet/me"), default);
        }

        // Circuito abierto: responde 503 con Retry-After sin llamar al servicio.
        var callsWhenOpen = inner.Calls;
        var rejected = await gateway.SendAsync(new HttpRequestMessage(HttpMethod.Post, "http://wallet.test/wallet/me"), default);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, rejected.StatusCode);
        Assert.NotNull(rejected.Headers.RetryAfter);
        Assert.Equal(callsWhenOpen, inner.Calls);

        // Pasado el corte y con el servicio sano, la llamada de prueba cierra el circuito.
        healthy = true;
        await Task.Delay(Fast.BreakDuration + TimeSpan.FromMilliseconds(200));
        var recovered = await gateway.SendAsync(new HttpRequestMessage(HttpMethod.Post, "http://wallet.test/wallet/me"), default);
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
    }
}
