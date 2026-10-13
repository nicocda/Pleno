using Casino.Modules.Identity.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;

namespace Casino.Modules.Identity.Api;

public sealed record RegisterRequest(string? Alias, string? Email, string? Password);

public sealed record TokenRequest(string? Token);

public sealed record EmailRequest(string? Email);

public sealed record ResetPasswordRequest(string? Token, string? NewPassword);

internal static class AccountEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/identity")
            .RequireRateLimiting(IdentityModule.RateLimitPolicy)
            .AllowAnonymous()
            .WithTags("Identity");

        // Alta de una cuenta de jugador. Responde 201 tambien si el email ya tenia cuenta (no se revela quien esta registrado).
        group.MapPost("/register", async (RegisterRequest request, AccountService accounts, CancellationToken ct) =>
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
        });

        group.MapPost("/verify-email", async (TokenRequest request, AccountFlows flows, CancellationToken ct) =>
            await flows.VerifyEmailAsync(request.Token, ct) ? Results.NoContent() : Results.BadRequest(new { error = "invalid_token" }));

        // Las dos siguientes responden siempre 202: no revelan si el email tiene cuenta.
        group.MapPost("/resend-verification", async (EmailRequest request, AccountFlows flows, CancellationToken ct) =>
        {
            await flows.ResendVerificationAsync(request.Email, ct);
            return Results.Accepted();
        });

        group.MapPost("/forgot-password", async (EmailRequest request, AccountFlows flows, CancellationToken ct) =>
        {
            await flows.ForgotPasswordAsync(request.Email, ct);
            return Results.Accepted();
        });

        group.MapPost("/reset-password", async (ResetPasswordRequest request, AccountFlows flows, CancellationToken ct) =>
            await flows.ResetPasswordAsync(request.Token, request.NewPassword, ct) switch
            {
                ResetOutcome.Done => Results.NoContent(),
                ResetOutcome.WeakPassword => Results.BadRequest(new { error = "weak_password" }),
                _ => Results.BadRequest(new { error = "invalid_token" }),
            });
    }
}
