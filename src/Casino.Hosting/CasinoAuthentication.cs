using System.Security.Claims;
using System.Text.Json;
using Casino.BuildingBlocks;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Casino.Hosting;

/// <summary>
/// Autenticacion con tokens JWT emitidos por Keycloak (OpenID Connect). Ningun servicio ve contraseñas: cada uno valida por su cuenta la
/// firma, el emisor, el destinatario (audience) y la vigencia del token (no confia en que otro ya lo haya hecho).
/// </summary>
public static class CasinoAuthentication
{
    public const string Audience = "casino-api";

    /// <param name="queryTokenPaths">
    /// Rutas que aceptan el token en la query (<c>?access_token=</c>). Un navegador no puede poner cabeceras en un WebSocket,
    /// asi que solo el hub de tiempo real lo necesita; el resto de los servicios no pasa nada aca.
    /// </param>
    public static WebApplicationBuilder AddCasinoAuthentication(this WebApplicationBuilder builder, params string[] queryTokenPaths)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var authority = builder.Configuration["Authentication:Authority"];
        if (string.IsNullOrWhiteSpace(authority))
        {
            throw new InvalidOperationException(
                "Falta 'Authentication:Authority' (la URL del realm de Keycloak, por ejemplo http://localhost:8080/realms/casino). La inyecta Aspire.");
        }

        builder.Services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.Authority = authority;
                // En modo red local el emisor es la URL publica (https://<ip>:5173/realms/casino), que el servicio no alcanza (certificado
                // autofirmado): los metadatos se leen de Keycloak directo y el emisor del token se sigue validando contra Authority.
                if (builder.Configuration["Authentication:MetadataAddress"] is { Length: > 0 } metadataAddress)
                {
                    options.MetadataAddress = metadataAddress;
                }

                options.Audience = Audience;
                options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
                options.MapInboundClaims = false; // conserva "sub" y "preferred_username" tal cual vienen en el token
                options.TokenValidationParameters.NameClaimType = "preferred_username";
                options.TokenValidationParameters.RoleClaimType = ClaimTypes.Role;
                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        if (queryTokenPaths.Any(path => context.Request.Path.StartsWithSegments(path, StringComparison.OrdinalIgnoreCase))
                            && context.Request.Query["access_token"] is { Count: > 0 } token)
                        {
                            context.Token = token;
                        }

                        return Task.CompletedTask;
                    },
                    OnTokenValidated = context =>
                    {
                        // Sin un "sub" valido no hay forma de identificar al jugador: el token no sirve.
                        if (context.Principal is not { } principal || !principal.TryGetUserId(out _))
                        {
                            context.Fail("El token no trae un sub valido.");
                            return Task.CompletedTask;
                        }

                        MapRealmRoles(context.Principal);
                        return Task.CompletedTask;
                    },
                };
            });
        builder.Services.AddAuthorization();
        return builder;
    }

    /// <summary>Keycloak entrega los roles del realm dentro del claim JSON "realm_access": { "roles": [...] }. Se pasan a roles de .NET.</summary>
    private static void MapRealmRoles(ClaimsPrincipal? principal)
    {
        if (principal?.Identity is not ClaimsIdentity identity
            || principal.FindFirst("realm_access")?.Value is not { } realmAccess)
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(realmAccess);
            if (document.RootElement.TryGetProperty("roles", out var roles) && roles.ValueKind == JsonValueKind.Array)
            {
                foreach (var role in roles.EnumerateArray().Select(r => r.GetString()).OfType<string>())
                {
                    identity.AddClaim(new Claim(ClaimTypes.Role, role));
                }
            }
        }
        catch (JsonException)
        {
            // Un claim mal formado no concede ningun rol: queda sin permisos.
        }
    }
}
