using Casino.Modules.Games.Application;
using Casino.Modules.Games.Fairness;
using Microsoft.AspNetCore.Http;

namespace Casino.Modules.Games.Api;

/// <summary>Traduccion comun de los errores de dominio de los juegos a respuestas HTTP: cada juego la reutiliza en sus endpoints.</summary>
public static class GamesEndpointFilters
{
    public static async ValueTask<object?> MapDomainErrors(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(next);

        try
        {
            return await next(context);
        }
        catch (GamesDomainException ex)
        {
            var status = ex.Error switch
            {
                GamesError.RoundNotFound or GamesError.TableNotFound => StatusCodes.Status404NotFound,
                GamesError.BetKeyReused or GamesError.SettingsConflict or GamesError.BettingClosed or GamesError.RoundNotRunning
                    or GamesError.CrashedAlready or GamesError.BetNotActive or GamesError.NotYourTurn or GamesError.AlreadySeated or GamesError.TableClosed => StatusCodes.Status409Conflict,
                _ => StatusCodes.Status400BadRequest,
            };
            return Results.Problem(statusCode: status, title: ex.Error.ToString(), detail: ex.Message);
        }
        catch (FairnessDomainException ex)
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: ex.Error.ToString(), detail: ex.Message);
        }
    }
}
