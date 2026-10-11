using System.Reflection;
using JasperFx;
using Marten;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Marten;
using Wolverine.RabbitMQ;
using Wolverine.RabbitMQ.Internal;

namespace Casino.Hosting;

/// <summary>Piezas de composicion comunes a todos los servicios: JSON, base de datos propia y mensajeria.</summary>
public static class CasinoHostingExtensions
{
    /// <summary>Los enums viajan como texto (por ejemplo el estado de una ronda): el front y las pruebas leen nombres, no numeros.</summary>
    public static WebApplicationBuilder AddCasinoJson(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
        return builder;
    }

    /// <summary>
    /// La base de datos PROPIA del servicio: un unico DocumentStore de Marten (con sus eventos, documentos y las tablas del
    /// outbox/inbox de Wolverine) sobre la connection string <paramref name="connectionName"/>. Ningun otro servicio lee estas tablas.
    /// </summary>
    public static WebApplicationBuilder AddCasinoData(this WebApplicationBuilder builder, string connectionName, string schemaName, Action<StoreOptions> register)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(register);

        builder.Services.AddMarten(sp =>
            {
                var connectionString = sp.GetRequiredService<IConfiguration>().GetConnectionString(connectionName)
                    ?? throw new InvalidOperationException($"Falta la connection string '{connectionName}' (la inyecta Aspire).");

                var options = new StoreOptions();
                options.Connection(connectionString);
                options.DatabaseSchemaName = schemaName;
                options.AutoCreateSchemaObjects = AutoCreate.CreateOrUpdate;
                register(options);
                return options;
            })
            .UseLightweightSessions()
            .IntegrateWithWolverine();
        return builder;
    }

    /// <summary>
    /// Wolverine del servicio: descubre los handlers de <paramref name="handlerAssemblies"/>, con inbox y outbox durables (lo recibido no se
    /// procesa dos veces y lo enviado queda guardado hasta que el broker lo confirma). Si hay RabbitMQ configurado se declara la topologia
    /// completa y <paramref name="configureRabbit"/> dice que escucha y que publica ESTE servicio.
    /// </summary>
    public static WebApplicationBuilder AddCasinoMessaging(
        this WebApplicationBuilder builder,
        string serviceName,
        IEnumerable<Assembly> handlerAssemblies,
        Action<WolverineOptions> configureRabbit)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(handlerAssemblies);
        ArgumentNullException.ThrowIfNull(configureRabbit);

        var assemblies = handlerAssemblies.ToArray();
        var rabbit = builder.Configuration.GetConnectionString("rabbitmq");
        builder.Host.UseWolverine(options =>
        {
            options.ServiceName = serviceName;
            foreach (var assembly in assemblies)
            {
                options.Discovery.IncludeAssembly(assembly);
            }

            options.Policies.UseDurableInboxOnAllListeners();
            options.Policies.UseDurableOutboxOnAllSendingEndpoints();

            if (!string.IsNullOrWhiteSpace(rabbit))
            {
                var broker = options.UseRabbitMq(new Uri(rabbit)).AutoProvision();
                options.UnknownMessageBehavior = UnknownMessageBehavior.LogOnly;
                MessagingTopology.Declare(broker);
                configureRabbit(options);
            }
        });

        return builder;
    }
}
