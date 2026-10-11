using Casino.BuildingBlocks;
using Casino.Modules.Games.Api;
using Casino.Modules.Games.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Casino.Modules.Games.Tables;

/// <summary>Crear una mesa. La identidad sale del token; el cuerpo solo dice como es la mesa.</summary>
public sealed record CreateTableBody(long BuyIn, int MaxPlayers, bool IsPrivate = false, string? Name = null);

public sealed record JoinTableBody(string? Code = null);

public sealed record TableCreatedResponse(Guid TableId, string? JoinCode, bool AlreadyCreated);

/// <summary>
/// Los endpoints comunes de todo juego de mesas entre jugadores, bajo <c>/games/{id}</c>. Cada juego los obtiene gratis al ser un <see cref="TableGameModule"/>; solo
/// agrega lo suyo (si hace falta). La jugada (<c>/action</c>) es el JSON propio de cada juego.
/// </summary>
internal static class TableEndpoints
{
    private const string IdempotencyHeader = "Idempotency-Key";
    private const int MaxActionBytes = 2_048;
    private const int MaxHistory = 50;

    public static void Map(IEndpointRouteBuilder app, string gameId)
    {
        var group = app.MapGroup($"/games/{gameId}").WithTags($"{gameId} (mesas)");
        group.RequireAuthorization(policy => policy.RequireRole(Roles.Player));
        group.AddEndpointFilter(GamesEndpointFilters.MapDomainErrors);

        // Las mesas abiertas y las propias en curso.
        group.MapGet("/tables", async (HttpContext http, TableService tables, CancellationToken ct) =>
            Results.Ok(await tables.ListAsync(gameId, http.User.GetUserId(), ct)));

        // Cuales son las reglas basicas del juego (para armar el formulario de "crear mesa").
        group.MapGet("/rules", (TableService tables) =>
        {
            var game = tables.GameOf(gameId);
            return Results.Ok(new { game.MinPlayers, game.MaxPlayers, game.MinBuyIn, game.MaxBuyIn });
        });

        group.MapPost("/tables", async (CreateTableBody body, HttpContext http, TableService tables, CancellationToken ct) =>
        {
            var userId = http.User.GetUserId();
            var created = await tables.CreateAsync(userId, PlayerIds.WalletAccountFor(userId), gameId, body.BuyIn, body.MaxPlayers, body.IsPrivate, body.Name, http.Request.Headers[IdempotencyHeader].ToString(), ct);
            return Results.Accepted($"/games/{gameId}/tables/{created.TableId}", new TableCreatedResponse(created.TableId, created.JoinCode, created.AlreadyCreated));
        });

        // Lo que ve MI asiento: la mesa, quienes juegan y las reglas del juego (sin cartas ajenas).
        group.MapGet("/tables/{tableId:guid}", async (Guid tableId, HttpContext http, TableService tables, CancellationToken ct) =>
            Results.Ok(await tables.GetViewAsync(http.User.GetUserId(), tableId, ct)));

        // Una mesa privada no se lista: se entra con su codigo y la respuesta dice a que mesa.
        group.MapPost("/tables/join", async (JoinTableBody body, HttpContext http, TableService tables, CancellationToken ct) =>
        {
            var userId = http.User.GetUserId();
            var tableId = await tables.JoinByCodeAsync(userId, PlayerIds.WalletAccountFor(userId), gameId, body.Code ?? string.Empty, ct);
            return Results.Accepted($"/games/{gameId}/tables/{tableId}", new { tableId });
        });

        group.MapPost("/tables/{tableId:guid}/join", async (Guid tableId, JoinTableBody? body, HttpContext http, TableService tables, CancellationToken ct) =>
        {
            var userId = http.User.GetUserId();
            await tables.JoinAsync(userId, PlayerIds.WalletAccountFor(userId), tableId, body?.Code, ct);
            return Results.Accepted();
        });

        group.MapPost("/tables/{tableId:guid}/leave", async (Guid tableId, HttpContext http, TableService tables, CancellationToken ct) =>
        {
            await tables.LeaveAsync(http.User.GetUserId(), tableId, ct);
            return Results.NoContent();
        });

        group.MapPost("/tables/{tableId:guid}/bots", async (Guid tableId, HttpContext http, TableService tables, CancellationToken ct) =>
        {
            await tables.AddBotAsync(http.User.GetUserId(), tableId, ct);
            return Results.NoContent();
        });

        group.MapDelete("/tables/{tableId:guid}/bots", async (Guid tableId, HttpContext http, TableService tables, CancellationToken ct) =>
        {
            await tables.RemoveBotAsync(http.User.GetUserId(), tableId, ct);
            return Results.NoContent();
        });

        group.MapPost("/tables/{tableId:guid}/start", async (Guid tableId, HttpContext http, TableService tables, CancellationToken ct) =>
        {
            await tables.StartAsync(http.User.GetUserId(), tableId, ct);
            return Results.NoContent();
        });

        // La jugada es un JSON propio de cada juego ({"type":"play","card":3}, etc.).
        group.MapPost("/tables/{tableId:guid}/action", async (Guid tableId, HttpContext http, TableService tables, CancellationToken ct) =>
        {
            using var reader = new StreamReader(http.Request.Body);
            var buffer = new char[MaxActionBytes + 1];
            var read = await reader.ReadBlockAsync(buffer, ct);
            if (read == 0 || read > MaxActionBytes)
            {
                throw new GamesDomainException(GamesError.InvalidAction, "La jugada es obligatoria y chica.");
            }

            await tables.ActAsync(http.User.GetUserId(), tableId, new string(buffer, 0, read), ct);
            return Results.NoContent();
        });

        group.MapGet("/history", async (HttpContext http, TableService tables, int? limit, CancellationToken ct) =>
            Results.Ok(await tables.GetHistoryAsync(gameId, http.User.GetUserId(), Math.Clamp(limit ?? 20, 1, MaxHistory), ct)));
    }
}
