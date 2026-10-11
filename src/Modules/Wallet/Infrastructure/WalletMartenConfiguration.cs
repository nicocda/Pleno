using Casino.Modules.Wallet.Application;
using Casino.Modules.Wallet.Domain;
using JasperFx;
using Marten;

namespace Casino.Modules.Wallet.Infrastructure;

public static class WalletMartenConfiguration
{
    public const string SchemaName = "wallet";

    /// <summary>Configuracion completa para usar la Wallet sola (por ejemplo en pruebas): conexion, esquema y tipos.</summary>
    public static void Configure(StoreOptions options, string connectionString)
    {
        options.Connection(connectionString);
        options.DatabaseSchemaName = SchemaName;
        options.AutoCreateSchemaObjects = AutoCreate.CreateOrUpdate;
        Register(options);
    }

    /// <summary>Registra los tipos del modulo en un store compartido por el host (la conexion y el esquema los decide quien compone).</summary>
    public static void Register(StoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.Events.AddEventTypes(
        [
            typeof(AccountOpened),
            typeof(ChipsCredited),
            typeof(ChipsTransferred),
            typeof(ReservationPaidOut),
            typeof(BetReserved),
            typeof(BetSettled),
            typeof(ReservationReleased),
            typeof(OperationReversed),
        ]);

        options.Schema.For<IdempotencyRecord>().Identity(r => r.Id);
        options.Schema.For<AuditEntry>().Identity(e => e.Id);
        options.Schema.For<AuditEntry>().Index(e => e.OccurredAt);
        options.Schema.For<HierarchyNode>().Identity(n => n.Id);
        options.Schema.For<HierarchyNode>().Index(n => n.ParentUserId);
        options.Schema.For<CommissionRecord>().Identity(c => c.Id);
        options.Schema.For<CommissionRecord>().Index(c => c.OccurredAt);
        options.Schema.For<WithdrawalRequest>().Identity(w => w.Id);
        options.Schema.For<WithdrawalRequest>().Index(w => w.PlayerUserId);
        options.Schema.For<WithdrawalRequest>().Index(w => w.ApproverUserId);
    }
}

/// <summary>
/// Segunda barrera de idempotencia: PK unica por (cuenta, clave) en la misma transaccion que los eventos.
/// La primera es el chequeo dentro del stream.
/// </summary>
public sealed class IdempotencyRecord
{
    public required string Id { get; init; }

    public required Guid AccountId { get; init; }

    public required string Key { get; init; }

    public required Guid TransactionId { get; init; }

    public required string Fingerprint { get; init; }

    public required DateTimeOffset RecordedAt { get; init; }

    public static string BuildId(Guid accountId, string key) => $"{accountId:N}:{key}";
}
