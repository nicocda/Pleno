using System.Security.Claims;
using Casino.Modules.Identity.Application;
using Casino.Modules.Identity.Domain;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Casino.Modules.Identity.Api;

/// <summary>
/// Entrar con Google. El navegador va a <c>/identity/google/start</c>, Google devuelve a <c>/signin-google</c> y este servicio vuelve al front con un
/// codigo de un solo uso (2 minutos) que el front cambia por tokens en <c>/connect/token</c> (grant <see cref="TokenEndpoint.ExternalCodeGrant"/>).
/// Los tokens nunca viajan por la URL.
/// </summary>
internal static class ExternalLoginEndpoints
{
    public const string ExternalScheme = "External";
    private static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(2);

    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/identity")
            .RequireRateLimiting(IdentityModule.RateLimitPolicy)
            .AllowAnonymous()
            .WithTags("Identity");

        // El front pregunta que formas de entrar hay (el boton de Google solo aparece si esta configurado).
        group.MapGet("/providers", (IServiceProvider services) => Results.Ok(new { google = IsGoogleEnabled(services) }));

        group.MapGet("/google/start", (string returnUrl, HttpContext http, IOptions<IdentityLinksOptions> links) =>
        {
            if (!IsGoogleEnabled(http.RequestServices))
            {
                return Results.NotFound();
            }

            if (!IsAllowedReturn(returnUrl, links.Value))
            {
                return Results.BadRequest(new { error = "invalid_return_url" });
            }

            var properties = new AuthenticationProperties { RedirectUri = $"/identity/google/callback?returnUrl={Uri.EscapeDataString(returnUrl)}" };
            return Results.Challenge(properties, [GoogleDefaults.AuthenticationScheme]);
        });

        group.MapGet("/google/callback", async (string returnUrl, HttpContext http, AccountService accounts, OneTimeTokenService tokens, IOptions<IdentityLinksOptions> links, CancellationToken ct) =>
        {
            if (!IsAllowedReturn(returnUrl, links.Value))
            {
                return Results.BadRequest(new { error = "invalid_return_url" });
            }

            var result = await http.AuthenticateAsync(ExternalScheme);
            await http.SignOutAsync(ExternalScheme);
            var principal = result.Principal;
            var subject = principal?.FindFirstValue(ClaimTypes.NameIdentifier);
            var email = principal?.FindFirstValue(ClaimTypes.Email);
            // Solo se acepta un email que Google marca como verificado: con eso se puede vincular a una cuenta existente sin riesgo.
            if (string.IsNullOrEmpty(subject) || string.IsNullOrEmpty(email) || principal?.FindFirstValue("email_verified") != "true")
            {
                return Results.Redirect(Append(returnUrl, "error", "google_failed"));
            }

            var user = await accounts.FindOrCreateExternalAsync(GoogleDefaults.AuthenticationScheme, subject, email, ct);
            var code = await tokens.IssueAsync(user.Id, OneTimeTokenPurpose.ExternalLogin, CodeLifetime, ct);
            return Results.Redirect(Append(returnUrl, "code", code));
        });
    }

    public static bool IsGoogleEnabled(IServiceProvider services) =>
        services.GetService<IAuthenticationSchemeProvider>() is { } schemes
        && schemes.GetSchemeAsync(GoogleDefaults.AuthenticationScheme).GetAwaiter().GetResult() is not null;

    /// <summary>Solo se vuelve a un origen del front que este en la lista (evita usar el login como redireccion abierta).</summary>
    private static bool IsAllowedReturn(string? returnUrl, IdentityLinksOptions options) =>
        Uri.TryCreate(returnUrl, UriKind.Absolute, out var uri)
        && options.AllowedReturnOrigins.Contains(uri.GetLeftPart(UriPartial.Authority), StringComparer.OrdinalIgnoreCase);

    private static string Append(string url, string key, string value) =>
        url + (url.Contains('?', StringComparison.Ordinal) ? '&' : '?') + key + "=" + Uri.EscapeDataString(value);
}
