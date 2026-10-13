using Casino.BuildingBlocks;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Casino.Hosting;

/// <summary>
/// Autenticacion con tokens JWT emitidos por el servicio de identidad (OpenID Connect). Ningun servicio ve contraseñas: cada uno valida por su cuenta la
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
                "Falta 'Authentication:Authority' (la URL del servicio de identidad, por ejemplo http://localhost:8080). La inyecta Aspire.");
        }

        builder.Services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.Authority = authority;
                // En modo red local el emisor es la URL publica (https://<ip>:5173), que el servicio no alcanza (certificado
                // autofirmado): los metadatos se leen del servicio de identidad directo y el emisor del token se sigue validando contra Authority.
                if (builder.Configuration["Authentication:MetadataAddress"] is { Length: > 0 } metadataAddress)
                {
                    options.MetadataAddress = metadataAddress;
                }

                options.Audience = Audience;
                options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
                options.MapInboundClaims = false; // conserva "sub" y "preferred_username" tal cual vienen en el token
                options.TokenValidationParameters.NameClaimType = "preferred_username";
                options.TokenValidationParameters.RoleClaimType = "role"; // el servicio de identidad emite los roles en el claim plano "role"
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

                        return Task.CompletedTask;
                    },
                };
            });
        builder.Services.AddAuthorization();
        return builder;
    }
}
