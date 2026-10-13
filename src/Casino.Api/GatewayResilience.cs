using System.Net;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;
using Yarp.ReverseProxy.Forwarder;

namespace Casino.Api;

/// <summary>
/// Ajustes de resiliencia del gateway hacia cada servicio (seccion <c>Gateway:Resilience</c>). Los valores por defecto sirven para
/// produccion; las pruebas los achican para no esperar.
/// </summary>
public sealed class GatewayResilienceOptions
{
    public const string SectionName = "Gateway:Resilience";

    /// <summary>Cuanto se espera la respuesta de un servicio (hasta recibir las cabeceras) antes de darla por perdida.</summary>
    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Reintentos de las lecturas (GET/HEAD) ante un fallo de transporte o un 502/503/504.</summary>
    public int MaxRetries { get; set; } = 2;

    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Proporcion de fallos (0 a 1) en la ventana de muestreo que abre el circuito.</summary>
    public double FailureRatio { get; set; } = 0.5;

    public TimeSpan SamplingDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Cantidad minima de llamadas en la ventana para que el circuito pueda abrirse (evita abrirlo por una sola falla).</summary>
    public int MinimumThroughput { get; set; } = 10;

    /// <summary>Cuanto queda abierto el circuito antes de dejar pasar una llamada de prueba.</summary>
    public TimeSpan BreakDuration { get; set; } = TimeSpan.FromSeconds(15);
}

/// <summary>
/// El cliente HTTP con el que YARP habla con cada servicio, envuelto en una pipeline de Polly: timeout por intento, reintentos de
/// lecturas y un circuito por servicio. El <c>AddStandardResilienceHandler</c> de ServiceDefaults NO alcanza al proxy (YARP crea su propio
/// cliente), por eso se envuelve aca. Las escrituras no se reintentan: el cuerpo ya se transmitio como flujo y, aunque las apuestas
/// llevan <c>Idempotency-Key</c>, un reintento ciego podria repetir algo a medio hacer; el cliente decide reintentar con la misma clave.
/// </summary>
public sealed class ResilientForwarderHttpClientFactory(GatewayResilienceOptions options) : ForwarderHttpClientFactory
{
    protected override HttpMessageHandler WrapHandler(ForwarderHttpClientContext context, HttpMessageHandler handler) =>
        Wrap(options, handler);

    /// <summary>Envuelve <paramref name="inner"/> con la pipeline de resiliencia (publico para probarla sin levantar un proxy).</summary>
    public static HttpMessageHandler Wrap(GatewayResilienceOptions options, HttpMessageHandler inner) =>
        new ResilienceHandler(BuildPipeline(options), inner);

    internal static ResiliencePipeline<HttpResponseMessage> BuildPipeline(GatewayResilienceOptions options)
    {
        static bool IsFailure(Outcome<HttpResponseMessage> outcome) =>
            outcome.Exception is HttpRequestException or TimeoutRejectedException
            || outcome.Result is { StatusCode: HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout };

        var builder = new ResiliencePipelineBuilder<HttpResponseMessage>();
        if (options.MaxRetries > 0)
        {
            builder
                .AddRetry(new RetryStrategyOptions<HttpResponseMessage>
                {
                    MaxRetryAttempts = options.MaxRetries,
                    Delay = options.RetryDelay,
                    BackoffType = DelayBackoffType.Exponential,
                    UseJitter = true,
                    ShouldHandle = args => ValueTask.FromResult(
                        IsFailure(args.Outcome)
                        && args.Context.Properties.TryGetValue(ResilienceHandler.RequestKey, out var request)
                        && request.Method.IsRead()),
                });
        }

        return builder
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions<HttpResponseMessage>
            {
                FailureRatio = options.FailureRatio,
                SamplingDuration = options.SamplingDuration,
                MinimumThroughput = options.MinimumThroughput,
                BreakDuration = options.BreakDuration,
                ShouldHandle = args => ValueTask.FromResult(IsFailure(args.Outcome)),
            })
            .AddTimeout(options.AttemptTimeout)
            .Build();
    }

    private sealed class ResilienceHandler(ResiliencePipeline<HttpResponseMessage> pipeline, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public static readonly ResiliencePropertyKey<HttpRequestMessage> RequestKey = new("gateway.request");

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var context = ResilienceContextPool.Shared.Get(cancellationToken);
            context.Properties.Set(RequestKey, request);
            try
            {
                return await pipeline.ExecuteAsync(
                    static async (ctx, state) => await state.Self.SendInnerAsync(state.Request, ctx.CancellationToken),
                    context,
                    (Self: this, Request: request));
            }
            catch (BrokenCircuitException)
            {
                // Circuito abierto: se responde de inmediato un 503 en vez de seguir golpeando a un servicio caido.
                var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { RequestMessage = request };
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(5));
                return response;
            }
            catch (TimeoutRejectedException ex)
            {
                throw new HttpRequestException("El servicio no respondio a tiempo.", ex, HttpStatusCode.GatewayTimeout);
            }
            finally
            {
                ResilienceContextPool.Shared.Return(context);
            }
        }

        private Task<HttpResponseMessage> SendInnerAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            base.SendAsync(request, cancellationToken);
    }
}

internal static class HttpMethodExtensions
{
    public static bool IsRead(this HttpMethod method) => method == HttpMethod.Get || method == HttpMethod.Head;
}
