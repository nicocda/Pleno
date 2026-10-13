using Casino.Modules.Identity.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;

namespace Casino.Modules.Identity.Api;

public sealed record RegisterRequest(string? Alias, string? Email, string? Password);

internal static class AccountEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        // Alta de una cuenta de jugador. Responde 201 tambien si el email ya tenia cuenta (no se revela quien esta registrado).
        app.MapPost("/identity/register", async (RegisterRequest request, AccountService accounts, CancellationToken ct) =>
            {
                var result = await accounts.RegisterAsync(request.Alias, request.Email, request.Password, ct);
                return result.Outcome switch
                {
                    RegisterOutcome.Created => Results.StatusCode(StatusCodes.Status201Created),
                    RegisterOutcome.AliasTaken => Results.Conflict(new { error = "alias_taken" }),
                    RegisterOutcome.InvalidAlias => Results.BadRequest(new { error = "invalid_alias" }),
                    RegisterOutcome.InvalidEmail => Results.BadRequest(new { error = "invalid_email" }),
                    _ => Results.BadRequest(new { error = "weak_password", details = result.PasswordErrors }),
                };
            })
            .RequireRateLimiting(IdentityModule.RateLimitPolicy)
            .AllowAnonymous()
            .WithTags("Identity");
    }
}
