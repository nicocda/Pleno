using Microsoft.AspNetCore;
using System.Security.Claims;
using Casino.Modules.Identity.Application;
using Casino.Modules.Identity.Domain;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Casino.Modules.Identity.Api;

/// <summary>
/// Endpoint de tokens (OAuth2): <c>password</c> (alias o email + clave, solo para el cliente propio) y <c>refresh_token</c> (con rotacion).
/// El access token es un JWT con <c>sub</c> (Guid de la cuenta), <c>preferred_username</c> (alias, nunca el email) y <c>role</c>.
/// </summary>
internal static class TokenEndpoint
{
    public const string ExternalCodeGrant = "urn:casino:grant_type:external_code";

    public static void Map(IEndpointRouteBuilder app) =>
        app.MapPost("/connect/token", HandleAsync)
            .RequireRateLimiting(IdentityModule.RateLimitPolicy)
            .AllowAnonymous()
            .WithTags("Identity")
            .ExcludeFromDescription();

    private static async Task<IResult> HandleAsync(HttpContext http, AccountService accounts, UserManager<PlayerUser> users, OneTimeTokenService oneTimeTokens, CancellationToken ct)
    {
        var request = http.GetOpenIddictServerRequest() ?? throw new InvalidOperationException("No se pudo leer la peticion OpenID Connect.");

        if (request.IsPasswordGrantType())
        {
            var user = await accounts.FindForLoginAsync(request.Username, ct);
            if (user is null || string.IsNullOrEmpty(request.Password))
            {
                return Invalid("Usuario o clave incorrectos.");
            }

            if (await users.IsLockedOutAsync(user))
            {
                return Invalid("Cuenta bloqueada temporalmente por intentos fallidos.");
            }

            if (!await users.CheckPasswordAsync(user, request.Password))
            {
                await users.AccessFailedAsync(user);
                return Invalid("Usuario o clave incorrectos.");
            }

            // Despues de comprobar la clave (no se revela si la cuenta existe): sin email verificado no se entra.
            if (!user.EmailConfirmed)
            {
                return Invalid("email_not_verified");
            }

            await users.ResetAccessFailedCountAsync(user);
            return await SignInAsync(user, users, request.GetScopes());
        }

        if (request.GrantType == ExternalCodeGrant)
        {
            // Codigo de un solo uso que dejo el login con Google (ver ExternalLoginEndpoints).
            var code = request.GetParameter("code")?.ToString();
            var user = await oneTimeTokens.ConsumeAsync(code, OneTimeTokenPurpose.ExternalLogin, ct) is { } userId
                ? await users.FindByIdAsync(userId.ToString())
                : null;
            if (user is null || await users.IsLockedOutAsync(user))
            {
                return Invalid("Codigo invalido o vencido.");
            }

            return await SignInAsync(user, users, request.GetScopes());
        }

        if (request.IsRefreshTokenGrantType())
        {
            var result = await http.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
            var user = result.Principal is { } principal ? await users.FindByIdAsync(principal.GetClaim(Claims.Subject) ?? string.Empty) : null;
            if (user is null || await users.IsLockedOutAsync(user))
            {
                return Invalid("La sesion ya no es valida.");
            }

            // Se reconstruye desde la base: si le cambiaron los roles o el alias, el token nuevo ya lo refleja.
            return await SignInAsync(user, users, result.Principal!.GetScopes());
        }

        return Results.BadRequest(new OpenIddictResponse { Error = Errors.UnsupportedGrantType });
    }

    private static async Task<IResult> SignInAsync(PlayerUser user, UserManager<PlayerUser> users, IEnumerable<string> scopes)
    {
        var identity = new ClaimsIdentity(
            OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
            Claims.PreferredUsername,
            Claims.Role);
        var principal = new ClaimsPrincipal(identity);
        principal.SetClaim(Claims.Subject, user.Id.ToString());
        principal.SetClaim(Claims.PreferredUsername, user.UserName);
        principal.SetClaims(Claims.Role, [.. await users.GetRolesAsync(user)]);
        principal.SetScopes(scopes);
        principal.SetResources(IdentityModule.Audience);
        principal.SetDestinations(claim => claim.Type is Claims.Subject or Claims.PreferredUsername or Claims.Role
            ? [Destinations.AccessToken]
            : []);
        return Results.SignIn(principal, authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    private static IResult Invalid(string description) =>
        Results.Forbid(
            new AuthenticationProperties(new Dictionary<string, string?>
            {
                [OpenIddictServerAspNetCoreConstants.Properties.Error] = Errors.InvalidGrant,
                [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = description,
            }),
            [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
}
